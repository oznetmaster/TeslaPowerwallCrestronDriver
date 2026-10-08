// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Reflection;

using NUnit.Framework;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;

using TeslaPowerwall.CrestronDriver;

namespace TeslaPowerwallCrestronDriver.Tests;

[TestFixture, FixtureLifeCycle (LifeCycle.InstancePerTestCase), Category ("Processor")]
public sealed class ConfigurationLifecycleTests
	{
	private DriverLogger _logger;
	private TeslaPowerwallDriver _driver;
	[SetUp]
	public void SetUp ()
		{
#if NETFRAMEWORK
		if (Type.GetType ("Mono.Runtime") == null)
			Assert.Ignore ("Requires the SDK desktop harness or the processor runtime.");
#endif
		_logger = new DriverLogger ("powerwall-configuration-test");
		var args = new DriverControllerCreationArgs ("powerwall-configuration-test", TestSupport.DataDirectory, _logger.AppLogger, null);
		_driver = new TeslaPowerwallDriver (args, TestSupport.Resources (_logger));
		}
	[TearDown]
	public void TearDown ()
		{
		_driver?.Dispose ();
		_logger?.Dispose ();
		}
	private FieldInfo Field (string name) => typeof (TeslaPowerwallDriver).GetField (name, BindingFlags.Instance | BindingFlags.NonPublic);
	private ConfigurationItemErrors Apply (Dictionary<string, DriverEntityValue?> values, bool clear = false) =>
		(ConfigurationItemErrors)typeof (TeslaPowerwallDriver).GetMethod ("ApplyConfigurationItems", BindingFlags.Instance | BindingFlags.NonPublic).Invoke (
			_driver, new object[] { clear ? DataDrivenConfigurationController.ApplyConfigurationAction.ClearValues : DataDrivenConfigurationController.ApplyConfigurationAction.ApplyAll, null, values });
	[TestCase ("")]
	[TestCase ("new-application")]
	public void ChangingAuthenticationModeOrApplication_CannotReusePriorToken (string clientId)
		{
		foreach (string field in new[] { "_clientId", "_pendingClientId" })
			Field (field).SetValue (_driver, "old-application");
		foreach (string field in new[] { "_refreshToken", "_pendingRefreshToken" })
			Field (field).SetValue (_driver, "synthetic-old-token");
		var errors = Apply (new Dictionary<string, DriverEntityValue?> { ["ClientId"] = new DriverEntityValue (clientId) });
		Assert.That (errors.ConfigurationErrorsByItemId.ContainsKey ("RefreshToken"), Is.True);
		Assert.That (Field ("_refreshToken").GetValue (_driver), Is.EqualTo ("synthetic-old-token"), "A rejected edit must leave active configuration intact.");
		Assert.That (Field ("_pendingRefreshToken").GetValue (_driver), Is.EqualTo (string.Empty));
		Assert.That (Field ("_refreshCancellationTokenSource").GetValue (_driver), Is.Null);
		}
	[TestCase (0, true)]
	[TestCase (29, true)]
	[TestCase (30, false)]
	[TestCase (3600, false)]
	[TestCase (3601, true)]
	[TestCase (int.MaxValue, true)]
	public void RefreshInterval_ValidatesBothBoundariesWithoutStartingNetworkWork (int seconds, bool invalid)
		{
		// Missing credentials guarantee no cloud activity, even for accepted intervals.
		var errors = Apply (new Dictionary<string, DriverEntityValue?> { ["RefreshIntervalSeconds"] = new DriverEntityValue ((long)seconds) });
		Assert.That (errors.ConfigurationErrorsByItemId.ContainsKey ("RefreshIntervalSeconds"), Is.EqualTo (invalid));
		Assert.That (errors.ConfigurationErrorsByItemId.ContainsKey ("RefreshToken"), Is.True);
		Assert.That (Field ("_refreshCancellationTokenSource").GetValue (_driver), Is.Null);
		}
	[Test]
	public void ClearConfiguration_RemovesActiveAndPendingCredentialsAndResetsIndicators ()
		{
		foreach (string field in new[] { "_clientId", "_pendingClientId", "_refreshToken", "_pendingRefreshToken", "_siteId", "_pendingSiteId" })
			Field (field).SetValue (_driver, "synthetic-test-value");
		Assert.That (Apply (null, clear: true), Is.Null);
		foreach (string field in new[] { "_clientId", "_pendingClientId", "_refreshToken", "_pendingRefreshToken", "_siteId", "_pendingSiteId" })
			Assert.That (Field (field).GetValue (_driver), Is.EqualTo (string.Empty), field);
		var state = _driver.GetState ();
		Assert.That (state.PropertyValues["onlineIndicator:isOnline"].GetValue<bool> (), Is.False);
		Assert.That (state.PropertyValues["readyIndicator:isReady"].GetValue<bool> (), Is.False);
		Assert.That (Apply (new Dictionary<string, DriverEntityValue?> ()).ConfigurationErrorsByItemId.Keys, Is.EquivalentTo (new[] { "RefreshToken" }));
		}
	/// <summary>Checks independent local configuration and explicit cloud history enablement.</summary>
	/// <param name="history">Whether history was enabled by the installer.</param>
	[TestCase (false)]
	[TestCase (true)]
	public void LocalCloudHistoryRequiresExplicitEnablementAndExactSite (bool history)
		{
		var errors = Apply (new Dictionary<string, DriverEntityValue?>
			{
			["ConnectionMode"] = new DriverEntityValue ("Gateway"),
			["LocalHost"] = new DriverEntityValue ("192.0.2.1"),
			["UseCloudHistory"] = new DriverEntityValue (history.ToString ())
			});
		Assert.That (errors.ConfigurationErrorsByItemId.ContainsKey ("LocalPassword"), Is.True);
		Assert.That (errors.ConfigurationErrorsByItemId.ContainsKey ("RefreshToken"), Is.EqualTo (history));
		Assert.That (errors.ConfigurationErrorsByItemId.ContainsKey ("SiteId"), Is.EqualTo (history));
		Assert.That (Field ("_refreshCancellationTokenSource").GetValue (_driver), Is.Null);
		}

	/// <summary>Checks that local credentials and intervals clear along with cloud credentials.</summary>
	[Test]
	public void ClearRemovesPendingLocalCredentials ()
		{
		Apply (new Dictionary<string, DriverEntityValue?>
			{
			["ConnectionMode"] = new DriverEntityValue ("Signed LAN"),
			["LocalPassword"] = new DriverEntityValue ("synthetic-local-password"),
			["LocalSigningKey"] = new DriverEntityValue ("synthetic-key"),
			["LocalRefreshIntervalSeconds"] = new DriverEntityValue (60L)
			});
		Apply (null, clear: true);
		foreach (string name in new[] { "_local", "_pendingLocal" })
			{
			var settings = (LocalConnectionSettings)Field (name).GetValue (_driver);
			Assert.That (settings.Password, Is.Empty);
			Assert.That (settings.SigningKey, Is.Empty);
			Assert.That (settings.IsLocal, Is.False);
			Assert.That (settings.IntervalSeconds, Is.EqualTo (15));
			}
		}

	/// <summary>Blank optional cloud intervals keep the default when the SDK submits an unused field.</summary>
	/// <param name="value">The empty installer field representation.</param>
	[TestCase (""), TestCase (" ")]
	public void EmptyOptionalCloudIntervalRetainsDefault (string value)
		{
		var errors = Apply (new Dictionary<string, DriverEntityValue?>
			{
			["ConnectionMode"] = new DriverEntityValue ("Gateway"),
			["RefreshIntervalSeconds"] = new DriverEntityValue (value)
			});
		Assert.That (errors.ConfigurationErrorsByItemId.ContainsKey ("RefreshIntervalSeconds"), Is.False);
		Assert.That (errors.ConfigurationErrorsByItemId.ContainsKey ("LocalPassword"), Is.True,
			"Missing local credentials prevent network activity in this regression test.");
		Assert.That (Field ("_pendingRefreshIntervalSeconds").GetValue (_driver), Is.EqualTo (60));
		Assert.That (Field ("_refreshCancellationTokenSource").GetValue (_driver), Is.Null);
		}
	}
