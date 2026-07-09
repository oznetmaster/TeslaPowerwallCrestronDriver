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
/// Entity Model commands (user/programmatic control of the Powerwall and Energy/Impact page navigation)
/// and entity events (one-shot state-transition notifications). See <c>PowerwallDriver.cs</c> for
/// the driver's core lifecycle/configuration and <c>PowerwallDriver.Refresh.cs</c> for the
/// polling/history logic that these commands trigger and that raises these events.
/// </content>
public sealed partial class TeslaPowerwallDriver
	{
	#region Entity commands

	/// <summary>Sets the Powerwall battery backup reserve level (0-100 percent).</summary>
	[EntityCommand (Id = "setBackupReservePercent")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetBackupReservePercent ([EntityParameter (RangeMinimum = 0, RangeMaximum = 100, Units = "Percent")] int value)
		{
		_ = Task.Run (() => ExecuteControlAsync (
			client => client.SetReserveAsync (value),
			() => BackupReservePercent = value,
			"set backup reserve to " + value.ToString (CultureInfo.InvariantCulture) + "%"));
		}

	/// <summary>Enables or disables charging the battery from the grid.</summary>
	[EntityCommand (Id = "setGridChargingEnabled")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetGridChargingEnabled ([EntityParameter] bool value)
		{
		_ = Task.Run (() => ExecuteControlAsync (
			client => client.SetGridChargingAsync (value),
			() => GridChargingEnabled = value,
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

		_ = Task.Run (() => ExecuteControlAsync (
			client => client.SetModeAsync (value),
			() =>
				{
				OperationMode = value;
				OperationModeDisplay = "Mode: " + FormatModeDisplay (value);
				},
			"set mode to " + value));
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

		_ = Task.Run (() => ExecuteControlAsync (
			client => client.SetGridExportAsync (value),
			() =>
				{
				GridExportMode = value;
				GridExportModeDisplay = "Export: " + FormatGridExportDisplay (value);
				},
			"set grid export to " + value));
		}

	/// <summary>Enables or disables Storm Watch predictive pre-charging ahead of severe weather.</summary>
	[EntityCommand (Id = "setStormWatchEnabled")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetStormWatchEnabled ([EntityParameter] bool value)
		{
		_ = Task.Run (() => ExecuteControlAsync (
			client => client.SetStormWatchAsync (value),
			() => StormWatchEnabled = value,
			"set storm watch to " + value));
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

	// Runs a user-initiated control command against the Tesla cloud. applyOptimisticUpdate is invoked
	// immediately, before the (potentially slow) cloud call, so the entity properties it touches reflect
	// the requested change right away rather than lagging until the next poll. The refresh triggered
	// afterwards - unconditionally, whether the command succeeded or failed - re-reads the true state from
	// Tesla and corrects those properties if the command was actually rejected, clamped to a different
	// value, or failed outright.
	private async Task ExecuteControlAsync (Func<TeslaPowerwallLibrary.Powerwall, Task> action, Action applyOptimisticUpdate, string description)
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

		applyOptimisticUpdate ();

		try
			{
			await action (client).ConfigureAwait (false);
			}
		catch (PowerwallException ex)
			{
			StatusSummary = "Command failed: " + ex.Message;
			LogError ("Failed to " + description + ": " + ex.Message);
			}
		finally
			{
			await TriggerImmediateRefreshAsync ().ConfigureAwait (false);
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
	}
