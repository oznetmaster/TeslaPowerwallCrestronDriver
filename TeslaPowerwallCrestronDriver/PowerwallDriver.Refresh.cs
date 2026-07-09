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

/// <content>
/// Background refresh/polling loop, Tesla cloud connection management, and the Energy/Impact history
/// retrieval, period-navigation math, and display-formatting logic that feeds the Entity Model properties
/// declared in <c>PowerwallDriver.Properties.cs</c>. See <c>PowerwallDriver.cs</c> for the
/// driver's core lifecycle/configuration and <c>PowerwallDriver.Commands.cs</c> for the entity
/// commands/events that trigger and observe this logic.
/// </content>
public sealed partial class TeslaPowerwallDriver
	{
	#region Connection and polling

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

	#endregion Connection and polling

	#region Energy and Impact history

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
				EnergyBreakdownVisible = false;
				ApplyEnergyRows (Array.Empty<(string, string, string)> ());
				return;
				}

			double solarKwh = points.Sum (p => p.SolarKwh);
			double homeKwh = points.Sum (p => p.HomeKwh);
			double fromGridKwh = points.Sum (p => p.FromGridKwh);
			double toGridKwh = points.Sum (p => p.ToGridKwh);
			double batteryChargeKwh = points.Sum (p => p.BatteryChargeKwh);
			double batteryDischargeKwh = points.Sum (p => p.BatteryDischargeKwh);

			ApplyEnergyDisplays (EnergyType, solarKwh, homeKwh, fromGridKwh, toGridKwh, batteryChargeKwh, batteryDischargeKwh);

			IReadOnlyList<(string Label, string Value, string Icon)> rows = BuildEnergyRows (period, _energyPeriodAnchorDate, EnergyType, points);
			EnergyBreakdownVisible = rows.Count > 0;
			ApplyEnergyRows (rows);
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

	// Builds the Energy page's per-sub-period breakdown rows shown beneath the whole-period summary
	// (EnergyValueDisplay/EnergyDetailDisplay/EnergySummaryDisplay above, which remain unchanged and keep
	// showing the aggregate total for the whole period): Day -> 24 hourly rows, Week -> 7 daily rows,
	// Month -> 4-5 weekly rows, Year -> 12 monthly rows. Lifetime has no meaningful sub-period to break out,
	// so it returns no rows (see BuildEnergyRowSlots), and ApplyEnergyRows/EnergyBreakdownVisible hide the
	// entire group in that case.
	private static IReadOnlyList<(string Label, string Value, string Icon)> BuildEnergyRows (
		HistoryPeriod period, DateTimeOffset anchor, string energyType, IReadOnlyList<EnergyHistoryPoint> points)
		{
		IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End, string Label)> slots = BuildEnergyRowSlots (period, anchor);
		if (slots.Count == 0)
			{
			return Array.Empty<(string, string, string)> ();
			}

		var rows = new List<(string Label, string Value, string Icon)> (slots.Count);
		foreach (var slot in slots)
			{
			double solar = 0, home = 0, fromGrid = 0, toGrid = 0, batteryCharge = 0, batteryDischarge = 0;
			foreach (EnergyHistoryPoint point in points)
				{
				DateTimeOffset local = point.Timestamp.ToLocalTime ();
				if (local >= slot.Start && local < slot.End)
					{
					solar += point.SolarKwh;
					home += point.HomeKwh;
					fromGrid += point.FromGridKwh;
					toGrid += point.ToGridKwh;
					batteryCharge += point.BatteryChargeKwh;
					batteryDischarge += point.BatteryDischargeKwh;
					}
				}

			(string value, string icon) = SelectRowDisplay (energyType, solar, home, fromGrid, toGrid, batteryCharge, batteryDischarge);
			rows.Add ((slot.Label, value, icon));
			}

		return rows;
		}

	// Builds the fixed row slots for a period: Day -> 24 one-hour slots, Week -> 7 one-day slots (Sunday
	// first, matching StartOfWeek), Month -> 4-6 Sunday-Saturday weekly slots covering the month, Year ->
	// 12 one-month slots. Lifetime has no fixed sub-period to break out and returns no slots.
	private static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End, string Label)> BuildEnergyRowSlots (HistoryPeriod period, DateTimeOffset anchor)
		{
		switch (period)
			{
			case HistoryPeriod.Week:
				return BuildDailyRowSlots (anchor);

			case HistoryPeriod.Month:
				return BuildWeeklyRowSlotsForMonth (anchor);

			case HistoryPeriod.Year:
				return BuildMonthlyRowSlots (anchor);

			case HistoryPeriod.Lifetime:
				return Array.Empty<(DateTimeOffset, DateTimeOffset, string)> ();

			default:
				return BuildHourlyRowSlots (anchor);
			}
		}

	private static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End, string Label)> BuildHourlyRowSlots (DateTimeOffset anchor)
		{
		DateTimeOffset dayStart = LocalMidnight (anchor.Year, anchor.Month, anchor.Day);
		var slots = new List<(DateTimeOffset, DateTimeOffset, string)> (24);
		for (int hour = 0; hour < 24; hour++)
			{
			DateTimeOffset slotStart = dayStart.AddHours (hour);
			slots.Add ((slotStart, slotStart.AddHours (1), slotStart.ToString ("h tt", CultureInfo.CurrentCulture)));
			}

		return slots;
		}

	private static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End, string Label)> BuildDailyRowSlots (DateTimeOffset anchor)
		{
		DateTimeOffset weekStart = StartOfWeek (anchor);
		var slots = new List<(DateTimeOffset, DateTimeOffset, string)> (7);
		for (int day = 0; day < 7; day++)
			{
			DateTimeOffset slotStart = weekStart.AddDays (day);
			slots.Add ((slotStart, slotStart.AddDays (1), slotStart.ToString ("dddd", CultureInfo.CurrentCulture)));
			}

		return slots;
		}

	// Chunks the month into groups of up to 7 calendar days starting from day 1 (not aligned to Sunday),
	// always yielding exactly 4 rows for a 28-day month or 5 rows for a 29-31 day month, per the fixed
	// row-count design (see BuildEnergyRowSlots/ApplyEnergyRows) - unlike calendar-week-aligned chunking,
	// which can yield as many as 6 rows depending on which weekday the month happens to start on.
	private static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End, string Label)> BuildWeeklyRowSlotsForMonth (DateTimeOffset anchor)
		{
		int daysInMonth = DateTime.DaysInMonth (anchor.Year, anchor.Month);
		var slots = new List<(DateTimeOffset, DateTimeOffset, string)> (5);
		for (int day = 1; day <= daysInMonth; day += 7)
			{
			int lastDay = Math.Min (day + 6, daysInMonth);
			DateTimeOffset slotStart = LocalMidnight (anchor.Year, anchor.Month, day);
			DateTimeOffset slotEnd = LocalMidnight (anchor.Year, anchor.Month, lastDay).AddDays (1);
			string label = day == lastDay
				? slotStart.ToString ("MMMM d", CultureInfo.CurrentCulture)
				: string.Format (CultureInfo.CurrentCulture, "{0}-{1}", slotStart.ToString ("MMMM d", CultureInfo.CurrentCulture), lastDay.ToString (CultureInfo.InvariantCulture));
			slots.Add ((slotStart, slotEnd, label));
			}

		return slots;
		}

	private static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End, string Label)> BuildMonthlyRowSlots (DateTimeOffset anchor)
		{
		var slots = new List<(DateTimeOffset, DateTimeOffset, string)> (12);
		for (int month = 1; month <= 12; month++)
			{
			DateTimeOffset slotStart = LocalMidnight (anchor.Year, month, 1);
			slots.Add ((slotStart, slotStart.AddMonths (1), slotStart.ToString ("MMMM", CultureInfo.CurrentCulture)));
			}

		return slots;
		}

	// Picks the single bare-kWh value and characteristic icon shown by one Energy breakdown row, for the
	// currently selected energy type. Solar has no direction (always "produced"). Grid shows its net flow
	// for the row's slot - import positive, export negative - so exactly one of two arrow icons applies:
	// icArrowDown for importing, icArrowUp for exporting. Powerwall shows its own net flow the same way
	// (icArrowUp for discharging, icArrowDown for charging), except when that net flow rounds to 0 kWh:
	// the battery is then neither charging nor discharging, so no icon (an empty string) is shown. House
	// shows total usage with an icon reflecting whichever source (solar, grid, or battery) supplied the
	// most of it, mirroring DeterminePrimarySourceIcon's tie-break order but over energy sums instead of
	// instantaneous power.
	private static (string Value, string Icon) SelectRowDisplay (
		string energyType,
		double solarKwh,
		double homeKwh,
		double fromGridKwh,
		double toGridKwh,
		double batteryChargeKwh,
		double batteryDischargeKwh)
		{
		switch (energyType)
			{
			case "powerwall":
				double batteryNet = batteryDischargeKwh - batteryChargeKwh;
				double batteryMagnitude = Math.Abs (batteryNet);
				bool batteryIdle = Math.Round (batteryMagnitude, 1, MidpointRounding.AwayFromZero) == 0;
				string batteryIcon = batteryIdle ? string.Empty : (batteryNet >= 0 ? "icArrowUp" : "icArrowDown");
				return (FormatKwhBare (batteryMagnitude), batteryIcon);

			case "grid":
				double gridNet = fromGridKwh - toGridKwh;
				return (FormatKwhBare (Math.Abs (gridNet)), gridNet >= 0 ? "icArrowDown" : "icArrowUp");

			case "house":
				return (FormatKwhBare (homeKwh), DetermineHouseSourceIcon (solarKwh, fromGridKwh, batteryDischargeKwh));

			case "solar":
			default:
				return (FormatKwhBare (solarKwh), "icSun");
			}
		}

	// Energy-sum counterpart of DeterminePrimarySourceIcon, used by the House row of the Energy breakdown:
	// same tie-break order (solar, then grid, then battery; only positive/supplying contributions count),
	// but over a kWh total for the row's slot instead of an instantaneous PowerSnapshot.
	private static string DetermineHouseSourceIcon (double solarKwh, double fromGridKwh, double batteryDischargeKwh)
		{
		double solar = Math.Max (0, solarKwh);
		double grid = Math.Max (0, fromGridKwh);
		double battery = Math.Max (0, batteryDischargeKwh);

		if (solar >= grid && solar >= battery && solar > 0)
			{
			return "icSun";
			}

		if (grid >= battery && grid > 0)
			{
			return "icQuickAction";
			}

		return "icBatteryLow";
		}

	// Applies the computed Energy breakdown rows to the 24 statically-declared EnergyRow{n} properties,
	// showing rows 1..rows.Count with their content and hiding (and blanking) every row beyond that -
	// including all 24 when rows is empty (Lifetime, or no history yet for the selected period).
	private void ApplyEnergyRows (IReadOnlyList<(string Label, string Value, string Icon)> rows)
		{
		for (int index = 1; index <= ENERGY_ROW_COUNT; index++)
			{
			ApplyEnergyRow (index, index <= rows.Count ? rows[index - 1] : null);
			}
		}

	private void ApplyEnergyRow (int index, (string Label, string Value, string Icon)? row)
		{
		bool visible = row.HasValue;
		string label = row?.Label ?? string.Empty;
		string value = row?.Value ?? string.Empty;
		string icon = row?.Icon ?? string.Empty;

		switch (index)
			{
			case 1:
				EnergyRow1Visible = visible;
				EnergyRow1Label = label;
				EnergyRow1Value = value;
				EnergyRow1Icon = icon;
				break;
			case 2:
				EnergyRow2Visible = visible;
				EnergyRow2Label = label;
				EnergyRow2Value = value;
				EnergyRow2Icon = icon;
				break;
			case 3:
				EnergyRow3Visible = visible;
				EnergyRow3Label = label;
				EnergyRow3Value = value;
				EnergyRow3Icon = icon;
				break;
			case 4:
				EnergyRow4Visible = visible;
				EnergyRow4Label = label;
				EnergyRow4Value = value;
				EnergyRow4Icon = icon;
				break;
			case 5:
				EnergyRow5Visible = visible;
				EnergyRow5Label = label;
				EnergyRow5Value = value;
				EnergyRow5Icon = icon;
				break;
			case 6:
				EnergyRow6Visible = visible;
				EnergyRow6Label = label;
				EnergyRow6Value = value;
				EnergyRow6Icon = icon;
				break;
			case 7:
				EnergyRow7Visible = visible;
				EnergyRow7Label = label;
				EnergyRow7Value = value;
				EnergyRow7Icon = icon;
				break;
			case 8:
				EnergyRow8Visible = visible;
				EnergyRow8Label = label;
				EnergyRow8Value = value;
				EnergyRow8Icon = icon;
				break;
			case 9:
				EnergyRow9Visible = visible;
				EnergyRow9Label = label;
				EnergyRow9Value = value;
				EnergyRow9Icon = icon;
				break;
			case 10:
				EnergyRow10Visible = visible;
				EnergyRow10Label = label;
				EnergyRow10Value = value;
				EnergyRow10Icon = icon;
				break;
			case 11:
				EnergyRow11Visible = visible;
				EnergyRow11Label = label;
				EnergyRow11Value = value;
				EnergyRow11Icon = icon;
				break;
			case 12:
				EnergyRow12Visible = visible;
				EnergyRow12Label = label;
				EnergyRow12Value = value;
				EnergyRow12Icon = icon;
				break;
			case 13:
				EnergyRow13Visible = visible;
				EnergyRow13Label = label;
				EnergyRow13Value = value;
				EnergyRow13Icon = icon;
				break;
			case 14:
				EnergyRow14Visible = visible;
				EnergyRow14Label = label;
				EnergyRow14Value = value;
				EnergyRow14Icon = icon;
				break;
			case 15:
				EnergyRow15Visible = visible;
				EnergyRow15Label = label;
				EnergyRow15Value = value;
				EnergyRow15Icon = icon;
				break;
			case 16:
				EnergyRow16Visible = visible;
				EnergyRow16Label = label;
				EnergyRow16Value = value;
				EnergyRow16Icon = icon;
				break;
			case 17:
				EnergyRow17Visible = visible;
				EnergyRow17Label = label;
				EnergyRow17Value = value;
				EnergyRow17Icon = icon;
				break;
			case 18:
				EnergyRow18Visible = visible;
				EnergyRow18Label = label;
				EnergyRow18Value = value;
				EnergyRow18Icon = icon;
				break;
			case 19:
				EnergyRow19Visible = visible;
				EnergyRow19Label = label;
				EnergyRow19Value = value;
				EnergyRow19Icon = icon;
				break;
			case 20:
				EnergyRow20Visible = visible;
				EnergyRow20Label = label;
				EnergyRow20Value = value;
				EnergyRow20Icon = icon;
				break;
			case 21:
				EnergyRow21Visible = visible;
				EnergyRow21Label = label;
				EnergyRow21Value = value;
				EnergyRow21Icon = icon;
				break;
			case 22:
				EnergyRow22Visible = visible;
				EnergyRow22Label = label;
				EnergyRow22Value = value;
				EnergyRow22Icon = icon;
				break;
			case 23:
				EnergyRow23Visible = visible;
				EnergyRow23Label = label;
				EnergyRow23Value = value;
				EnergyRow23Icon = icon;
				break;
			case 24:
				EnergyRow24Visible = visible;
				EnergyRow24Label = label;
				EnergyRow24Value = value;
				EnergyRow24Icon = icon;
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

	#endregion Energy and Impact history

	#region Period calculation helpers

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

	#endregion Period calculation helpers

	#region Display formatting helpers

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

	private static string FormatKwh (string label, double kwh) =>
		string.Format (CultureInfo.InvariantCulture, "{0}: {1:0.0} kWh", label, kwh);

	private static string FormatNetKwh (string label, double kwh) =>
		string.Format (CultureInfo.InvariantCulture, "{0}: {1}{2:0.0} kWh", label, kwh >= 0 ? "+" : "-", Math.Abs (kwh));

	// Bare "N.N kWh" formatting with no label prefix, for the Energy breakdown rows, whose sub-period
	// (e.g. "3 PM", "Wed") is already conveyed by the row's own label.
	private static string FormatKwhBare (double kwh) =>
		string.Format (CultureInfo.InvariantCulture, "{0:0.0} kWh", kwh);

	#endregion Display formatting helpers
	}
