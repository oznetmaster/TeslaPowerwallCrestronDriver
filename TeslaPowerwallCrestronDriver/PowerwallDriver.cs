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

	private string _email = string.Empty;
	private string _refreshToken = string.Empty;
	private string _siteId = string.Empty;
	private int _refreshIntervalSeconds = DEFAULT_REFRESH_INTERVAL_SECONDS;

	private string _pendingEmail = string.Empty;
	private string _pendingRefreshToken = string.Empty;
	private string _pendingSiteId = string.Empty;
	private int _pendingRefreshIntervalSeconds = DEFAULT_REFRESH_INTERVAL_SECONDS;

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
		BatteryLevelDisplay = "--";
		OperationModeDisplay = "--";
		GridStatusDisplay = "--";
		SolarPowerDisplay = "--";
		GridPowerDisplay = "--";
		LoadPowerDisplay = "--";
		BatteryPowerDisplay = "--";
		GridExportModeDisplay = "--";
		SiteNameDisplay = "--";
		StatusSummary = "Waiting for configuration";
		BackupReservePercent = 0;
		GridChargingEnabled = false;

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

			ApplyPolledState (level, power, gridStatus, reserve, mode, gridCharging, gridExport);
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
		string gridExport)
		{
		BatteryLevelDisplay = level.HasValue ? Math.Round (level.Value, MidpointRounding.AwayFromZero).ToString ("0", CultureInfo.InvariantCulture) + "%" : "--";
		if (reserve.HasValue)
			{
			BackupReservePercent = (int) Math.Round (reserve.Value, MidpointRounding.AwayFromZero);
			}

		OperationModeDisplay = FormatModeDisplay (mode);
		if (gridCharging.HasValue)
			{
			GridChargingEnabled = gridCharging.Value;
			}

		GridExportModeDisplay = FormatGridExportDisplay (gridExport);
		SolarPowerDisplay = FormatPower (power?.Solar);
		GridPowerDisplay = FormatGridPower (power?.Site);
		LoadPowerDisplay = FormatPower (power?.Load);
		BatteryPowerDisplay = FormatBatteryPower (power?.Battery);
		GridStatusDisplay = FormatGridStatus (gridStatus);
		TileDisplay = string.Format (CultureInfo.InvariantCulture, "{0} | {1}", BatteryLevelDisplay, OperationModeDisplay);
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

	[EntityProperty (Id = "gridChargingEnabled")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool GridChargingEnabled
		{
		get;
		private set => SetAndNotify ("gridChargingEnabled", value, ref field);
		}

	[EntityProperty (Id = "gridExportModeDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string GridExportModeDisplay
		{
		get;
		private set => SetAndNotify ("gridExportModeDisplay", value, ref field);
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

	[EntityProperty (Id = "batteryPowerDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string BatteryPowerDisplay
		{
		get;
		private set => SetAndNotify ("batteryPowerDisplay", value, ref field);
		}

	[EntityProperty (Id = "gridStatusDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string GridStatusDisplay
		{
		get;
		private set => SetAndNotify ("gridStatusDisplay", value, ref field);
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

	/// <summary>Sets the battery operation mode to Self Powered.</summary>
	[EntityCommand (Id = "setOperationModeSelfConsumption")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetOperationModeSelfConsumption ()
		{
		_ = Task.Run (() => ExecuteControlAsync (client => client.SetModeAsync ("self_consumption"), "set mode to self_consumption"));
		}

	/// <summary>Sets the battery operation mode to Backup Only.</summary>
	[EntityCommand (Id = "setOperationModeBackup")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetOperationModeBackup ()
		{
		_ = Task.Run (() => ExecuteControlAsync (client => client.SetModeAsync ("backup"), "set mode to backup"));
		}

	/// <summary>Sets the battery operation mode to Autonomous (time-based control).</summary>
	[EntityCommand (Id = "setOperationModeAutonomous")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetOperationModeAutonomous ()
		{
		_ = Task.Run (() => ExecuteControlAsync (client => client.SetModeAsync ("autonomous"), "set mode to autonomous"));
		}

	/// <summary>Sets the grid export rule to allow exporting from solar or battery.</summary>
	[EntityCommand (Id = "setGridExportBatteryOk")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetGridExportBatteryOk ()
		{
		_ = Task.Run (() => ExecuteControlAsync (client => client.SetGridExportAsync ("battery_ok"), "set grid export to battery_ok"));
		}

	/// <summary>Sets the grid export rule to allow exporting from solar only.</summary>
	[EntityCommand (Id = "setGridExportPvOnly")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetGridExportPvOnly ()
		{
		_ = Task.Run (() => ExecuteControlAsync (client => client.SetGridExportAsync ("pv_only"), "set grid export to pv_only"));
		}

	/// <summary>Sets the grid export rule to disallow all export.</summary>
	[EntityCommand (Id = "setGridExportNever")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetGridExportNever ()
		{
		_ = Task.Run (() => ExecuteControlAsync (client => client.SetGridExportAsync ("never"), "set grid export to never"));
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
		BatteryLevelDisplay = "--";
		OperationModeDisplay = "--";
		GridStatusDisplay = "--";
		SolarPowerDisplay = "--";
		GridPowerDisplay = "--";
		LoadPowerDisplay = "--";
		BatteryPowerDisplay = "--";
		GridExportModeDisplay = "--";
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

	private static string FormatGridPower (double? watts)
		{
		if (!watts.HasValue)
			{
			return "--";
			}

		double kw = watts.Value / 1000d;
		return kw >= 0
			? "Importing " + kw.ToString ("0.0", CultureInfo.InvariantCulture) + " kW"
			: "Exporting " + Math.Abs (kw).ToString ("0.0", CultureInfo.InvariantCulture) + " kW";
		}

	private static string FormatBatteryPower (double? watts)
		{
		if (!watts.HasValue)
			{
			return "--";
			}

		double kw = watts.Value / 1000d;
		return kw >= 0
			? "Discharging " + kw.ToString ("0.0", CultureInfo.InvariantCulture) + " kW"
			: "Charging " + Math.Abs (kw).ToString ("0.0", CultureInfo.InvariantCulture) + " kW";
		}

	private static string FormatGridStatus (GridStatus? status) => status switch
		{
		GridStatus.Up => "Connected",
		GridStatus.Down => "Islanded",
		GridStatus.Syncing => "Syncing",
		_ => "Unknown"
		};

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

