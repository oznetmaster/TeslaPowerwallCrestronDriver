// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.SDK;
using NUnit.Framework;
using TeslaPowerwall.CrestronDriver;

namespace TeslaPowerwallCrestronDriver.Tests;

/// <summary>Checks accepted-configuration presentation without starting a client or polling loop.</summary>
[TestFixture, Category ("Processor")]
public sealed class RefreshNowVisibilityTests
	{
	private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
	private DriverLogger _logger;
	private TeslaPowerwallDriver _driver;

	[SetUp]
	public void SetUp ()
		{
#if NETFRAMEWORK
		if (Type.GetType ("Mono.Runtime") == null)
			Assert.Ignore ("Requires the SDK desktop harness or processor runtime.");
#endif
		_logger = new DriverLogger ("refresh-now-visibility-test");
		_driver = new TeslaPowerwallDriver (
			new DriverControllerCreationArgs ("refresh-now-visibility-test", TestSupport.DataDirectory, _logger.AppLogger, null),
			TestSupport.Resources (_logger));
		}

	[TearDown]
	public void TearDown ()
		{
		_driver?.Dispose ();
		_logger?.Dispose ();
		}

	private void Present (string connection, bool history, bool fleet)
		{
		// Supply a synthetic accepted snapshot to the real presentation boundary.
		// No credentials, client, refresh loop or connection validation is exercised here.
		var local = new LocalConnectionSettings ();
		typeof (LocalConnectionSettings).GetProperty ("Connection", Private).SetValue (local, connection);
		typeof (LocalConnectionSettings).GetProperty ("UseCloudHistory", Private).SetValue (local, history);
		typeof (TeslaPowerwallDriver).GetField ("_local", Private).SetValue (_driver, local);
		typeof (TeslaPowerwallDriver).GetField ("_clientId", Private).SetValue (_driver, fleet ? "synthetic-fleet" : "");
		typeof (TeslaPowerwallDriver).GetField ("_refreshToken", Private).SetValue (_driver, "synthetic");
		typeof (TeslaPowerwallDriver).GetField ("_siteId", Private).SetValue (_driver, "123456");
		typeof (TeslaPowerwallDriver).GetMethod ("UpdateConnectionPresentation", Private).Invoke (_driver, null);
		}

	[TestCase ("Cloud", false, false, true)]
	[TestCase ("Cloud", false, true, true)]
	[TestCase ("Gateway", false, false, false)]
	[TestCase ("Signed LAN", false, false, false)]
	[TestCase ("Gateway", true, false, false)]
	[TestCase ("Signed LAN", true, false, false)]
	public void VisibilityFollowsConnectionWithoutChangingPolling (string connection, bool history, bool fleet, bool visible)
		{
		Present (connection, history, fleet);
		Assert.That (_driver.RefreshNowVisible, Is.EqualTo (visible));
		Assert.That (_driver.HistoryVisible, Is.EqualTo (connection == "Cloud" || history));
		Assert.That ((int)typeof (TeslaPowerwallDriver).GetProperty ("PollIntervalSeconds", Private).GetValue (_driver),
			Is.EqualTo (connection == "Cloud" ? 60 : 15));
		Assert.That (typeof (TeslaPowerwallDriver).GetField ("_client", Private).GetValue (_driver), Is.Null);
		Assert.That (typeof (TeslaPowerwallDriver).GetField ("_refreshCancellationTokenSource", Private).GetValue (_driver), Is.Null);
		}

	[Test]
	public void ReconfigurationUpdatesVisibilityInBothDirections ()
		{
		Present ("Cloud", false, false);
		Assert.That (_driver.RefreshNowVisible, Is.True);
		Present ("Gateway", true, false);
		Assert.That (_driver.RefreshNowVisible, Is.False);
		Assert.That (_driver.HistoryVisible, Is.True);
		Present ("Cloud", false, true);
		Assert.That (_driver.RefreshNowVisible, Is.True);
		Present ("Signed LAN", false, false);
		Assert.That (_driver.RefreshNowVisible, Is.False);
		Present ("Cloud", false, false);
		Assert.That (_driver.RefreshNowVisible, Is.True);
		}

	[Test]
	public void OnlyRefreshButtonUsesModeVisibilityAndCommandRemainsAvailable ()
		{
		var xml = XDocument.Load (Path.Combine (TestSupport.DataDirectory, "uidefinitions", "UiDefinition.xml"));
		var button = xml.Descendants ().Single (x => (string)x.Attribute ("id") == "RefreshNowButton");
		Assert.That ((string)button.Attribute ("visible"), Is.EqualTo ("{refreshNowVisible}"));
		Assert.That ((string)button.Attribute ("action"), Is.EqualTo ("command:refreshNow"));
		Assert.That (xml.Descendants ().Count (x => (string)x.Attribute ("visible") == "{refreshNowVisible}"), Is.EqualTo (1));
		Assert.That (typeof (TeslaPowerwallDriver).GetMethod ("RefreshNow"), Is.Not.Null);
		}
	}
