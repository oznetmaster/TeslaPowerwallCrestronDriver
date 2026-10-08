// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using NUnit.Framework;
using TeslaPowerwall.CrestronDriver;
using TeslaPowerwallLibrary;

namespace TeslaPowerwallCrestronDriver.Tests;

[TestFixture, Category ("Processor")]
public sealed class RefreshLifecycleTests
	{
	private DriverLogger _logger;
	private TeslaPowerwallDriver _driver;
	private Powerwall _client;
	private DelayedClient _transport;
	private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
	[SetUp]
	public void SetUp ()
		{
#if NETFRAMEWORK
		if (Type.GetType ("Mono.Runtime") == null) Assert.Ignore ("Requires the SDK desktop harness or processor runtime.");
#endif
		_logger = new DriverLogger ("powerwall-refresh-test");
		_driver = new TeslaPowerwallDriver (new DriverControllerCreationArgs ("powerwall-refresh-test", TestSupport.DataDirectory, _logger.AppLogger, null), TestSupport.Resources (_logger));
		_client = new Powerwall (new PowerwallOptions { Host = "192.0.2.1", Password = "synthetic" });
		_transport = new DelayedClient ();
		typeof (Powerwall).GetField ("_client", Private).SetValue (_client, _transport);
		Set ("_client", _client);
		}
	[TearDown]
	public void TearDown ()
		{
		_transport?.Release.TrySetResult (true);
		_driver?.Dispose ();
		_client?.Dispose ();
		_logger?.Dispose ();
		}
	private void Set (string field, object value) => typeof (TeslaPowerwallDriver).GetField (field, Private).SetValue (_driver, value);
	private object Field (string field) => typeof (TeslaPowerwallDriver).GetField (field, Private).GetValue (_driver);
	private object Call (string method, params object[] args) => typeof (TeslaPowerwallDriver).GetMethod (method, Private).Invoke (_driver, args);
	private void Clear () => Call ("ApplyConfigurationItems", DataDrivenConfigurationController.ApplyConfigurationAction.ClearValues, null, null);
	[TestCase (false, false)]
	[TestCase (true, false)]
	[TestCase (false, true)]
	[TestCase (true, true)]
	public void RefreshedTokensOnlyApplyToTheCurrentClient (bool fleet, bool stale)
		{
		Set ("_refreshToken", "current-token");
		Set ("_pendingRefreshToken", "current-token");
		object sender = stale ? new object () : _client;
		Call (fleet ? "OnFleetApiTokensRefreshed" : "OnCloudTokensRefreshed", sender,
			fleet ? (object)new FleetApiTokensRefreshedEventArgs ("unused", "new-token") : new CloudTokensRefreshedEventArgs ("unused", "new-token"));
		Assert.That (Field ("_refreshToken"), Is.EqualTo (stale ? "current-token" : "new-token"));
		Assert.That (Field ("_pendingRefreshToken"), Is.EqualTo (stale ? "current-token" : "new-token"));
		}
	[TestCase (false)]
	[TestCase (true)]
	public void LateTokenCannotRestoreClearedCredentials (bool fleet)
		{
		Clear ();
		Call (fleet ? "OnFleetApiTokensRefreshed" : "OnCloudTokensRefreshed", _client,
			fleet ? (object)new FleetApiTokensRefreshedEventArgs ("unused", "late-token") : new CloudTokensRefreshedEventArgs ("unused", "late-token"));
		Assert.That (Field ("_refreshToken"), Is.EqualTo (string.Empty));
		Assert.That (Field ("_pendingRefreshToken"), Is.EqualTo (string.Empty));
		}
	[TestCase (false, false)]
	[TestCase (true, false)]
	[TestCase (false, true)]
	[TestCase (true, true)]
	public async Task LatePollCannotOverwriteClearOrDisposedState (bool dispose, bool failure)
		{
		_transport.Fail = failure;
		var poll = (Task)Call ("PollAsync", _client, CancellationToken.None);
		try
			{
			await TestSupport.Complete (_transport.Entered.Task);
			if (dispose) _driver.Dispose (); else Clear ();
			}
		finally { _transport.Release.TrySetResult (true); }
		string status = _driver.StatusSummary;
		try { await TestSupport.Complete (poll); }
		catch (OperationCanceledException) { }
		Assert.That (_driver.StatusSummary, Is.EqualTo (status));
		Assert.That (_driver.ReadyIndicatorIsReady, Is.False);
		Assert.That (_transport.Reads, Is.EqualTo (1), "Superseded polling must not continue issuing reads.");
		}
	/// <summary>Checks that timer and user refreshes cannot overlap network reads.</summary>
	[Test]
	public async Task ConcurrentRefreshIsSkipped ()
		{
		var first = (Task)Call ("RunPollAsync", CancellationToken.None);
		try
			{
			await TestSupport.Complete (_transport.Entered.Task);
			await TestSupport.Complete ((Task)Call ("RunPollAsync", CancellationToken.None));
			Assert.That (_transport.Reads, Is.EqualTo (1));
			Clear ();
			}
		finally
			{
			_transport.Release.TrySetResult (true);
			}
		try
			{
			await TestSupport.Complete (first);
			}
		catch (OperationCanceledException)
			{
			}
		Assert.That (Field ("_refreshInProgress"), Is.EqualTo (0));
		}

	/// <summary>Checks that a local-only connection never requests cloud history.</summary>
	[Test]
	public async Task LocalWithoutCloudCredentialsDoesNotReadHistory ()
		{
		var errors = new Dictionary<string, string> ();
		var settings = new LocalConnectionSettings ().Merge (new Dictionary<string, Crestron.DeviceDrivers.EntityModel.Data.DriverEntityValue?>
			{ ["ConnectionMode"] = new Crestron.DeviceDrivers.EntityModel.Data.DriverEntityValue ("Gateway") }, errors);
		Set ("_local", settings);
		await (Task)Call ("RefreshHistoryIfDueAsync", _client, CancellationToken.None);
		Assert.That (_transport.Reads, Is.Zero);
		Assert.That (Field ("_historyClient"), Is.Null);
		}

	/// <summary>Checks that unchanged history is not read at every local refresh.</summary>
	[Test]
	public async Task HistoryCadenceSkipsUnchangedSelection ()
		{
		Set ("_lastHistorySelection", (string)Call ("HistorySelection"));
		Set ("_nextHistoryRefresh", DateTimeOffset.UtcNow.AddMinutes (5));
		await (Task)Call ("RefreshHistoryIfDueAsync", _client, CancellationToken.None);
		Assert.That (_transport.Reads, Is.Zero);
		}

	/// <summary>Hides missing reserve and mode instead of showing constructor defaults or stale settings.</summary>
	[Test]
	public void MissingOperatingSettingsHideControlsUntilReportedAgain ()
		{
		var power = new TeslaPowerwallLibrary.Models.PowerReadings { Site = 100, Solar = 200, Load = 300, Battery = 0 };
		Call ("ApplyPolledState", 50d, power, null, 20d, "self_consumption", null, null, null);
		Assert.That (_driver.OperatingSettingsVisible, Is.True);
		Call ("ApplyPolledState", 50d, power, null, null, null, null, null, null);
		Assert.That (_driver.OperatingSettingsVisible, Is.False);
		Call ("ApplyPolledState", 50d, power, null, 0d, "backup", null, null, null);
		Assert.That (_driver.OperatingSettingsVisible, Is.True, "A real zero reserve is available, not missing.");
		Assert.That (_driver.BackupReservePercent, Is.Zero);
		}

	private sealed class DelayedClient : PowerwallClientBase
		{
		internal DelayedClient () : base ("test@example.invalid") { }
		internal readonly TaskCompletionSource<bool> Entered = new (TaskCreationOptions.RunContinuationsAsynchronously);
		internal readonly TaskCompletionSource<bool> Release = new (TaskCreationOptions.RunContinuationsAsynchronously);
		internal bool Fail;
		internal int Reads;
		public override async Task<string> PollAsync (string api, bool force = false, bool recursive = false, CancellationToken cancellationToken = default)
			{
			Reads++;
			Assert.That (api, Is.EqualTo ("/api/system_status/soe"));
			Entered.TrySetResult (true);
			await Release.Task;
			if (Fail) throw new PowerwallException ("Synthetic late failure");
			return "{\"percentage\":75}";
			}
		public override Task AuthenticateAsync (CancellationToken cancellationToken = default) => Task.CompletedTask;
		public override Task CloseSessionAsync (CancellationToken cancellationToken = default) => Task.CompletedTask;
		public override Task<byte[]> PollRawAsync (string api, bool force = false, bool recursive = false, CancellationToken cancellationToken = default) => throw new AssertionException ("Unexpected raw read");
		public override Task<string> PostAsync (string api, object payload, string din = null, bool recursive = false, CancellationToken cancellationToken = default) => throw new AssertionException ("Tests must not operate a Powerwall");
		public override Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>>> VitalsAsync (CancellationToken cancellationToken = default) => throw new AssertionException ("Unexpected vitals read");
		public override Task<double?> GetTimeRemainingAsync (CancellationToken cancellationToken = default) => throw new AssertionException ("Unexpected time read");
		}
	}
