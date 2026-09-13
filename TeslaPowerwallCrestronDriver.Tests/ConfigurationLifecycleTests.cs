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
	}