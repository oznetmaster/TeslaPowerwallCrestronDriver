// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Collections.Generic;

using NUnit.Framework;

using TeslaPowerwall.CrestronDriver;

using TeslaPowerwallLibrary.Models;
namespace TeslaPowerwallCrestronDriver.Tests;

[TestFixture, FixtureLifeCycle (LifeCycle.InstancePerTestCase), SetCulture ("fr-FR")]
public sealed class PowerDisplayTests
	{
	private static T Call<T> (string name, params object[] args) => TestSupport.Call<T> (typeof (TeslaPowerwallDriver), name, args);
	[TestCase (null, "--")]
	[TestCase (0d, "0.0 kW")]
	[TestCase (1234d, "1.2 kW")]
	[TestCase (-2500d, "-2.5 kW")]
	public void Power_UsesInvariantKilowatts (double? watts, string expected) => Assert.That (Call<string> ("FormatPower", watts), Is.EqualTo (expected));
	[TestCase (null, true, "--")]
	[TestCase (1200d, true, "Importing 1.2 kW")]
	[TestCase (-1200d, true, "Exporting 1.2 kW")]
	[TestCase (1200d, false, "1.2 kW")]
	[TestCase (-1200d, false, "1.2 kW")]
	[TestCase (-1d, true, "0.0 kW")]
	public void Grid_OnlyLabelsMeaningfulFlowWhenExportIsEnabled (double? watts, bool export, string expected) => Assert.That (Call<string> ("FormatGridPower", watts, export), Is.EqualTo (expected));
	[TestCase (null, "Idle")]
	[TestCase (0d, "Idle")]
	[TestCase (-1d, "Idle")]
	[TestCase (1200d, "Discharging 1.2 kW")]
	[TestCase (-1200d, "Charging 1.2 kW")]
	public void Battery_DistinguishesChargingDischargingAndIdle (double? watts, string expected) => Assert.That (Call<string> ("FormatBatteryChargeState", watts), Is.EqualTo (expected));
	[TestCase (3000d, 1000d, 2000d, "icSun")]
	[TestCase (1000d, 3000d, 2000d, "icQuickAction")]
	[TestCase (1000d, 2000d, 3000d, "icBatteryLow")]
	[TestCase (1000d, -5000d, -6000d, "icSun")]
	[TestCase (0d, 0d, 0d, "icBatteryLow")]
	public void PrimarySource_IgnoresExportAndCharging (double solar, double grid, double battery, string expected) => Assert.That (Call<string> ("DeterminePrimarySourceIcon", new PowerSnapshot { Solar = solar, Site = grid, Battery = battery }), Is.EqualTo (expected));
	[Test]
	public void TileSourceSummary_UsesStableOrderAndOmitsNonSuppliers ()
		{
		Assert.That (Call<string> ("BuildTileSourceSummary", new PowerSnapshot { Solar = 1200, Battery = 500, Site = 300 }), Is.EqualTo ("S:1.2 P:0.5 G:0.3"));
		Assert.That (Call<string> ("BuildTileSourceSummary", new PowerSnapshot { Solar = 0, Battery = -500, Site = -300 }), Is.EqualTo ("--"));
		}
	[TestCase ("self_consumption", "Self Powered")]
	[TestCase ("backup", "Backup Only")]
	[TestCase ("autonomous", "Autonomous")]
	[TestCase (null, "--")]
	[TestCase ("future-mode", "future-mode")]
	public void OperatingMode_KnownNamesAndUnknownValuesAreDisplayed (string mode, string expected) => Assert.That (Call<string> ("FormatModeDisplay", mode), Is.EqualTo (expected));
	[TestCase ("battery_ok", "Battery OK")]
	[TestCase ("pv_only", "Solar Only")]
	[TestCase ("never", "Never")]
	[TestCase (null, "--")]
	[TestCase ("future-mode", "future-mode")]
	public void ExportMode_KnownNamesAndUnknownValuesAreDisplayed (string mode, string expected) => Assert.That (Call<string> ("FormatGridExportDisplay", mode), Is.EqualTo (expected));
	}