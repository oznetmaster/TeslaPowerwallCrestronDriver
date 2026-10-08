// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using NUnit.Framework;
using TeslaPowerwall.CrestronDriver;

namespace TeslaPowerwallCrestronDriver.Tests;

/// <summary>Read-only live entity and UI-binding checks using the packaged driver layout.</summary>
public sealed partial class LiveSiteTests
	{
	/// <summary>Checks that real local power readings reach the published UI properties.</summary>
	[Test, Category ("LocalLive")]
	public async Task LocalSite_PublishesObservedPowerToUi ()
		{
		RequireLocal ();
		await Poll ();
		var power = await _client.GetPowerReadingsAsync (_deadline.Token);
		Assert.That (new[] { power.Site, power.Solar, power.Battery, power.Load }
			.All (value => value.HasValue && !double.IsNaN (value.Value) && !double.IsInfinity (value.Value)), Is.True,
			"Local meters must report finite readings; missing readings must not pass as zero.");
		Assert.That (_driver.SolarPowerDisplay, Is.EqualTo (TestSupport.Call<string> (typeof (TeslaPowerwallDriver), "FormatPower", power.Solar)));
		Assert.That (_driver.LoadPowerDisplay, Does.StartWith (TestSupport.Call<string> (typeof (TeslaPowerwallDriver), "FormatPower", power.Load)));
		foreach (string property in new[] { "solarPowerDisplay", "loadPowerDisplay", "gridPowerDisplay", "batteryLevelDisplay" })
			{
			var state = _driver.GetState ();
			Assert.That (state.PropertyValues.ContainsKey (property), Is.True, "The UI property must be published: " + property);
			Assert.That (state.PropertyValues[property].GetValue<string> (), Is.Not.Null.And.Not.Empty.And.Not.EqualTo ("--"));
			}
		}

	/// <summary>Checks local read-only controls and cloud-history visibility against the actual UI bindings.</summary>
	[Test, Category ("LocalLive")]
	public void LocalSite_UiDisablesWritesAndHidesCloudOnlyFeatures ()
		{
		RequireLocal ();
		var state = _driver.GetState ();
		Assert.That (state.PropertyValues["controlsEnabled"].GetValue<bool> (), Is.False);
		Assert.That (state.PropertyValues["historyVisible"].GetValue<bool> (), Is.False);
		Assert.That (state.PropertyValues["stormWatchVisible"].GetValue<bool> (), Is.False);
		Assert.That (_driver.GridSettingsVisible, Is.EqualTo (_settings.LocalProtocol != "Gateway"));
		Assert.That (typeof (TeslaPowerwallDriver).GetField ("_historyClient", PRIVATE).GetValue (_driver), Is.Null);
		XDocument ui = LoadUi ();
		foreach (string id in new[] { "BackupReserveControl", "OperationModeSelector" })
			Assert.That ((string)ui.Descendants ().Single (node => (string)node.Attribute ("id") == id).Attribute ("visible"), Is.EqualTo ("{operatingSettingsVisible}"));
		foreach (string id in new[] { "BackupReserveControl", "GridChargingToggle", "StormWatchToggle", "OperationModeSelector", "GridExportSelector" })
			Assert.That ((string)ui.Descendants ().Single (node => (string)node.Attribute ("id") == id).Attribute ("enabled"), Is.EqualTo ("{controlsEnabled}"));
		foreach (string id in new[] { "EnergyStatusRow", "ImpactStatusRow" })
			Assert.That ((string)ui.Descendants ().Single (node => (string)node.Attribute ("id") == id).Attribute ("visible"), Is.EqualTo ("{historyVisible}"));
		}

	/// <summary>Checks tile and settings navigation plus the live connection label in the shipped layout.</summary>
	[Test, Category ("LocalLive")]
	public void LocalSite_UiNavigationAndConnectionLabelAreAvailable ()
		{
		RequireLocal ();
		XDocument ui = LoadUi ();
		Assert.That ((string)ui.Root.Element ("tile").Attribute ("navigation"), Is.EqualTo ("show:MainPage"));
		foreach (XAttribute link in ui.Descendants ().Attributes ("navigation"))
			{
			string target = link.Value;
			Assert.That (target, Does.StartWith ("show:"));
			Assert.That (ui.Descendants ("layout").Any (layout => (string)layout.Attribute ("id") == target.Substring (5)), Is.True);
			}
		Assert.That ((string)ui.Descendants ().Single (node => (string)node.Attribute ("id") == "OpenSettingsButton").Attribute ("navigation"), Is.EqualTo ("show:SettingsPage"));
		Assert.That (_driver.ConnectionDisplay, Does.Contain (_settings.LocalProtocol).And.Contain (_settings.Host)
			.And.Contain (_settings.LocalSettings ().IntervalSeconds + " seconds"));
		}

	private void RequireLocal ()
		{
		if (_settings.Mode != "local") Assert.Ignore ("Select local inputs for local UI coverage.");
		}

	private static XDocument LoadUi () => XDocument.Load (Path.Combine (TestSupport.DataDirectory, "uidefinitions", "UiDefinition.xml"));
	}
