// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;
using Crestron.DeviceDrivers.SDK.EntityModel.Data;
using Crestron.SimplSharp;

using TeslaPowerwall;
using TeslaPowerwallLibrary;
using TeslaPowerwallLibrary.Cloud;
using TeslaPowerwallLibrary.Models;

namespace TeslaPowerwall.CrestronDriver;

/// <summary>
/// Root Crestron Home Entity V2 Tesla Powerwall driver. Connects to the Tesla Owners (cloud) API via
/// <see cref="TeslaPowerwallLibrary.Powerwall"/> to monitor and control a single Powerwall energy site.
/// </summary>
/// <remarks>
/// This class is split across several partial-class files, grouped by concern:
/// <list type="bullet">
/// <item><description><c>PowerwallDriver.cs</c> (this file): construction, configuration, disposal, and shared helpers.</description></item>
/// <item><description><c>PowerwallDriver.Properties.cs</c>: Entity Model property declarations.</description></item>
/// <item><description><c>PowerwallDriver.Commands.cs</c>: Entity Model commands and events.</description></item>
/// <item><description><c>PowerwallDriver.Refresh.cs</c>: background polling, Tesla cloud communication, and Energy/Impact history logic.</description></item>
/// </list>
/// </remarks>
public sealed partial class TeslaPowerwallDriver : ReflectedAttributeDriverEntity
	{
	private const int DEFAULT_REFRESH_INTERVAL_SECONDS = 60;
	private const int MINIMUM_REFRESH_INTERVAL_SECONDS = 30;
	private const int MAXIMUM_REFRESH_INTERVAL_SECONDS = 3600;

	// Statically-declared row count for the Energy page's per-sub-period breakdown (see EnergyBreakdownVisible
	// and BuildEnergyRows): the schema has no repeating/templated row control, so every row from 1 to this
	// count is declared as its own set of properties/UI controls, and BuildEnergyRows hides whichever ones
	// the currently selected period does not need. 24 covers the largest case (Day -> 24 hourly rows).
	private const int ENERGY_ROW_COUNT = 24;

	private readonly DriverControllerLogger _logger;
	private readonly string _logControllerId;
	private readonly UiDefinitionProperty _uiDefinition;
	private readonly object _syncLock = new ();
	private readonly object _stateLock = new ();

	private CancellationTokenSource _refreshCancellationTokenSource;
	private int _refreshInProgress;
	private TeslaPowerwallLibrary.Powerwall _client;
	private string _siteName = string.Empty;

	// The actual calendar date/time that currently defines the Energy/Impact period being displayed (not
	// an offset or count). Reinterpreted under whichever period type (Day/Week/Month/Year) is currently
	// selected, so switching period types deliberately does not reset it: the same underlying date just
	// becomes the containing week/month/year, and so on.
	private DateTimeOffset _energyPeriodAnchorDate = DateTimeOffset.Now;
	private DateTimeOffset _impactPeriodAnchorDate = DateTimeOffset.Now;

	// True while the Energy/Impact page is following the live current period (e.g. today, for Day) rather
	// than a historical one the user navigated back to. Set explicitly by navigation - Back always leaves
	// the current period, Forward returns to it once it is reached - rather than inferred by comparing the
	// anchor date to "now" on every refresh, which cannot tell "still live, a new day just began" apart
	// from "deliberately parked on a past date" once the calendar bucket rolls over. While true, the anchor
	// date is re-pinned to DateTimeOffset.Now on every refresh so Today/This Week/etc. track real time
	// across a normal midnight rollover with no other special-cased handling.
	private bool _energyPeriodIsCurrent = true;
	private bool _impactPeriodIsCurrent = true;

	private string _email = string.Empty;
	private string _refreshToken = string.Empty;
	private string _siteId = string.Empty;
	private int _refreshIntervalSeconds = DEFAULT_REFRESH_INTERVAL_SECONDS;

	private string _pendingEmail = string.Empty;
	private string _pendingRefreshToken = string.Empty;
	private string _pendingSiteId = string.Empty;
	private int _pendingRefreshIntervalSeconds = DEFAULT_REFRESH_INTERVAL_SECONDS;

	// Sticky "last known state" trackers used to fire the one-shot transition events below only on a
	// genuine edge (not on every poll while a condition remains true, and not on the very first poll
	// after startup/reconnect, when there is no established baseline to compare against).
	private bool? _gridBackupActive;
	private bool? _stormWatchActive;
	private bool? _batteryReserveLowActive;
	private bool? _batteryFullyChargedActive;

	/// <summary>
	/// Initializes a new instance of the <see cref="TeslaPowerwallDriver"/> class.
	/// </summary>
	/// <param name="creationArgs">The Crestron runtime creation arguments.</param>
	/// <param name="resources">The driver implementation resources resolved by the SDK.</param>
	public TeslaPowerwallDriver (DriverControllerCreationArgs creationArgs, DriverImplementationResources resources)
		: base (DriverController.RootControllerId)
		{
		_logger = creationArgs.Logger;
		_logControllerId = creationArgs.DriverId;

		var configurationArgs = DataDrivenConfigurationControllerArgs.FromResources (creationArgs, resources, ControllerId);
		ConfigurationController = new DelegateDataDrivenConfigurationController (configurationArgs, ApplyConfigurationItems, null, null);

		_uiDefinition = UiDefinitionProperty.LoadFromDirectoryIfExists (creationArgs.DriverDataDirectoryPath, resources.InitLogger, LogEntryLevel.Error);
		if (_uiDefinition != null)
			{
			AddProperty (this, UiDefinitionProperty.Name, _uiDefinition);
			}

		try
			{
			AddCommand (this, ExtensionDoCommandExecutor.CommandName, new ExtensionDoCommandExecutor (GetCommand, resources.Logger));
			AddCommand (this, ExtensionSetPropertyValueExecutor.CommandName, new ExtensionSetPropertyValueExecutor (GetCommand, resources.Logger));
			}
		catch (Exception ex)
			{
			LogWarning ("Failed to register extension UI command helpers: " + ex.Message);
			}

		DeviceLabel = "Tesla Powerwall";
		TileDisplay = "--";
		TileIcon = "icBatteryLow";
		BatteryLevelDisplay = "--";
		OperationModeDisplay = "--";
		SolarPowerDisplay = "--";
		GridPowerDisplay = "--";
		LoadPowerDisplay = "--";
		GridExportModeDisplay = "--";
		SiteNameDisplay = "--";
		StatusSummary = "Waiting for configuration";
		BackupReservePercent = 0;
		GridChargingEnabled = false;
		OperationMode = "self_consumption";
		GridExportMode = "battery_ok";
		StormWatchEnabled = false;
		EnergyPeriod = "day";
		EnergyPeriodOffsetFormat = BuildPeriodOffsetText (HistoryPeriod.Day, _energyPeriodAnchorDate, isCurrentPeriod: true);
		EnergyPeriodOffsetVisible = true;
		EnergyPeriodForwardEnabled = false;
		EnergyType = "solar";
		EnergyPeriodLabel = "--";
		EnergyValueDisplay = "--";
		EnergyDetailDisplay = "--";
		EnergySummaryDisplay = "--";
		EnergyContentEnabled = true;
		EnergyBreakdownVisible = false;
		ApplyEnergyRows (Array.Empty<(string, string, string)> ());
		ImpactPeriod = "day";
		ImpactPeriodOffsetFormat = BuildPeriodOffsetText (HistoryPeriod.Day, _impactPeriodAnchorDate, isCurrentPeriod: true);
		ImpactPeriodOffsetVisible = true;
		ImpactPeriodForwardEnabled = false;
		ImpactPeriodLabel = "--";
		SelfPoweredPercent = 0;
		ImpactHomeUsageDisplay = "--";
		ImpactGridUsageDisplay = "--";
		ImpactSummaryDisplay = "--";
		ImpactContentEnabled = true;

		TryPublishUiDefinition ();
		}

	internal DataDrivenConfigurationController ConfigurationController
		{
		get;
		}

	/// <inheritdoc/>
	public override void Dispose ()
		{
		StopRefreshLoop ();
		base.Dispose ();
		}

	private ConfigurationItemErrors ApplyConfigurationItems (
		DataDrivenConfigurationController.ApplyConfigurationAction action,
		string stepId,
		IDictionary<string, DriverEntityValue?> values)
		{
		if (action == DataDrivenConfigurationController.ApplyConfigurationAction.ClearValues)
			{
			StopRefreshLoop ();
			_email = string.Empty;
			_refreshToken = string.Empty;
			_siteId = string.Empty;
			_refreshIntervalSeconds = DEFAULT_REFRESH_INTERVAL_SECONDS;
			_pendingEmail = string.Empty;
			_pendingRefreshToken = string.Empty;
			_pendingSiteId = string.Empty;
			_pendingRefreshIntervalSeconds = DEFAULT_REFRESH_INTERVAL_SECONDS;
			_siteName = string.Empty;
			_gridBackupActive = null;
			_stormWatchActive = null;
			_batteryReserveLowActive = null;
			_batteryFullyChargedActive = null;
			SetUnavailableState ("Configuration cleared");
			return null;
			}

		// Email is a label only in cloud mode: Tesla cloud authentication is entirely token-based, so it is
		// never required or validated here, per driver design (matches the manifest's Required: false).
		// AccessToken is intentionally not configured: PowerwallOptions.AccessToken is optional, and the library
		// obtains (and, in NoCloudTokenPersistence mode, only ever surfaces via CloudTokensRefreshed) a fresh access
		// token from RefreshToken alone on every connect, so RefreshToken is the only credential the driver needs.
		string email = GetString (values, "Email") ?? _pendingEmail;
		string refreshToken = GetString (values, "RefreshToken") ?? _pendingRefreshToken;
		string siteId = GetString (values, "SiteId") ?? _pendingSiteId;
		int refreshInterval = GetInteger (values, "RefreshIntervalSeconds") ?? _pendingRefreshIntervalSeconds;

		_pendingEmail = email ?? string.Empty;
		_pendingRefreshToken = refreshToken ?? string.Empty;
		_pendingSiteId = siteId ?? string.Empty;
		_pendingRefreshIntervalSeconds = refreshInterval;

		var errors = new Dictionary<string, string> (StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace (refreshToken))
			{
			errors["RefreshToken"] = "Tesla refresh token is required.";
			}

		if (refreshInterval < MINIMUM_REFRESH_INTERVAL_SECONDS || refreshInterval > MAXIMUM_REFRESH_INTERVAL_SECONDS)
			{
			errors["RefreshIntervalSeconds"] = "Refresh interval must be between 30 and 3600 seconds.";
			}

		if (errors.Count > 0)
			{
			return new ConfigurationItemErrors (errors, "Correct the configuration values and retry.");
			}

		_email = email ?? string.Empty;
		_refreshToken = refreshToken;
		_siteId = siteId ?? string.Empty;
		_refreshIntervalSeconds = refreshInterval;
		_siteName = string.Empty;
		SiteNameDisplay = "--";

		StartRefreshLoop ();
		return null;
		}

	private static string GetString (IDictionary<string, DriverEntityValue?> values, string key)
		{
		if (values == null || !values.TryGetValue (key, out DriverEntityValue? value) || !value.HasValue)
			{
			return null;
			}

		return value.Value.ToString ();
		}

	private static int? GetInteger (IDictionary<string, DriverEntityValue?> values, string key)
		{
		string raw = GetString (values, key);
		if (string.IsNullOrWhiteSpace (raw))
			{
			return null;
			}

		return int.TryParse (raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
		}

	private void SetAndNotify (string propertyId, string value, ref string backingField)
		{
		value ??= string.Empty;
		lock (_stateLock)
			{
			if (string.Equals (backingField, value, StringComparison.Ordinal))
				{
				return;
				}

			backingField = value;
			}

		NotifyPropertyChanged (propertyId, new DriverEntityValue (value));
		}

	private void SetAndNotify (string propertyId, bool value, ref bool backingField)
		{
		lock (_stateLock)
			{
			if (backingField == value)
				{
				return;
				}

			backingField = value;
			}

		NotifyPropertyChanged (propertyId, new DriverEntityValue (value));
		}

	private void SetAndNotify (string propertyId, int value, ref int backingField)
		{
		lock (_stateLock)
			{
			if (backingField == value)
				{
				return;
				}

			backingField = value;
			}

		NotifyPropertyChanged (propertyId, new DriverEntityValue (value));
		}

	private void TryPublishUiDefinition ()
		{
		if (_uiDefinition == null)
			{
			return;
			}

		DriverEntityValue? uiDefinitionValue = _uiDefinition.GetValue (null, null);
		if (uiDefinitionValue.HasValue)
			{
			NotifyPropertyChanged (UiDefinitionProperty.Name, uiDefinitionValue.Value);
			}
		}

	[Conditional ("DEBUG")]
	private void DebugLog (string message) => LogInformation (message);

	private void LogWarning (string message) => _logger?.Log (_logControllerId, LogEntryLevel.Warning, message);

	private void LogError (string message) => _logger?.Log (_logControllerId, LogEntryLevel.Error, message);

	private void LogInformation (string message) => _logger?.Log (_logControllerId, LogEntryLevel.Info, message);
	}
