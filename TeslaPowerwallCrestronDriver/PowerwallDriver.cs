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
public sealed class TeslaPowerwallDriver : ReflectedAttributeDriverEntity
	{
	private const int DEFAULT_REFRESH_INTERVAL_SECONDS = 60;
	private const int MINIMUM_REFRESH_INTERVAL_SECONDS = 30;
	private const int MAXIMUM_REFRESH_INTERVAL_SECONDS = 3600;

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

	#region Refresh loop

	private void StartRefreshLoop ()
		{
		StopRefreshLoop ();
		lock (_syncLock)
			{
			_refreshCancellationTokenSource = new CancellationTokenSource ();
			_ = Task.Run (() => RefreshLoopAsync (_refreshCancellationTokenSource.Token));
			}
		}

	private void StopRefreshLoop ()
		{
		lock (_syncLock)
			{
			try
				{
				_refreshCancellationTokenSource?.Cancel ();
				}
			catch
				{
				}

			_refreshCancellationTokenSource?.Dispose ();
			_refreshCancellationTokenSource = null;
			}

		DisposeClient ();
		}

	private void DisposeClient ()
		{
		TeslaPowerwallLibrary.Powerwall client;
		lock (_stateLock)
			{
			client = _client;
			_client = null;
			}

		if (client != null)
			{
			client.CloudTokensRefreshed -= OnCloudTokensRefreshed;
			client.Dispose ();
			}
		}

	private async Task RefreshLoopAsync (CancellationToken cancellationToken)
		{
		try
			{
			DebugLog ("RefreshLoop: starting initial connect and refresh.");
			await RunPollAsync (cancellationToken).ConfigureAwait (false);
			}
		catch (OperationCanceledException)
			{
			return;
			}
		catch (Exception ex)
			{
			SetUnavailableState ("Unable to connect: " + ex.Message);
			LogError ("Initial Tesla cloud connection failed: " + ex.Message);
			}

		while (!cancellationToken.IsCancellationRequested)
			{
			try
				{
				await Task.Delay (TimeSpan.FromSeconds (_refreshIntervalSeconds), cancellationToken).ConfigureAwait (false);
				}
			catch (OperationCanceledException)
				{
				break;
				}

			try
				{
				DebugLog ("RefreshLoop: starting scheduled refresh.");
				await RunPollAsync (cancellationToken).ConfigureAwait (false);
				}
			catch (OperationCanceledException)
				{
				break;
				}
			catch (Exception ex)
				{
				LogError ("Tesla cloud refresh failed: " + ex.Message);
				}
			}
		}

	private async Task RunPollAsync (CancellationToken cancellationToken)
		{
		if (System.Threading.Interlocked.CompareExchange (ref _refreshInProgress, 1, 0) != 0)
			{
			DebugLog ("RunPollAsync: refresh already in progress; skipping new request.");
			return;
			}

		try
			{
			DebugLog ("RunPollAsync: entered refresh execution.");
			await ConnectAndPollAsync (cancellationToken).ConfigureAwait (false);
			}
		finally
			{
			DebugLog ("RunPollAsync: leaving refresh execution.");
			System.Threading.Interlocked.Exchange (ref _refreshInProgress, 0);
			}
		}

	private async Task ConnectAndPollAsync (CancellationToken cancellationToken)
		{
		TeslaPowerwallLibrary.Powerwall client = GetOrCreateClient ();

		if (!client.IsClientConnected)
			{
			bool connected;
			try
				{
				connected = await client.ConnectAsync (cancellationToken).ConfigureAwait (false);
				}
			catch (PowerwallCloudNoTeslaAuthFileException ex)
				{
				SetUnavailableState ("Invalid or missing Tesla tokens");
				LogError ("Tesla cloud authentication failed: " + ex.Message);
				return;
				}
			catch (PowerwallInvalidConfigurationException ex)
				{
				SetUnavailableState ("Configuration error");
				LogError ("Tesla cloud configuration error: " + ex.Message);
				return;
				}
			catch (PowerwallException ex)
				{
				SetUnavailableState ("Unable to connect to Tesla cloud");
				LogError ("Tesla cloud connection error: " + ex.Message);
				return;
				}

			if (!connected)
				{
				SetUnavailableState ("Unable to connect to Tesla cloud");
				return;
				}

			OnlineIndicatorIsOnline = true;
			await ResolveConfiguredSiteAsync (client, cancellationToken).ConfigureAwait (false);
			await RefreshSiteNameAsync (client, cancellationToken).ConfigureAwait (false);
			}

		await PollAsync (client, cancellationToken).ConfigureAwait (false);
		}

	private static bool IsNumericSiteId (string siteId) =>
		!string.IsNullOrEmpty (siteId) && siteId.All (char.IsDigit);

	private TeslaPowerwallLibrary.Powerwall GetOrCreateClient ()
		{
		lock (_stateLock)
			{
			if (_client != null)
				{
				return _client;
				}

			// AccessToken is intentionally omitted: it is optional on PowerwallOptions, and the library obtains
			// a fresh one from RefreshToken alone on every connect, so there is nothing for the driver to supply.
			// SiteId is only passed here when it is purely numeric (a real Tesla energy site id); an alphanumeric
			// site name is resolved to its id after connecting, in ResolveConfiguredSiteAsync.
			var options = new TeslaPowerwallLibrary.PowerwallOptions
				{
				CloudMode = true,
				NoCloudTokenPersistence = true,
				Email = string.IsNullOrWhiteSpace (_email) ? TeslaPowerwallLibrary.Constants.DEFAULT_EMAIL : _email,
				RefreshToken = _refreshToken,
				SiteId = IsNumericSiteId (_siteId) ? _siteId : null,
				Timeout = TimeSpan.FromSeconds (15),
				};

			var client = new TeslaPowerwallLibrary.Powerwall (options);
			client.CloudTokensRefreshed += OnCloudTokensRefreshed;
			_client = client;
			return client;
			}
		}

	private async Task ResolveConfiguredSiteAsync (TeslaPowerwallLibrary.Powerwall client, CancellationToken cancellationToken)
		{
		// Numeric SiteId values are already passed straight through to PowerwallOptions.SiteId in
		// GetOrCreateClient and selected during ConnectAsync, so only an alphanumeric name needs resolving here.
		if (string.IsNullOrWhiteSpace (_siteId) || IsNumericSiteId (_siteId))
			{
			return;
			}

		try
			{
			IReadOnlyList<CloudSite> sites = await client.GetSitesAsync (cancellationToken).ConfigureAwait (false);
			string trimmed = _siteId.Trim ();
			CloudSite match = sites.FirstOrDefault (site => string.Equals (site.SiteId, trimmed, StringComparison.Ordinal))
				?? sites.FirstOrDefault (site => string.Equals (site.SiteName?.Trim (), trimmed, StringComparison.OrdinalIgnoreCase));
			if (match == null)
				{
				LogWarning ($"Configured Tesla site name '{_siteId}' was not found; using the account's default site.");
				return;
				}

			await client.ChangeSiteAsync (match.SiteId, cancellationToken).ConfigureAwait (false);
			}
		catch (PowerwallException ex)
			{
			LogWarning ("Unable to resolve configured Tesla site name: " + ex.Message);
			}
		}

	private async Task RefreshSiteNameAsync (TeslaPowerwallLibrary.Powerwall client, CancellationToken cancellationToken)
		{
		try
			{
			string siteName = await client.SiteNameAsync (cancellationToken).ConfigureAwait (false);
			_siteName = siteName ?? string.Empty;
			SiteNameDisplay = string.IsNullOrWhiteSpace (siteName) ? "--" : siteName;
			}
		catch (PowerwallException ex)
			{
			LogWarning ("Unable to retrieve Tesla site name: " + ex.Message);
			}
		}

	private async Task PollAsync (TeslaPowerwallLibrary.Powerwall client, CancellationToken cancellationToken)
		{
		try
			{
			double? level = await client.LevelAsync (scale: true, cancellationToken: cancellationToken).ConfigureAwait (false);
			PowerSnapshot power = await client.PowerAsync (cancellationToken).ConfigureAwait (false);
			GridStatus? gridStatus = await client.GridStatusAsync (cancellationToken).ConfigureAwait (false);
			double? reserve = await client.GetReserveAsync (cancellationToken: cancellationToken).ConfigureAwait (false);
			string mode = await client.GetModeAsync (cancellationToken: cancellationToken).ConfigureAwait (false);
			bool? gridCharging = await client.GetGridChargingAsync (cancellationToken: cancellationToken).ConfigureAwait (false);
			string gridExport = await client.GetGridExportAsync (cancellationToken: cancellationToken).ConfigureAwait (false);
			bool? stormWatch = await client.GetStormWatchAsync (cancellationToken: cancellationToken).ConfigureAwait (false);

			ApplyPolledState (level, power, gridStatus, reserve, mode, gridCharging, gridExport, stormWatch);
			await RefreshEnergyAndImpactAsync (client, cancellationToken).ConfigureAwait (false);
			OnlineIndicatorIsOnline = true;
			ReadyIndicatorIsReady = true;
			StatusSummary = BuildUpdatedSummary ();
			}
		catch (PowerwallException ex)
			{
			ReadyIndicatorIsReady = false;
			StatusSummary = "Update failed: " + ex.Message;
			LogWarning ("Tesla cloud poll failed: " + ex.Message);
			}
		}

	private void ApplyPolledState (
		double? level,
		PowerSnapshot power,
		GridStatus? gridStatus,
		double? reserve,
		string mode,
		bool? gridCharging,
		string gridExport,
		bool? stormWatch)
		{
		BatteryLevelDisplay = level.HasValue
			? string.Format (
				CultureInfo.InvariantCulture,
				"{0}% ({1})",
				Math.Round (level.Value, MidpointRounding.AwayFromZero).ToString ("0", CultureInfo.InvariantCulture),
				FormatBatteryChargeState (power?.Battery))
			: "--";
		if (reserve.HasValue)
			{
			BackupReservePercent = (int) Math.Round (reserve.Value, MidpointRounding.AwayFromZero);
			}

		OperationModeDisplay = "Mode: " + FormatModeDisplay (mode);
		if (!string.IsNullOrEmpty (mode))
			{
			OperationMode = mode;
			}

		if (gridCharging.HasValue)
			{
			GridChargingEnabled = gridCharging.Value;
			}

		GridExportModeDisplay = "Export: " + FormatGridExportDisplay (gridExport);
		if (!string.IsNullOrEmpty (gridExport))
			{
			GridExportMode = gridExport;
			}

		if (stormWatch.HasValue)
			{
			StormWatchEnabled = stormWatch.Value;
			}

		RaiseStateTransitionEvents (level, gridStatus, stormWatch);

		bool gridExportEnabled = !string.Equals (gridExport, "never", StringComparison.OrdinalIgnoreCase);

		// The grid is considered offline (and the Powerwall is acting as the backup source) whenever the
		// system reports an islanded/faulted/waiting state; GridStatus.Up (connected) and Syncing
		// (transitioning) are not treated as an outage.
		bool gridOffline = gridStatus == GridStatus.Down;

		SolarPowerDisplay = FormatPower (power?.Solar);
		GridPowerDisplay = FormatGridPower (power?.Site, gridExportEnabled);
		LoadPowerDisplay = gridOffline
			? FormatPower (power?.Load) + " (Grid Offline)"
			: FormatPower (power?.Load);

		// Home-page tile: line 1 is current home consumption (with a "BKUP" suffix when the grid is
		// offline and the Powerwall is running the house as the backup source); line 2 is a compact
		// breakdown of which sources are currently contributing (non-contributing sources, e.g. a
		// charging battery or an exporting grid connection, are omitted). The tile status schema is a
		// plain string that wraps when it overflows, but there is no confirmed behavior for embedded
		// newlines forcing a break; "\n" is used here as the best available attempt at a deterministic
		// two-line layout.
		string tileConsumption = FormatPower (power?.Load) + (gridOffline ? " BKUP" : string.Empty);
		TileDisplay = tileConsumption + "\n" + BuildTileSourceSummary (power);
		TileIcon = DeterminePrimarySourceIcon (power);
		}

	private async Task RefreshEnergyAndImpactAsync (TeslaPowerwallLibrary.Powerwall client, CancellationToken cancellationToken)
		{
		await RefreshEnergyAsync (client, cancellationToken).ConfigureAwait (false);
		await RefreshImpactAsync (client, cancellationToken).ConfigureAwait (false);
		}

	private async Task RefreshEnergyAsync (TeslaPowerwallLibrary.Powerwall client, CancellationToken cancellationToken)
		{
		HistoryPeriod period = ParseHistoryPeriod (EnergyPeriod);
		if (_energyPeriodIsCurrent)
			{
			_energyPeriodAnchorDate = DateTimeOffset.Now;
			}

		(string startDate, string endDate) = ResolvePeriodRange (period, _energyPeriodAnchorDate);

		try
			{
			IReadOnlyList<EnergyHistoryPoint> points = await client.GetEnergyCalendarHistoryAsync (
				period, startDate: startDate, endDate: endDate, cancellationToken: cancellationToken).ConfigureAwait (false);

			EnergyPeriodLabel = BuildPeriodLabel (period, _energyPeriodAnchorDate);
			EnergyPeriodOffsetFormat = BuildPeriodOffsetText (period, _energyPeriodAnchorDate, _energyPeriodIsCurrent);
			EnergyPeriodForwardEnabled = !_energyPeriodIsCurrent;

			if (points.Count == 0)
				{
				EnergyValueDisplay = "--";
				EnergyDetailDisplay = "--";
				EnergySummaryDisplay = "--";
				return;
				}

			double solarKwh = points.Sum (p => p.SolarKwh);
			double homeKwh = points.Sum (p => p.HomeKwh);
			double fromGridKwh = points.Sum (p => p.FromGridKwh);
			double toGridKwh = points.Sum (p => p.ToGridKwh);
			double batteryChargeKwh = points.Sum (p => p.BatteryChargeKwh);
			double batteryDischargeKwh = points.Sum (p => p.BatteryDischargeKwh);

			ApplyEnergyDisplays (EnergyType, solarKwh, homeKwh, fromGridKwh, toGridKwh, batteryChargeKwh, batteryDischargeKwh);
			}
		catch (PowerwallException ex)
			{
			LogWarning ("Unable to retrieve Tesla energy history: " + ex.Message);
			}
		finally
			{
			// Re-enables the data controls once this refresh has resolved (successfully, empty, or failed),
			// regardless of what triggered it. Only the user-initiated commands ever set this false, so a
			// routine background poll finding it already true here is a no-op.
			EnergyContentEnabled = true;
			}
		}

	// Derives the Energy page's three display lines from the selected energy type, using only the sums
	// exposed by EnergyHistoryPoint's computed kWh properties (no Tesla fields are invented or re-attributed).
	private void ApplyEnergyDisplays (
		string energyType,
		double solarKwh,
		double homeKwh,
		double fromGridKwh,
		double toGridKwh,
		double batteryChargeKwh,
		double batteryDischargeKwh)
		{
		// Grid Export is a solar-view line that only means something when the site is actually allowed
		// to export; when the mode is "never" the value is always zero, so the line is cleared instead.
		bool gridExportEnabled = !string.Equals (GridExportMode, "never", StringComparison.OrdinalIgnoreCase);

		switch (energyType)
			{
			case "powerwall":
				EnergyValueDisplay = FormatKwh ("Discharged", batteryDischargeKwh);
				EnergyDetailDisplay = FormatKwh ("Charged", batteryChargeKwh);
				EnergySummaryDisplay = FormatNetKwh ("Net", batteryDischargeKwh - batteryChargeKwh);
				break;

			case "grid":
				EnergyValueDisplay = FormatKwh ("Imported", fromGridKwh);
				EnergyDetailDisplay = FormatKwh ("Exported", toGridKwh);
				EnergySummaryDisplay = FormatNetKwh ("Net", fromGridKwh - toGridKwh);
				break;

			case "house":
				EnergyValueDisplay = FormatKwh ("Used", homeKwh);
				EnergyDetailDisplay = string.Format (CultureInfo.InvariantCulture, "Solar: {0:0.0} kWh  Grid: {1:0.0} kWh", solarKwh, fromGridKwh);
				EnergySummaryDisplay = FormatKwh ("Powerwall", batteryDischargeKwh);
				break;

			case "solar":
			default:
				EnergyValueDisplay = FormatKwh ("Produced", solarKwh);
				EnergyDetailDisplay = FormatKwh ("Home Usage", homeKwh);
				EnergySummaryDisplay = gridExportEnabled ? FormatKwh ("Grid Export", toGridKwh) : string.Empty;
				break;
			}
		}

	private async Task RefreshImpactAsync (TeslaPowerwallLibrary.Powerwall client, CancellationToken cancellationToken)
		{
		HistoryPeriod period = ParseHistoryPeriod (ImpactPeriod);
		if (_impactPeriodIsCurrent)
			{
			_impactPeriodAnchorDate = DateTimeOffset.Now;
			}

		(string startDate, string endDate) = ResolvePeriodRange (period, _impactPeriodAnchorDate);

		try
			{
			IReadOnlyList<SelfConsumptionHistoryPoint> points = await client.GetSelfConsumptionCalendarHistoryAsync (
				period, startDate: startDate, endDate: endDate, cancellationToken: cancellationToken).ConfigureAwait (false);

			ImpactPeriodLabel = BuildPeriodLabel (period, _impactPeriodAnchorDate);
			ImpactPeriodOffsetFormat = BuildPeriodOffsetText (period, _impactPeriodAnchorDate, _impactPeriodIsCurrent);
			ImpactPeriodForwardEnabled = !_impactPeriodIsCurrent;

			if (points.Count == 0)
				{
				SelfPoweredPercent = 0;
				ImpactHomeUsageDisplay = "--";
				ImpactGridUsageDisplay = "--";
				ImpactSummaryDisplay = "--";
				return;
				}

			double solarPercent = points.Average (p => p.SolarPercentage);
			double batteryPercent = points.Average (p => p.BatteryPercentage);
			double selfPowered = Math.Min (100, Math.Max (0, solarPercent + batteryPercent));
			double gridPercent = Math.Min (100, Math.Max (0, 100 - selfPowered));

			SelfPoweredPercent = (int) Math.Round (selfPowered, MidpointRounding.AwayFromZero);
			ImpactHomeUsageDisplay = string.Format (CultureInfo.InvariantCulture, "Solar: {0:0}%  Pwall: {1:0}%", solarPercent, batteryPercent);
			ImpactGridUsageDisplay = string.Format (CultureInfo.InvariantCulture, "Grid: {0:0}%", gridPercent);
			ImpactSummaryDisplay = string.Format (CultureInfo.InvariantCulture, "{0}% Self", SelfPoweredPercent);
			}
		catch (PowerwallException ex)
			{
			LogWarning ("Unable to retrieve Tesla self-consumption history: " + ex.Message);
			}
		finally
			{
			// Re-enables the data controls once this refresh has resolved (successfully, empty, or failed),
			// regardless of what triggered it. Only the user-initiated commands ever set this false, so a
			// routine background poll finding it already true here is a no-op.
			ImpactContentEnabled = true;
			}
		}

	// Applies a user-initiated Energy period/anchor change (period type switch, or back/forward navigation)
	// to the label, offset button text, and forward button state immediately, ahead of the Tesla cloud
	// refresh that will supply the new period's actual figures. The data controls are disabled in the
	// meantime, via EnergyContentEnabled, so stale figures for the previous period are never shown next to
	// a label/button that has already moved on; RefreshEnergyAsync re-enables them once it completes.
	private void UpdateEnergyPeriodDisplayImmediate ()
		{
		HistoryPeriod period = ParseHistoryPeriod (EnergyPeriod);
		EnergyPeriodLabel = BuildPeriodLabel (period, _energyPeriodAnchorDate);
		EnergyPeriodOffsetFormat = BuildPeriodOffsetText (period, _energyPeriodAnchorDate, _energyPeriodIsCurrent);
		EnergyPeriodForwardEnabled = !_energyPeriodIsCurrent;
		EnergyContentEnabled = false;
		}

	// Impact counterpart of UpdateEnergyPeriodDisplayImmediate; see there for the full rationale.
	private void UpdateImpactPeriodDisplayImmediate ()
		{
		HistoryPeriod period = ParseHistoryPeriod (ImpactPeriod);
		ImpactPeriodLabel = BuildPeriodLabel (period, _impactPeriodAnchorDate);
		ImpactPeriodOffsetFormat = BuildPeriodOffsetText (period, _impactPeriodAnchorDate, _impactPeriodIsCurrent);
		ImpactPeriodForwardEnabled = !_impactPeriodIsCurrent;
		ImpactContentEnabled = false;
		}

	private static string FormatKwh (string label, double kwh) =>
		string.Format (CultureInfo.InvariantCulture, "{0}: {1:0.0} kWh", label, kwh);

	private static string FormatNetKwh (string label, double kwh) =>
		string.Format (CultureInfo.InvariantCulture, "{0}: {1}{2:0.0} kWh", label, kwh >= 0 ? "+" : "-", Math.Abs (kwh));

	private static HistoryPeriod ParseHistoryPeriod (string period) => period switch
		{
		"week" => HistoryPeriod.Week,
		"month" => HistoryPeriod.Month,
		"year" => HistoryPeriod.Year,
		"lifetime" => HistoryPeriod.Lifetime,
		_ => HistoryPeriod.Day
		};

	// True when the two given dates fall within the same instance of the given period (e.g. the same
	// calendar day, for Day; the same calendar week, for Week; and so on). Lifetime has only ever one
	// instance, so it is always considered the same bucket.
	private static bool IsSamePeriodBucket (HistoryPeriod period, DateTimeOffset first, DateTimeOffset second)
		{
		switch (period)
			{
			case HistoryPeriod.Week:
				return StartOfWeek (first) == StartOfWeek (second);

			case HistoryPeriod.Month:
				return first.Year == second.Year && first.Month == second.Month;

			case HistoryPeriod.Year:
				return first.Year == second.Year;

			case HistoryPeriod.Lifetime:
				return true;

			default:
				return first.Date == second.Date;
			}
		}

	// Moves an anchor date one whole step of the given period type, backward (direction = -1) or forward
	// (direction = 1). Lifetime has no navigable instances, so the anchor is left unchanged for it.
	private static DateTimeOffset StepPeriod (HistoryPeriod period, DateTimeOffset anchor, int direction)
		{
		switch (period)
			{
			case HistoryPeriod.Week:
				return anchor.AddDays (7 * direction);

			case HistoryPeriod.Month:
				return anchor.AddMonths (direction);

			case HistoryPeriod.Year:
				return anchor.AddYears (direction);

			case HistoryPeriod.Lifetime:
				return anchor;

			default:
				return anchor.AddDays (direction);
			}
		}

	// Builds a human-readable label for the instance of the given period containing the anchor date,
	// e.g. "March 4, 2026", "Mar 2 - Mar 8, 2026", "March 2026", "2026", or "Lifetime".
	private static string BuildPeriodLabel (HistoryPeriod period, DateTimeOffset anchor)
		{
		switch (period)
			{
			case HistoryPeriod.Week:
				DateTimeOffset weekStart = StartOfWeek (anchor);
				DateTimeOffset weekEnd = weekStart.AddDays (7).AddSeconds (-1);
				return weekStart.Year == weekEnd.Year
					? string.Format (CultureInfo.CurrentCulture, "{0} - {1}", weekStart.ToString ("MMM d", CultureInfo.CurrentCulture), weekEnd.ToString ("MMM d, yyyy", CultureInfo.CurrentCulture))
					: string.Format (CultureInfo.CurrentCulture, "{0} - {1}", weekStart.ToString ("MMM d, yyyy", CultureInfo.CurrentCulture), weekEnd.ToString ("MMM d, yyyy", CultureInfo.CurrentCulture));

			case HistoryPeriod.Month:
				return anchor.ToString ("MMMM yyyy", CultureInfo.CurrentCulture);

			case HistoryPeriod.Year:
				return anchor.ToString ("yyyy", CultureInfo.CurrentCulture);

			case HistoryPeriod.Lifetime:
				return "Lifetime";

			default:
				return anchor.ToString ("MMMM d, yyyy", CultureInfo.CurrentCulture);
			}
		}

	// Resolves the RFC 3339 start/end window for the instance of the given period containing the anchor
	// date, in local time; mirrors the Tesla app's own calendar-aligned period boundaries. Lifetime has no
	// fixed window, so both bounds are null and the Tesla cloud returns the site's full history.
	private static (string StartDate, string EndDate) ResolvePeriodRange (HistoryPeriod period, DateTimeOffset anchor)
		{
		switch (period)
			{
			case HistoryPeriod.Week:
				DateTimeOffset weekStart = StartOfWeek (anchor);
				return (ToRfc3339 (weekStart), ToRfc3339 (weekStart.AddDays (7).AddSeconds (-1)));

			case HistoryPeriod.Month:
				DateTimeOffset monthStart = LocalMidnight (anchor.Year, anchor.Month, 1);
				return (ToRfc3339 (monthStart), ToRfc3339 (monthStart.AddMonths (1).AddSeconds (-1)));

			case HistoryPeriod.Year:
				DateTimeOffset yearStart = LocalMidnight (anchor.Year, 1, 1);
				return (ToRfc3339 (yearStart), ToRfc3339 (yearStart.AddYears (1).AddSeconds (-1)));

			case HistoryPeriod.Lifetime:
				return (null, null);

			default:
				DateTimeOffset dayStart = LocalMidnight (anchor.Year, anchor.Month, anchor.Day);
				return (ToRfc3339 (dayStart), ToRfc3339 (dayStart.AddDays (1).AddSeconds (-1)));
			}
		}

	// Builds the text shown by the Energy/Impact period button group in place of a raw offset number: the
	// current period always reads "Today"/"This Week"/"This Month"/"This Year", and earlier periods show
	// the actual date or date range instead - dd/MM (or dd/MM/yyyy when not the current year) for Day,
	// dd/MM-dd/MM for Week, "MMMM, yyyy" for Month, and yyyy for Year. Lifetime has no navigable instances,
	// so the control is hidden and this is never shown for it.
	private static string BuildPeriodOffsetText (HistoryPeriod period, DateTimeOffset anchor, bool isCurrentPeriod)
		{
		if (isCurrentPeriod)
			{
			return period switch
				{
				HistoryPeriod.Week => "This Week",
				HistoryPeriod.Month => "This Month",
				HistoryPeriod.Year => "This Year",
				HistoryPeriod.Lifetime => "Lifetime",
				_ => "Today"
				};
			}

		switch (period)
			{
			case HistoryPeriod.Week:
				DateTimeOffset weekStart = StartOfWeek (anchor);
				DateTimeOffset weekEnd = weekStart.AddDays (7).AddSeconds (-1);
				return string.Format (
					CultureInfo.InvariantCulture,
					"{0}-{1}",
					weekStart.ToString ("dd/MM", CultureInfo.InvariantCulture),
					weekEnd.ToString ("dd/MM", CultureInfo.InvariantCulture));

			case HistoryPeriod.Month:
				return anchor.ToString ("MMMM, yyyy", CultureInfo.CurrentCulture);

			case HistoryPeriod.Year:
				return anchor.ToString ("yyyy", CultureInfo.InvariantCulture);

			case HistoryPeriod.Lifetime:
				return "Lifetime";

			default:
				return anchor.Year == DateTimeOffset.Now.Year
					? anchor.ToString ("dd/MM", CultureInfo.InvariantCulture)
					: anchor.ToString ("dd/MM/yyyy", CultureInfo.InvariantCulture);
			}
		}

	// Weeks always run Sunday-Saturday, matching the Tesla app's convention regardless of the current culture.
	private static DateTimeOffset StartOfWeek (DateTimeOffset local)
		{
		int current = (int) local.DayOfWeek;
		DateTime day = local.Date.AddDays (-current);
		return LocalMidnight (day.Year, day.Month, day.Day);
		}

	private static DateTimeOffset LocalMidnight (int year, int month, int day)
		{
		var midnight = new DateTime (year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
		return new DateTimeOffset (midnight, TimeZoneInfo.Local.GetUtcOffset (midnight));
		}

	private static string ToRfc3339 (DateTimeOffset value) =>
		value.ToString ("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

	// Detects one-shot state transitions from the latest poll and raises the corresponding Entity Model
	// events. Each tracked condition uses a sticky nullable flag so the event fires exactly once per
	// genuine edge: not repeatedly while the condition remains true, and not on the first poll after
	// startup/reconnect (when there is no established baseline to compare the new reading against).
	private void RaiseStateTransitionEvents (double? level, GridStatus? gridStatus, bool? stormWatch)
		{
		if (gridStatus == GridStatus.Down)
			{
			if (_gridBackupActive != true)
				{
				if (_gridBackupActive.HasValue)
					{
					GridStatusChangedToBackup?.Invoke (this, EventArgs.Empty);
					}

				_gridBackupActive = true;
				}
			}
		else if (gridStatus == GridStatus.Up)
			{
			if (_gridBackupActive != false)
				{
				if (_gridBackupActive.HasValue)
					{
					GridStatusRestored?.Invoke (this, EventArgs.Empty);
					}

				_gridBackupActive = false;
				}
			}

		// GridStatus.Syncing (or unknown) is a transient in-between state: leave the sticky flag as-is
		// so the eventual restore to Up is still detected as a transition out of backup mode.

		if (stormWatch.HasValue)
			{
			if (stormWatch.Value)
				{
				if (_stormWatchActive != true)
					{
					if (_stormWatchActive.HasValue)
						{
						StormWatchActivated?.Invoke (this, EventArgs.Empty);
						}

					_stormWatchActive = true;
					}
				}
			else
				{
				if (_stormWatchActive != false)
					{
					if (_stormWatchActive.HasValue)
						{
						StormWatchDeactivated?.Invoke (this, EventArgs.Empty);
						}

					_stormWatchActive = false;
					}
				}
			}

		if (!level.HasValue)
			{
			return;
			}

		double roundedLevel = Math.Round (level.Value, MidpointRounding.AwayFromZero);

		bool reserveLow = roundedLevel <= BackupReservePercent;
		if (reserveLow)
			{
			if (_batteryReserveLowActive != true)
				{
				if (_batteryReserveLowActive.HasValue)
					{
					BatteryReserveLow?.Invoke (this, EventArgs.Empty);
					}

				_batteryReserveLowActive = true;
				}
			}
		else
			{
			_batteryReserveLowActive = false;
			}

		bool fullyCharged = roundedLevel >= 100;
		if (fullyCharged)
			{
			if (_batteryFullyChargedActive != true)
				{
				if (_batteryFullyChargedActive.HasValue)
					{
					BatteryFullyCharged?.Invoke (this, EventArgs.Empty);
					}

				_batteryFullyChargedActive = true;
				}
			}
		else
			{
			_batteryFullyChargedActive = false;
			}
		}

	#endregion Refresh loop

	#region Entity properties

	[EntityProperty (Id = "tileDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string TileDisplay
		{
		get;
		private set => SetAndNotify ("tileDisplay", value, ref field);
		}

	[EntityProperty (Id = "tileIcon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string TileIcon
		{
		get;
		private set => SetAndNotify ("tileIcon", value, ref field);
		}

	[EntityProperty (Id = "deviceLabel")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string DeviceLabel
		{
		get;
		private set => SetAndNotify ("deviceLabel", value, ref field);
		}

	[EntityProperty (Id = "onlineIndicator:isOnline")]
	public bool OnlineIndicatorIsOnline
		{
		get;
		private set => SetAndNotify ("onlineIndicator:isOnline", value, ref field);
		}

	[EntityProperty (Id = "readyIndicator:isReady")]
	public bool ReadyIndicatorIsReady
		{
		get;
		private set => SetAndNotify ("readyIndicator:isReady", value, ref field);
		}

	[EntityProperty (Id = "batteryLevelDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string BatteryLevelDisplay
		{
		get;
		private set => SetAndNotify ("batteryLevelDisplay", value, ref field);
		}

	[EntityProperty (Id = "backupReservePercent", RangeMinimum = 0, RangeMaximum = 100, RangeStepSize = 1, Units = "Percent")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public int BackupReservePercent
		{
		get;
		private set => SetAndNotify ("backupReservePercent", value, ref field);
		}

	[EntityProperty (Id = "backupReserveValueFormat")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string BackupReserveValueFormat => "%s%";

	[EntityProperty (Id = "operationModeDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string OperationModeDisplay
		{
		get;
		private set => SetAndNotify ("operationModeDisplay", value, ref field);
		}

	[EntityProperty (
		Id = "operationMode",
		Type = DriverEntityValueType.String,
		AvailableValues = new[] { "self_consumption", "backup", "autonomous" },
		AvailableValuesLabels = new[] { "Self Powered", "Backup Only", "Autonomous" },
		AvailableValuesLocalizationKeys = new[] { "SelfPoweredButtonLabel", "BackupOnlyButtonLabel", "AutonomousButtonLabel" })]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string OperationMode
		{
		get;
		private set => SetAndNotify ("operationMode", value, ref field);
		}

	[EntityProperty (Id = "gridChargingEnabled")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool GridChargingEnabled
		{
		get;
		private set => SetAndNotify ("gridChargingEnabled", value, ref field);
		}

	[EntityProperty (Id = "stormWatchEnabled")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool StormWatchEnabled
		{
		get;
		private set => SetAndNotify ("stormWatchEnabled", value, ref field);
		}

	[EntityProperty (Id = "gridExportModeDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string GridExportModeDisplay
		{
		get;
		private set => SetAndNotify ("gridExportModeDisplay", value, ref field);
		}

	[EntityProperty (
		Id = "gridExportMode",
		Type = DriverEntityValueType.String,
		AvailableValues = new[] { "battery_ok", "pv_only", "never" },
		AvailableValuesLabels = new[] { "Battery OK", "Solar Only", "Never" },
		AvailableValuesLocalizationKeys = new[] { "BatteryOkButtonLabel", "PvOnlyButtonLabel", "NeverButtonLabel" })]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string GridExportMode
		{
		get;
		private set => SetAndNotify ("gridExportMode", value, ref field);
		}

	[EntityProperty (Id = "solarPowerDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string SolarPowerDisplay
		{
		get;
		private set => SetAndNotify ("solarPowerDisplay", value, ref field);
		}

	[EntityProperty (Id = "gridPowerDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string GridPowerDisplay
		{
		get;
		private set => SetAndNotify ("gridPowerDisplay", value, ref field);
		}

	[EntityProperty (Id = "loadPowerDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string LoadPowerDisplay
		{
		get;
		private set => SetAndNotify ("loadPowerDisplay", value, ref field);
		}

	[EntityProperty (Id = "siteNameDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string SiteNameDisplay
		{
		get;
		private set => SetAndNotify ("siteNameDisplay", value, ref field);
		}

	[EntityProperty (Id = "statusSummary")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string StatusSummary
		{
		get;
		private set => SetAndNotify ("statusSummary", value, ref field);
		}

	[EntityProperty (
		Id = "energyPeriod",
		Type = DriverEntityValueType.String,
		AvailableValues = new[] { "day", "week", "month", "year", "lifetime" },
		AvailableValuesLabels = new[] { "Day", "Week", "Month", "Year", "Lifetime" },
		AvailableValuesLocalizationKeys = new[] { "PeriodDayButtonLabel", "PeriodWeekButtonLabel", "PeriodMonthButtonLabel", "PeriodYearButtonLabel", "PeriodLifetimeButtonLabel" })]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyPeriod
		{
		get;
		private set => SetAndNotify ("energyPeriod", value, ref field);
		}

	// The text shown by the Energy page's period button group in place of a raw offset number; rebuilt
	// whenever EnergyPeriod changes or the underlying anchor date moves (see RefreshEnergyAsync).
	[EntityProperty (Id = "energyPeriodOffsetFormat")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyPeriodOffsetFormat
		{
		get;
		private set => SetAndNotify ("energyPeriodOffsetFormat", value, ref field);
		}

	// Lifetime has no navigable prior instances, so the button group is hidden whenever it is selected.
	[EntityProperty (Id = "energyPeriodOffsetVisible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyPeriodOffsetVisible
		{
		get;
		private set => SetAndNotify ("energyPeriodOffsetVisible", value, ref field);
		}

	// Disables the Energy page's forward button once the current instance of the selected period (e.g.
	// today, for Day) has been reached, since there is nothing more recent to navigate to.
	[EntityProperty (Id = "energyPeriodForwardEnabled")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyPeriodForwardEnabled
		{
		get;
		private set => SetAndNotify ("energyPeriodForwardEnabled", value, ref field);
		}

	[EntityProperty (
		Id = "energyType",
		Type = DriverEntityValueType.String,
		AvailableValues = new[] { "solar", "powerwall", "grid", "house" },
		AvailableValuesLabels = new[] { "Solar", "Powerwall", "Grid", "House" },
		AvailableValuesLocalizationKeys = new[] { "EnergyTypeSolarButtonLabel", "EnergyTypePowerwallButtonLabel", "EnergyTypeGridButtonLabel", "EnergyTypeHouseButtonLabel" })]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyType
		{
		get;
		private set => SetAndNotify ("energyType", value, ref field);
		}

	[EntityProperty (Id = "energyPeriodLabel")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyPeriodLabel
		{
		get;
		private set => SetAndNotify ("energyPeriodLabel", value, ref field);
		}

	[EntityProperty (Id = "energyValueDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyValueDisplay
		{
		get;
		private set => SetAndNotify ("energyValueDisplay", value, ref field);
		}

	[EntityProperty (Id = "energyDetailDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyDetailDisplay
		{
		get;
		private set => SetAndNotify ("energyDetailDisplay", value, ref field);
		}

	[EntityProperty (Id = "energySummaryDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergySummaryDisplay
		{
		get;
		private set => SetAndNotify ("energySummaryDisplay", value, ref field);
		}

	// Disables the Energy page's data controls while a user-initiated period change (period type switch, or
	// back/forward navigation) is in flight, so stale figures for the previous period are never shown next
	// to a label/button that has already moved on to the new one. Re-enabled once the corresponding refresh
	// completes, successfully or not (see RefreshEnergyAsync). Routine background polling never disables
	// this; it only ever re-enables what is already enabled.
	[EntityProperty (Id = "energyContentEnabled")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyContentEnabled
		{
		get;
		private set => SetAndNotify ("energyContentEnabled", value, ref field);
		}

	[EntityProperty (
		Id = "impactPeriod",
		Type = DriverEntityValueType.String,
		AvailableValues = new[] { "day", "week", "month", "year", "lifetime" },
		AvailableValuesLabels = new[] { "Day", "Week", "Month", "Year", "Lifetime" },
		AvailableValuesLocalizationKeys = new[] { "PeriodDayButtonLabel", "PeriodWeekButtonLabel", "PeriodMonthButtonLabel", "PeriodYearButtonLabel", "PeriodLifetimeButtonLabel" })]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ImpactPeriod
		{
		get;
		private set => SetAndNotify ("impactPeriod", value, ref field);
		}

	// The text shown by the Impact page's period button group in place of a raw offset number; rebuilt
	// whenever ImpactPeriod changes or the underlying anchor date moves (see RefreshImpactAsync).
	[EntityProperty (Id = "impactPeriodOffsetFormat")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ImpactPeriodOffsetFormat
		{
		get;
		private set => SetAndNotify ("impactPeriodOffsetFormat", value, ref field);
		}

	// Lifetime has no navigable prior instances, so the button group is hidden whenever it is selected.
	[EntityProperty (Id = "impactPeriodOffsetVisible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool ImpactPeriodOffsetVisible
		{
		get;
		private set => SetAndNotify ("impactPeriodOffsetVisible", value, ref field);
		}

	// Disables the Impact page's forward button once the current instance of the selected period (e.g.
	// today, for Day) has been reached, since there is nothing more recent to navigate to.
	[EntityProperty (Id = "impactPeriodForwardEnabled")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool ImpactPeriodForwardEnabled
		{
		get;
		private set => SetAndNotify ("impactPeriodForwardEnabled", value, ref field);
		}

	[EntityProperty (Id = "impactPeriodLabel")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ImpactPeriodLabel
		{
		get;
		private set => SetAndNotify ("impactPeriodLabel", value, ref field);
		}

	[EntityProperty (Id = "selfPoweredPercent", RangeMinimum = 0, RangeMaximum = 100, Units = "Percent")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public int SelfPoweredPercent
		{
		get;
		private set => SetAndNotify ("selfPoweredPercent", value, ref field);
		}

	[EntityProperty (Id = "impactHomeUsageDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ImpactHomeUsageDisplay
		{
		get;
		private set => SetAndNotify ("impactHomeUsageDisplay", value, ref field);
		}

	[EntityProperty (Id = "impactGridUsageDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ImpactGridUsageDisplay
		{
		get;
		private set => SetAndNotify ("impactGridUsageDisplay", value, ref field);
		}

	[EntityProperty (Id = "impactSummaryDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ImpactSummaryDisplay
		{
		get;
		private set => SetAndNotify ("impactSummaryDisplay", value, ref field);
		}

	// Disables the Impact page's data controls while a user-initiated period change (period type switch, or
	// back/forward navigation) is in flight; see EnergyContentEnabled for the full rationale.
	[EntityProperty (Id = "impactContentEnabled")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool ImpactContentEnabled
		{
		get;
		private set => SetAndNotify ("impactContentEnabled", value, ref field);
		}

	#endregion Entity properties

	#region Entity commands

	/// <summary>Sets the Powerwall battery backup reserve level (0-100 percent).</summary>
	[EntityCommand (Id = "setBackupReservePercent")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetBackupReservePercent ([EntityParameter (RangeMinimum = 0, RangeMaximum = 100, Units = "Percent")] int value)
		{
		_ = Task.Run (() => ExecuteControlAsync (
			client => client.SetReserveAsync (value),
			"set backup reserve to " + value.ToString (CultureInfo.InvariantCulture) + "%"));
		}

	/// <summary>Enables or disables charging the battery from the grid.</summary>
	[EntityCommand (Id = "setGridChargingEnabled")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetGridChargingEnabled ([EntityParameter] bool value)
		{
		_ = Task.Run (() => ExecuteControlAsync (
			client => client.SetGridChargingAsync (value),
			"set grid charging to " + value));
		}

	/// <summary>Sets the battery operation mode (self_consumption, backup, or autonomous).</summary>
	[EntityCommand (Id = "setOperationMode")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetOperationMode ([EntityParameter] string value)
		{
		if (string.IsNullOrWhiteSpace (value))
			{
			return;
			}

		_ = Task.Run (() => ExecuteControlAsync (client => client.SetModeAsync (value), "set mode to " + value));
		}

	/// <summary>Sets the grid export rule (battery_ok, pv_only, or never).</summary>
	[EntityCommand (Id = "setGridExportMode")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetGridExportMode ([EntityParameter] string value)
		{
		if (string.IsNullOrWhiteSpace (value))
			{
			return;
			}

		_ = Task.Run (() => ExecuteControlAsync (client => client.SetGridExportAsync (value), "set grid export to " + value));
		}

	/// <summary>Enables or disables Storm Watch predictive pre-charging ahead of severe weather.</summary>
	[EntityCommand (Id = "setStormWatchEnabled")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetStormWatchEnabled ([EntityParameter] bool value)
		{
		_ = Task.Run (() => ExecuteControlAsync (client => client.SetStormWatchAsync (value), "set storm watch to " + value));
		}

	/// <summary>Sets the selected aggregation period for the Energy page (day, week, month, year, or lifetime).</summary>
	[EntityCommand (Id = "setEnergyPeriod")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetEnergyPeriod ([EntityParameter] string value)
		{
		if (string.IsNullOrWhiteSpace (value))
			{
			return;
			}

		// The anchor date is deliberately left untouched here: the same underlying date is simply
		// reinterpreted under the newly selected period type (e.g. a date viewed on Day becomes the
		// containing month when switching to Month), rather than resetting to the current period. Whether
		// that reinterpreted date is still "current" depends on the new period type though (e.g. a date
		// within this month, but not today, is current for Month but not for Day), so it is recomputed.
		EnergyPeriod = value;
		EnergyPeriodOffsetVisible = !string.Equals (value, "lifetime", StringComparison.OrdinalIgnoreCase);
		_energyPeriodIsCurrent = IsSamePeriodBucket (ParseHistoryPeriod (value), _energyPeriodAnchorDate, DateTimeOffset.Now);
		UpdateEnergyPeriodDisplayImmediate ();
		_ = Task.Run (TriggerImmediateRefreshAsync);
		}

	/// <summary>Moves the Energy page's viewed period back one whole step (e.g. from Today to Yesterday).</summary>
	[EntityCommand (Id = "energyPeriodOffsetBack")]
	[EntityCommandMetadata (Programmable = true)]
	public void EnergyPeriodOffsetBack ()
		{
		_energyPeriodAnchorDate = StepPeriod (ParseHistoryPeriod (EnergyPeriod), _energyPeriodAnchorDate, -1);
		_energyPeriodIsCurrent = false;
		UpdateEnergyPeriodDisplayImmediate ();
		_ = Task.Run (TriggerImmediateRefreshAsync);
		}

	/// <summary>
	/// Moves the Energy page's viewed period forward one whole step. Stepping never overshoots into the
	/// future: <see cref="DateTimeOffset.Now"/> is read fresh on every call, so the stop point is always
	/// whichever period is live at the moment this is invoked, not whichever period was live when the user
	/// first navigated back. If time has advanced past the originally-viewed period while browsing history,
	/// this simply lands on the new current period rather than the old one.
	/// </summary>
	[EntityCommand (Id = "energyPeriodOffsetForward")]
	[EntityCommandMetadata (Programmable = true)]
	public void EnergyPeriodOffsetForward ()
		{
		HistoryPeriod period = ParseHistoryPeriod (EnergyPeriod);
		DateTimeOffset stepped = StepPeriod (period, _energyPeriodAnchorDate, 1);
		DateTimeOffset now = DateTimeOffset.Now;
		if (stepped >= now || IsSamePeriodBucket (period, stepped, now))
			{
			_energyPeriodAnchorDate = now;
			_energyPeriodIsCurrent = true;
			}
		else
			{
			_energyPeriodAnchorDate = stepped;
			}

		UpdateEnergyPeriodDisplayImmediate ();
		_ = Task.Run (TriggerImmediateRefreshAsync);
		}

	/// <summary>Sets the selected energy type for the Energy page (solar, powerwall, grid, or house).</summary>
	[EntityCommand (Id = "setEnergyType")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetEnergyType ([EntityParameter] string value)
		{
		if (string.IsNullOrWhiteSpace (value))
			{
			return;
			}

		EnergyType = value;
		_ = Task.Run (TriggerImmediateRefreshAsync);
		}

	/// <summary>Sets the selected aggregation period for the Impact page (day, week, month, year, or lifetime).</summary>
	[EntityCommand (Id = "setImpactPeriod")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetImpactPeriod ([EntityParameter] string value)
		{
		if (string.IsNullOrWhiteSpace (value))
			{
			return;
			}

		// The anchor date is deliberately left untouched here: the same underlying date is simply
		// reinterpreted under the newly selected period type (e.g. a date viewed on Day becomes the
		// containing month when switching to Month), rather than resetting to the current period. Whether
		// that reinterpreted date is still "current" depends on the new period type though (e.g. a date
		// within this month, but not today, is current for Month but not for Day), so it is recomputed.
		ImpactPeriod = value;
		ImpactPeriodOffsetVisible = !string.Equals (value, "lifetime", StringComparison.OrdinalIgnoreCase);
		_impactPeriodIsCurrent = IsSamePeriodBucket (ParseHistoryPeriod (value), _impactPeriodAnchorDate, DateTimeOffset.Now);
		UpdateImpactPeriodDisplayImmediate ();
		_ = Task.Run (TriggerImmediateRefreshAsync);
		}

	/// <summary>Moves the Impact page's viewed period back one whole step (e.g. from Today to Yesterday).</summary>
	[EntityCommand (Id = "impactPeriodOffsetBack")]
	[EntityCommandMetadata (Programmable = true)]
	public void ImpactPeriodOffsetBack ()
		{
		_impactPeriodAnchorDate = StepPeriod (ParseHistoryPeriod (ImpactPeriod), _impactPeriodAnchorDate, -1);
		_impactPeriodIsCurrent = false;
		UpdateImpactPeriodDisplayImmediate ();
		_ = Task.Run (TriggerImmediateRefreshAsync);
		}

	/// <summary>
	/// Moves the Impact page's viewed period forward one whole step. Stepping never overshoots into the
	/// future: <see cref="DateTimeOffset.Now"/> is read fresh on every call, so the stop point is always
	/// whichever period is live at the moment this is invoked, not whichever period was live when the user
	/// first navigated back. If time has advanced past the originally-viewed period while browsing history,
	/// this simply lands on the new current period rather than the old one.
	/// </summary>
	[EntityCommand (Id = "impactPeriodOffsetForward")]
	[EntityCommandMetadata (Programmable = true)]
	public void ImpactPeriodOffsetForward ()
		{
		HistoryPeriod period = ParseHistoryPeriod (ImpactPeriod);
		DateTimeOffset stepped = StepPeriod (period, _impactPeriodAnchorDate, 1);
		DateTimeOffset now = DateTimeOffset.Now;
		if (stepped >= now || IsSamePeriodBucket (period, stepped, now))
			{
			_impactPeriodAnchorDate = now;
			_impactPeriodIsCurrent = true;
			}
		else
			{
			_impactPeriodAnchorDate = stepped;
			}

		UpdateImpactPeriodDisplayImmediate ();
		_ = Task.Run (TriggerImmediateRefreshAsync);
		}

	/// <summary>Immediately refreshes monitored Powerwall status from the Tesla cloud.</summary>
	[EntityCommand (Id = "refreshNow")]
	[EntityCommandMetadata (Programmable = true)]
	public void RefreshNow ()
		{
		_ = Task.Run (TriggerImmediateRefreshAsync);
		}

	private async Task ExecuteControlAsync (Func<TeslaPowerwallLibrary.Powerwall, Task> action, string description)
		{
		TeslaPowerwallLibrary.Powerwall client;
		lock (_stateLock)
			{
			client = _client;
			}

		if (client == null || !client.IsClientConnected)
			{
			LogWarning ("Cannot " + description + ": not connected.");
			return;
			}

		try
			{
			await action (client).ConfigureAwait (false);
			await TriggerImmediateRefreshAsync ().ConfigureAwait (false);
			}
		catch (PowerwallException ex)
			{
			StatusSummary = "Command failed: " + ex.Message;
			LogError ("Failed to " + description + ": " + ex.Message);
			}
		}

	private async Task TriggerImmediateRefreshAsync ()
		{
		CancellationToken token;
		lock (_syncLock)
			{
			token = _refreshCancellationTokenSource?.Token ?? CancellationToken.None;
			}

		if (token.IsCancellationRequested)
			{
			return;
			}

		await RunPollAsync (token).ConfigureAwait (false);
		}

	#endregion Entity commands

	#region Entity events

	/// <summary>Raised when the grid becomes unavailable and the Powerwall begins operating as the backup source.</summary>
	[EntityEvent (Id = "gridStatusChangedToBackup", FriendlyName = "Grid Status Changed To Backup", NameLocalizationKey = "Event_GridStatusChangedToBackup")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler GridStatusChangedToBackup;

	/// <summary>Raised when grid power is restored after an outage.</summary>
	[EntityEvent (Id = "gridStatusRestored", FriendlyName = "Grid Status Restored", NameLocalizationKey = "Event_GridStatusRestored")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler GridStatusRestored;

	/// <summary>Raised when Tesla's Storm Watch predictive pre-charge activates ahead of severe weather.</summary>
	[EntityEvent (Id = "stormWatchActivated", FriendlyName = "Storm Watch Activated", NameLocalizationKey = "Event_StormWatchActivated")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler StormWatchActivated;

	/// <summary>Raised when Tesla's Storm Watch predictive pre-charge deactivates.</summary>
	[EntityEvent (Id = "stormWatchDeactivated", FriendlyName = "Storm Watch Deactivated", NameLocalizationKey = "Event_StormWatchDeactivated")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler StormWatchDeactivated;

	/// <summary>Raised when the battery charge level crosses down to or below the configured backup reserve.</summary>
	[EntityEvent (Id = "batteryReserveLow", FriendlyName = "Battery Reserve Low", NameLocalizationKey = "Event_BatteryReserveLow")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler BatteryReserveLow;

	/// <summary>Raised when the battery reaches a full (100%) charge level.</summary>
	[EntityEvent (Id = "batteryFullyCharged", FriendlyName = "Battery Fully Charged", NameLocalizationKey = "Event_BatteryFullyCharged")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler BatteryFullyCharged;

	#endregion Entity events

	private void OnCloudTokensRefreshed (object sender, CloudTokensRefreshedEventArgs e)
		{
		// Only RefreshToken is persisted: AccessToken is never configured or read back by the driver, since
		// the library derives a fresh one from RefreshToken alone on every connect (see GetOrCreateClient).
		if (string.IsNullOrWhiteSpace (e.RefreshToken))
			{
			return;
			}

		_refreshToken = e.RefreshToken;
		_pendingRefreshToken = e.RefreshToken;

		var updates = new Dictionary<string, DriverEntityValue?> (StringComparer.OrdinalIgnoreCase)
			{
			["RefreshToken"] = new DriverEntityValue (e.RefreshToken),
			};

		try
			{
			ConfigurationController.NotifyValuesChanged (updates);
			}
		catch (Exception ex)
			{
			LogWarning ("Failed to persist refreshed Tesla refresh token: " + ex.Message);
			}
		}

	private void SetUnavailableState (string status)
		{
		TileDisplay = "--";
		TileIcon = "icBatteryLow";
		BatteryLevelDisplay = "--";
		OperationModeDisplay = "--";
		SolarPowerDisplay = "--";
		GridPowerDisplay = "--";
		LoadPowerDisplay = "--";
		GridExportModeDisplay = "--";
		EnergyPeriodLabel = "--";
		EnergyValueDisplay = "--";
		EnergyDetailDisplay = "--";
		EnergySummaryDisplay = "--";
		ImpactPeriodLabel = "--";
		SelfPoweredPercent = 0;
		ImpactHomeUsageDisplay = "--";
		ImpactGridUsageDisplay = "--";
		ImpactSummaryDisplay = "--";
		StatusSummary = status;
		OnlineIndicatorIsOnline = false;
		ReadyIndicatorIsReady = false;
		TryPublishUiDefinition ();
		}

	private static string FormatPower (double? watts)
		{
		if (!watts.HasValue)
			{
			return "--";
			}

		return (watts.Value / 1000d).ToString ("0.0", CultureInfo.InvariantCulture) + " kW";
		}

	private static string FormatGridPower (double? watts, bool exportEnabled)
		{
		if (!watts.HasValue)
			{
			return "--";
			}

		double kw = watts.Value / 1000d;
		string magnitude = Math.Abs (kw).ToString ("0.0", CultureInfo.InvariantCulture);
		if (!exportEnabled || magnitude == "0.0")
			{
			// Export disabled: the grid can only ever supply the house (import), so with only one
			// possible direction there is nothing to call out. Also used when the flow rounds to
			// zero, to avoid a misleading directional label (e.g. "Importing 0.0 kW").
			return magnitude + " kW";
			}

		return kw > 0
			? "Importing " + magnitude + " kW"
			: "Exporting " + magnitude + " kW";
		}

	private static string FormatBatteryChargeState (double? watts)
		{
		if (!watts.HasValue)
			{
			return "Idle";
			}

		double kw = watts.Value / 1000d;
		string magnitude = Math.Abs (kw).ToString ("0.0", CultureInfo.InvariantCulture);
		if (magnitude == "0.0")
			{
			// Rounds to zero: the battery isn't meaningfully charging or discharging.
			return "Idle";
			}

		return kw > 0
			? "Discharging " + magnitude + " kW"
			: "Charging " + magnitude + " kW";
		}

	// The house can draw power from solar, the grid, and the battery simultaneously; the tile icon
	// reflects whichever source is currently contributing the most, using only the three icons
	// available for this driver (solar, grid, and powerwall). Only positive (supplying) contributions
	// are considered: a positive Site means the grid is supplying (importing), a positive Battery
	// means the battery is supplying (discharging); grid export and battery charging are not
	// "sources" for this purpose.
	private static string DeterminePrimarySourceIcon (PowerSnapshot power)
		{
		if (power == null)
			{
			return "icBatteryLow";
			}

		double solar = Math.Max (0, power.Solar);
		double gridSupply = Math.Max (0, power.Site);
		double batterySupply = Math.Max (0, power.Battery);

		if (solar >= gridSupply && solar >= batterySupply && solar > 0)
			{
			return "icSun";
			}

		if (gridSupply >= batterySupply && gridSupply > 0)
			{
			return "icQuickAction";
			}

		return "icBatteryLow";
		}

	// Builds the compact home-tile source summary (e.g. "S:1.2 P:0.5 G:0.3"), omitting any source
	// that is not currently contributing power to the house (solar at zero, battery charging, or
	// grid export), using the same contribution sign convention as DeterminePrimarySourceIcon.
	private static string BuildTileSourceSummary (PowerSnapshot power)
		{
		if (power == null)
			{
			return "--";
			}

		double solarSupply = Math.Max (0, power.Solar);
		double batterySupply = Math.Max (0, power.Battery);
		double gridSupply = Math.Max (0, power.Site);

		var parts = new List<string> ();
		if (solarSupply > 0)
			{
			parts.Add ("S:" + FormatKwNoUnit (solarSupply));
			}

		if (batterySupply > 0)
			{
			parts.Add ("P:" + FormatKwNoUnit (batterySupply));
			}

		if (gridSupply > 0)
			{
			parts.Add ("G:" + FormatKwNoUnit (gridSupply));
			}

		return parts.Count > 0 ? string.Join (" ", parts) : "--";
		}

	private static string FormatKwNoUnit (double watts) => (watts / 1000d).ToString ("0.0", CultureInfo.InvariantCulture);

	private static string FormatModeDisplay (string mode) => mode switch
		{
		"self_consumption" => "Self Powered",
		"backup" => "Backup Only",
		"autonomous" => "Autonomous",
		null => "--",
		_ => mode
		};

	private static string FormatGridExportDisplay (string mode) => mode switch
		{
		"battery_ok" => "Battery OK",
		"pv_only" => "Solar Only",
		"never" => "Never",
		null => "--",
		_ => mode
		};

	private static string BuildUpdatedSummary ()
	=> string.Format (CultureInfo.CurrentCulture, "Updated {0}", DateTime.Now.ToString ("t", CultureInfo.CurrentCulture));

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

