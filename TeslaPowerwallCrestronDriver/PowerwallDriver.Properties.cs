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
/// Entity Model property declarations (Main/Energy/Impact page bindings, including the statically
/// declared Energy breakdown rows). See <c>PowerwallDriver.cs</c> for the driver's core
/// lifecycle/configuration, <c>PowerwallDriver.Refresh.cs</c> for the polling/history logic that
/// populates these properties, and <c>PowerwallDriver.Commands.cs</c> for entity commands/events.
/// </content>
public sealed partial class TeslaPowerwallDriver
	{
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

	// The Crestron selectorbutton only reserves display width for whichever value is shown when its page
	// first loads, then truncates any wider value selected afterward. This property reflects actual device
	// state, so any value could be the one shown first after a restart/reload - there is no fixed "first"
	// value to rely on. Every string looked up in en-US.json via AvailableValuesLocalizationKeys is
	// therefore padded to the same overall width; these fallback labels are not.
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

	// Storm Watch is fully supported when connected via the Tesla Owner API, but the Tesla Fleet API does
	// not expose it (the underlying library throws if it is called in Fleet API mode), so the Settings page
	// hides the Storm Watch toggle entirely whenever a Client ID (Fleet API) is configured. See IsFleetApi.
	[EntityProperty (Id = "stormWatchVisible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool StormWatchVisible
		{
		get;
		private set => SetAndNotify ("stormWatchVisible", value, ref field);
		}

	[EntityProperty (Id = "gridExportModeDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string GridExportModeDisplay
		{
		get;
		private set => SetAndNotify ("gridExportModeDisplay", value, ref field);
		}

	// See OperationMode above: this also reflects actual device state (no fixed first value), so every
	// label in en-US.json is padded to the same overall width.
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

	// Unlike OperationMode/GridExportMode above, this selector always shows its first value ("Day") when
	// the page loads, so only that value's label in en-US.json is padded wide enough to reserve room for
	// the others; the rest are left unpadded. Shared by ImpactPeriod below, which uses the same
	// values/labels/keys.
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

	// See EnergyPeriod above: this always shows its first value ("Solar") when the page loads, so only
	// that value's label in en-US.json is padded wide enough to reserve room for the others.
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

	// The Energy page shows a per-sub-period breakdown beneath the whole-period summary above (these rows
	// are *in addition to* EnergyValueDisplay/EnergyDetailDisplay/EnergySummaryDisplay, which continue to
	// show the aggregate total for the whole selected period): up to 24 rows, one per sub-period of the
	// selected Energy period (Day -> 24 hourly rows, Week -> 7 daily rows, Month -> 4-5 weekly rows, Year ->
	// 12 monthly rows). All 24 rows are statically declared here and in the UI, since the schema has no
	// repeating/templated row control; BuildEnergyRows (see RefreshEnergyAsync) fills in as many as the
	// selected period needs and hides the rest via EnergyRow{n}Visible. EnergyBreakdownVisible hides the
	// entire group only for Lifetime, which has no sub-period breakdown.
	[EntityProperty (Id = "energyBreakdownVisible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyBreakdownVisible
		{
		get;
		private set => SetAndNotify ("energyBreakdownVisible", value, ref field);
		}

	[EntityProperty (Id = "energyRow1Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow1Visible
		{
		get;
		private set => SetAndNotify ("energyRow1Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow1Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow1Label
		{
		get;
		private set => SetAndNotify ("energyRow1Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow1Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow1Value
		{
		get;
		private set => SetAndNotify ("energyRow1Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow1Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow1Icon
		{
		get;
		private set => SetAndNotify ("energyRow1Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow2Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow2Visible
		{
		get;
		private set => SetAndNotify ("energyRow2Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow2Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow2Label
		{
		get;
		private set => SetAndNotify ("energyRow2Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow2Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow2Value
		{
		get;
		private set => SetAndNotify ("energyRow2Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow2Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow2Icon
		{
		get;
		private set => SetAndNotify ("energyRow2Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow3Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow3Visible
		{
		get;
		private set => SetAndNotify ("energyRow3Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow3Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow3Label
		{
		get;
		private set => SetAndNotify ("energyRow3Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow3Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow3Value
		{
		get;
		private set => SetAndNotify ("energyRow3Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow3Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow3Icon
		{
		get;
		private set => SetAndNotify ("energyRow3Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow4Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow4Visible
		{
		get;
		private set => SetAndNotify ("energyRow4Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow4Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow4Label
		{
		get;
		private set => SetAndNotify ("energyRow4Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow4Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow4Value
		{
		get;
		private set => SetAndNotify ("energyRow4Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow4Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow4Icon
		{
		get;
		private set => SetAndNotify ("energyRow4Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow5Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow5Visible
		{
		get;
		private set => SetAndNotify ("energyRow5Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow5Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow5Label
		{
		get;
		private set => SetAndNotify ("energyRow5Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow5Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow5Value
		{
		get;
		private set => SetAndNotify ("energyRow5Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow5Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow5Icon
		{
		get;
		private set => SetAndNotify ("energyRow5Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow6Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow6Visible
		{
		get;
		private set => SetAndNotify ("energyRow6Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow6Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow6Label
		{
		get;
		private set => SetAndNotify ("energyRow6Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow6Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow6Value
		{
		get;
		private set => SetAndNotify ("energyRow6Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow6Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow6Icon
		{
		get;
		private set => SetAndNotify ("energyRow6Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow7Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow7Visible
		{
		get;
		private set => SetAndNotify ("energyRow7Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow7Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow7Label
		{
		get;
		private set => SetAndNotify ("energyRow7Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow7Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow7Value
		{
		get;
		private set => SetAndNotify ("energyRow7Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow7Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow7Icon
		{
		get;
		private set => SetAndNotify ("energyRow7Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow8Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow8Visible
		{
		get;
		private set => SetAndNotify ("energyRow8Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow8Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow8Label
		{
		get;
		private set => SetAndNotify ("energyRow8Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow8Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow8Value
		{
		get;
		private set => SetAndNotify ("energyRow8Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow8Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow8Icon
		{
		get;
		private set => SetAndNotify ("energyRow8Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow9Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow9Visible
		{
		get;
		private set => SetAndNotify ("energyRow9Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow9Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow9Label
		{
		get;
		private set => SetAndNotify ("energyRow9Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow9Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow9Value
		{
		get;
		private set => SetAndNotify ("energyRow9Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow9Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow9Icon
		{
		get;
		private set => SetAndNotify ("energyRow9Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow10Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow10Visible
		{
		get;
		private set => SetAndNotify ("energyRow10Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow10Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow10Label
		{
		get;
		private set => SetAndNotify ("energyRow10Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow10Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow10Value
		{
		get;
		private set => SetAndNotify ("energyRow10Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow10Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow10Icon
		{
		get;
		private set => SetAndNotify ("energyRow10Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow11Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow11Visible
		{
		get;
		private set => SetAndNotify ("energyRow11Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow11Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow11Label
		{
		get;
		private set => SetAndNotify ("energyRow11Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow11Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow11Value
		{
		get;
		private set => SetAndNotify ("energyRow11Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow11Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow11Icon
		{
		get;
		private set => SetAndNotify ("energyRow11Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow12Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow12Visible
		{
		get;
		private set => SetAndNotify ("energyRow12Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow12Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow12Label
		{
		get;
		private set => SetAndNotify ("energyRow12Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow12Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow12Value
		{
		get;
		private set => SetAndNotify ("energyRow12Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow12Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow12Icon
		{
		get;
		private set => SetAndNotify ("energyRow12Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow13Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow13Visible
		{
		get;
		private set => SetAndNotify ("energyRow13Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow13Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow13Label
		{
		get;
		private set => SetAndNotify ("energyRow13Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow13Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow13Value
		{
		get;
		private set => SetAndNotify ("energyRow13Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow13Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow13Icon
		{
		get;
		private set => SetAndNotify ("energyRow13Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow14Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow14Visible
		{
		get;
		private set => SetAndNotify ("energyRow14Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow14Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow14Label
		{
		get;
		private set => SetAndNotify ("energyRow14Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow14Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow14Value
		{
		get;
		private set => SetAndNotify ("energyRow14Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow14Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow14Icon
		{
		get;
		private set => SetAndNotify ("energyRow14Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow15Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow15Visible
		{
		get;
		private set => SetAndNotify ("energyRow15Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow15Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow15Label
		{
		get;
		private set => SetAndNotify ("energyRow15Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow15Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow15Value
		{
		get;
		private set => SetAndNotify ("energyRow15Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow15Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow15Icon
		{
		get;
		private set => SetAndNotify ("energyRow15Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow16Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow16Visible
		{
		get;
		private set => SetAndNotify ("energyRow16Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow16Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow16Label
		{
		get;
		private set => SetAndNotify ("energyRow16Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow16Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow16Value
		{
		get;
		private set => SetAndNotify ("energyRow16Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow16Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow16Icon
		{
		get;
		private set => SetAndNotify ("energyRow16Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow17Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow17Visible
		{
		get;
		private set => SetAndNotify ("energyRow17Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow17Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow17Label
		{
		get;
		private set => SetAndNotify ("energyRow17Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow17Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow17Value
		{
		get;
		private set => SetAndNotify ("energyRow17Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow17Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow17Icon
		{
		get;
		private set => SetAndNotify ("energyRow17Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow18Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow18Visible
		{
		get;
		private set => SetAndNotify ("energyRow18Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow18Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow18Label
		{
		get;
		private set => SetAndNotify ("energyRow18Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow18Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow18Value
		{
		get;
		private set => SetAndNotify ("energyRow18Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow18Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow18Icon
		{
		get;
		private set => SetAndNotify ("energyRow18Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow19Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow19Visible
		{
		get;
		private set => SetAndNotify ("energyRow19Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow19Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow19Label
		{
		get;
		private set => SetAndNotify ("energyRow19Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow19Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow19Value
		{
		get;
		private set => SetAndNotify ("energyRow19Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow19Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow19Icon
		{
		get;
		private set => SetAndNotify ("energyRow19Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow20Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow20Visible
		{
		get;
		private set => SetAndNotify ("energyRow20Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow20Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow20Label
		{
		get;
		private set => SetAndNotify ("energyRow20Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow20Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow20Value
		{
		get;
		private set => SetAndNotify ("energyRow20Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow20Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow20Icon
		{
		get;
		private set => SetAndNotify ("energyRow20Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow21Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow21Visible
		{
		get;
		private set => SetAndNotify ("energyRow21Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow21Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow21Label
		{
		get;
		private set => SetAndNotify ("energyRow21Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow21Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow21Value
		{
		get;
		private set => SetAndNotify ("energyRow21Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow21Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow21Icon
		{
		get;
		private set => SetAndNotify ("energyRow21Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow22Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow22Visible
		{
		get;
		private set => SetAndNotify ("energyRow22Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow22Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow22Label
		{
		get;
		private set => SetAndNotify ("energyRow22Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow22Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow22Value
		{
		get;
		private set => SetAndNotify ("energyRow22Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow22Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow22Icon
		{
		get;
		private set => SetAndNotify ("energyRow22Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow23Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow23Visible
		{
		get;
		private set => SetAndNotify ("energyRow23Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow23Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow23Label
		{
		get;
		private set => SetAndNotify ("energyRow23Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow23Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow23Value
		{
		get;
		private set => SetAndNotify ("energyRow23Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow23Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow23Icon
		{
		get;
		private set => SetAndNotify ("energyRow23Icon", value, ref field);
		}

	[EntityProperty (Id = "energyRow24Visible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool EnergyRow24Visible
		{
		get;
		private set => SetAndNotify ("energyRow24Visible", value, ref field);
		}

	[EntityProperty (Id = "energyRow24Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow24Label
		{
		get;
		private set => SetAndNotify ("energyRow24Label", value, ref field);
		}

	[EntityProperty (Id = "energyRow24Value")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow24Value
		{
		get;
		private set => SetAndNotify ("energyRow24Value", value, ref field);
		}

	[EntityProperty (Id = "energyRow24Icon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string EnergyRow24Icon
		{
		get;
		private set => SetAndNotify ("energyRow24Icon", value, ref field);
		}

	// See EnergyPeriod above: this always shows its first value ("Day") when the page loads, so only that
	// value's label in en-US.json is padded wide enough to reserve room for the others. Shares the same
	// values/labels/keys as EnergyPeriod above.
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
	}
