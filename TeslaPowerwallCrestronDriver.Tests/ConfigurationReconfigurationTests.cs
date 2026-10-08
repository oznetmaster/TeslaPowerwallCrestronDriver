// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Linq;

using NUnit.Framework;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;

namespace TeslaPowerwallCrestronDriver.Tests;

/// <summary>Exercises the SDK configuration boundary with synthetic credentials and no network client.</summary>
[TestFixture, FixtureLifeCycle (LifeCycle.InstancePerTestCase), Category ("Processor")]
public sealed class ConfigurationReconfigurationTests
	{
	/// <summary>The isolated SDK logger.</summary>
	private DriverLogger _logger;
	/// <summary>The configuration controller under test.</summary>
	private DelegateDataDrivenConfigurationController _controller;
	/// <summary>Records callback values without creating a Powerwall connection.</summary>
	private readonly List<Dictionary<string, DriverEntityValue?>> _received = new ();
	/// <summary>Records SDK apply and clear actions.</summary>
	private readonly List<DataDrivenConfigurationController.ApplyConfigurationAction> _actions = new ();

	/// <summary>Creates the SDK state machine from the real production manifest.</summary>
	[SetUp]
	public void SetUp ()
		{
#if NETFRAMEWORK
		if (Type.GetType ("Mono.Runtime") == null)
			Assert.Ignore ("Requires the SDK desktop harness or the processor runtime.");
#endif
		_logger = new DriverLogger ("powerwall-reconfiguration-test");
		_controller = CreateController ();
		}

	/// <summary>Releases the isolated logger.</summary>
	[TearDown]
	public void TearDown () => _logger?.Dispose ();

	/// <summary>Builds a fresh controller whose apply callback cannot perform network work.</summary>
	/// <returns>The isolated controller.</returns>
	private DelegateDataDrivenConfigurationController CreateController ()
		{
		var args = new DriverControllerCreationArgs ("powerwall-reconfiguration-test", TestSupport.DataDirectory, _logger.AppLogger, null);
		return new DelegateDataDrivenConfigurationController (
			DataDrivenConfigurationControllerArgs.FromResources (args, TestSupport.Resources (_logger), DriverController.RootControllerId),
			(action, step, values) =>
				{
				_actions.Add (action);
				_received.Add (values == null ? null : new Dictionary<string, DriverEntityValue?> (values));
				return null;
				}, null, null);
		}

	/// <summary>Returns synthetic values matching the original cloud-only manifest.</summary>
	/// <param name="fleet">Whether the synthetic account uses Fleet.</param>
	/// <returns>The former four-field configuration.</returns>
	private static Dictionary<string, string> LegacyValues (bool fleet) => new ()
		{
		["ClientId"] = fleet ? "synthetic-application" : string.Empty,
		["RefreshToken"] = "synthetic-refresh-token",
		["SiteId"] = "123456789",
		["RefreshIntervalSeconds"] = "60"
		};

	/// <summary>Observes restoration of the previous version's persisted cloud fields.</summary>
	/// <param name="fleet">Whether Fleet is selected by the legacy client ID.</param>
	[TestCase (false), TestCase (true)]
	public void LegacyCloudValuesRemainAvailableWhenNewManifestLoads (bool fleet)
		{
		var values = LegacyValues (fleet);
		var result = _controller.ApplyConfiguration (values);
		Assert.That (result.ConfigurationErrorsByItemId, Is.Empty);
		var stored = _controller.GetAllConfigurationValues ().ConfigurationSettings;
		foreach (var pair in values)
			Assert.That (stored[pair.Key], Is.EqualTo (pair.Value), pair.Key);
		Assert.That (stored.ContainsKey ("LocalRefreshIntervalSeconds"), Is.False,
			"Restoring old fields does not persist newly introduced defaults automatically.");
		}

	/// <summary>Documents the SDK reset when a caller restarts the first configuration step.</summary>
	[Test]
	public void OpeningFirstStepClearsPreviouslyStoredFields ()
		{
		_controller.ApplyConfiguration (LegacyValues (false));
		var step = _controller.GetFirstConfigurationStep ();
		var token = step.ConfigurationItems.Single (item => item.Id == "RefreshToken");
		Assert.That (token.Value.Masked, Is.True);
		Assert.That (token.Value.CurrentValue, Is.Null);
		Assert.That (_controller.GetAllConfigurationValues ().ConfigurationSettings, Is.Empty);
		Assert.That (_actions.Last (), Is.EqualTo (DataDrivenConfigurationController.ApplyConfigurationAction.ClearValues));
		}

	/// <summary>Checks whether applying a changed interval preserves an omitted token.</summary>
	[Test]
	public void PartialUpdateRetainsOmittedCredential ()
		{
		_controller.ApplyConfiguration (LegacyValues (false));
		_controller.ApplyConfiguration (new Dictionary<string, string> { ["RefreshIntervalSeconds"] = "120" });
		var stored = _controller.GetAllConfigurationValues ().ConfigurationSettings;
		Assert.That (stored["RefreshToken"], Is.EqualTo ("synthetic-refresh-token"));
		Assert.That (stored["RefreshIntervalSeconds"], Is.EqualTo ("120"));
		}

	/// <summary>Checks whether a submitted blank is distinct from an omitted credential.</summary>
	[Test]
	public void ExplicitBlankCredentialDoesNotRecoverPreviousSecret ()
		{
		_controller.ApplyConfiguration (LegacyValues (false));
		_controller.ApplyConfiguration (new Dictionary<string, string> { ["RefreshToken"] = string.Empty });
		Assert.That (_controller.GetAllConfigurationValues ().ConfigurationSettings["RefreshToken"], Is.Empty);
		}

	/// <summary>Checks that the SDK export can be restored into a newly created controller.</summary>
	[Test]
	public void SavedConfigurationRoundTripRetainsCloudValues ()
		{
		var values = LegacyValues (true);
		_controller.ApplyConfiguration (values);
		var replacement = CreateController ();
		replacement.ApplyConfiguration (_controller.GetAllConfigurationValues ().ConfigurationSettings);
		var restored = replacement.GetAllConfigurationValues ().ConfigurationSettings;
		foreach (var pair in values)
			Assert.That (restored[pair.Key], Is.EqualTo (pair.Value), pair.Key);
		}
	/// <summary>Checks the SDK reset for each connection family's stored fields.</summary>
	/// <param name="mode">The connection being reconfigured.</param>
	[TestCase ("Cloud"), TestCase ("Gateway"), TestCase ("Signed LAN")]
	public void ReopeningClearsLocalCredentialsAndPermissionsToo (string mode)
		{
		var values = LegacyValues (false);
		values["ConnectionMode"] = mode;
		values["LocalHost"] = "192.0.2.10";
		values["LocalPassword"] = "synthetic-local-password";
		values["LocalSigningKey"] = "synthetic-signing-key";
		values["LocalRefreshIntervalSeconds"] = "30";
		values["AllowLocalControl"] = "true";
		values["UseCloudHistory"] = "false";
		_controller.ApplyConfiguration (values);
		Assert.That (_controller.GetAllConfigurationValues ().ConfigurationSettings["LocalSigningKey"], Is.EqualTo ("synthetic-signing-key"));
		_controller.GetFirstConfigurationStep ();
		Assert.That (_controller.GetAllConfigurationValues ().ConfigurationSettings, Is.Empty);
		Assert.That (_actions.Last (), Is.EqualTo (DataDrivenConfigurationController.ApplyConfigurationAction.ClearValues));
		}

	/// <summary>Checks a caller-owned snapshot survives an SDK reset and can populate the complete new form.</summary>
	/// <param name="fleet">Whether the prior cloud account uses Fleet.</param>
	[TestCase (false), TestCase (true)]
	public void ExplicitSnapshotAndNewDefaultsCanPopulateReconfiguration (bool fleet)
		{
		_controller.ApplyConfiguration (LegacyValues (fleet));
		var snapshot = new Dictionary<string, string> (_controller.GetAllConfigurationValues ().ConfigurationSettings);
		var step = _controller.GetFirstConfigurationStep ();
		Assert.That (_controller.GetAllConfigurationValues ().ConfigurationSettings, Is.Empty);
		var submission = step.ConfigurationItems.ToDictionary (item => item.Id, item => item.Value.DefaultValue ?? string.Empty);
		foreach (var pair in snapshot)
			submission[pair.Key] = pair.Value;
		Assert.That (submission["ConnectionMode"], Is.EqualTo ("Cloud"));
		Assert.That (submission["LocalRefreshIntervalSeconds"], Is.EqualTo ("15"));
		var result = _controller.ApplyConfigurationStep (step.Id, submission);
		Assert.That (result.ConfigurationErrorsByItemId, Is.Empty);
		Assert.That (result.NextConfigurationStep, Is.Null);
		var saved = _controller.GetAllConfigurationValues ().ConfigurationSettings;
		Assert.That (saved["RefreshToken"], Is.EqualTo (snapshot["RefreshToken"]));
		Assert.That (saved["ClientId"], Is.EqualTo (snapshot["ClientId"]));
		Assert.That (saved["LocalRefreshIntervalSeconds"], Is.EqualTo ("15"));
		Assert.That (_actions.Last (), Is.EqualTo (DataDrivenConfigurationController.ApplyConfigurationAction.ApplyStep));
		Assert.That (_received.Last ()["RefreshToken"].Value.GetValue<string> (), Is.EqualTo (snapshot["RefreshToken"]));
		}
	}