// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Net;
using System.Net.Http;
using TeslaPowerwallLibrary.Cloud;
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
	private void Property (string name, object value) => typeof (TeslaPowerwallDriver).GetProperty (name).SetValue (_driver, value);
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


	private CancellationTokenSource ConfigureLocal (int interval = 15)
		{
		var settings = new LocalConnectionSettings ().Merge (new Dictionary<string, Crestron.DeviceDrivers.EntityModel.Data.DriverEntityValue?>
			{ ["ConnectionMode"] = new Crestron.DeviceDrivers.EntityModel.Data.DriverEntityValue ("Gateway"),
			  ["LocalRefreshIntervalSeconds"] = new Crestron.DeviceDrivers.EntityModel.Data.DriverEntityValue (interval) }, new Dictionary<string, string> ());
		Set ("_local", settings);
		var cts = new CancellationTokenSource ();
		return cts;
		}

	[Test]
	public async Task FailedPollPublishesOfflineInsteadOfStaleTile ()
		{
		Property ("TileDisplay", "4.6 kW");
		Property ("OnlineIndicatorIsOnline", true);
		Property ("ReadyIndicatorIsReady", true);
		_transport.Fail = true;
		_transport.Release.TrySetResult (true);
		await TestSupport.Complete ((Task)Call ("PollAsync", _client, CancellationToken.None));
		Assert.That (_driver.TileDisplay, Is.EqualTo ("Offline"));
		Assert.That (_driver.OnlineIndicatorIsOnline, Is.False);
		Assert.That (_driver.ReadyIndicatorIsReady, Is.False);
		}





	[Test]
 public async Task FailedFreshApiCannotPublishCachedData()
 {
  using var cts = ConfigureLocal();
  _transport.CompletePoll = true;
  _transport.Fail = true;
  _transport.Release.TrySetResult(true);
  Property("TileDisplay", "cached 4.6 kW");
  Property("OnlineIndicatorIsOnline", true);
  await TestSupport.Complete((Task)Call("PollAsync",_client,cts.Token));
  Assert.That(_transport.Reads, Is.EqualTo(1), "No cached projections may publish after the required fresh API fails.");
  Assert.That(_transport.ForceReads, Is.EqualTo(1));
  Assert.That(_driver.TileDisplay, Is.EqualTo("Offline"));
  Assert.That(_driver.OnlineIndicatorIsOnline, Is.False);
 }


	[Test]
	public async Task FreshSuccessfulApiAndDataPollRestoreOfflineTile ()
		{
		using var cts = ConfigureLocal ();
		_transport.CompletePoll = true;
		_transport.Release.TrySetResult (true);
		Call ("SetOfflineState", "Synthetic failed required API observation");
		await TestSupport.Complete ((Task)Call ("PollAsync", _client, CancellationToken.None));
		Assert.That (_transport.ForceReads, Is.EqualTo (1));
		Assert.That (_transport.SoeReads, Is.EqualTo (1), "The required fresh battery reading must not be issued again.");
		Assert.That (_driver.TileDisplay, Does.Contain ("0.3 kW"));
		Assert.That (_driver.OnlineIndicatorIsOnline, Is.True);
		Assert.That (_driver.ReadyIndicatorIsReady, Is.True);
		}

	[Test]
	public async Task RequiredFreshApiFailureStaysOffline ()
		{
		using var cts = ConfigureLocal ();
		_transport.Fail = true;
		_transport.Release.TrySetResult (true);
		await TestSupport.Complete ((Task)Call ("PollAsync", _client, CancellationToken.None));
		Assert.That (_transport.ForceReads, Is.EqualTo (1));
		Assert.That (_driver.TileDisplay, Is.EqualTo ("Offline"));
		Assert.That (_driver.ReadyIndicatorIsReady, Is.False);
		}


	[Test]
	public async Task ConnectionLossPreservesCachedHistoricalValuesAcrossRecovery ()
		{
		using var cts = ConfigureLocal ();
		EnableSyntheticHistory (new DelayedHistoryHandler ());
		Set ("_lastHistorySelection", (string)Call ("HistorySelection"));
		Set ("_nextHistoryRefresh", DateTimeOffset.UtcNow.AddMinutes (5));
		Property ("EnergyValueDisplay", "Solar: 5.0 kWh");
		Property ("ImpactSummaryDisplay", "Historical summary");
		_transport.CompletePoll = true;
		_transport.Release.TrySetResult (true);
		Call ("SetOfflineState", "Synthetic failed required API observation");
		Assert.That (_driver.EnergyValueDisplay, Is.EqualTo ("Solar: 5.0 kWh"));
		await TestSupport.Complete ((Task)Call ("PollAsync", _client, CancellationToken.None));
		Assert.That (_driver.EnergyValueDisplay, Is.EqualTo ("Solar: 5.0 kWh"));
		Assert.That (_driver.ImpactSummaryDisplay, Is.EqualTo ("Historical summary"));
		}

	[Test]
	public async Task SlowHistoryCannotDelayLivePublicationOrUndoClearedConfiguration ()
		{
		using var cts = ConfigureLocal ();
		var history = new DelayedHistoryHandler ();
		EnableSyntheticHistory (history);
		_transport.CompletePoll = true;
		_transport.Release.TrySetResult (true);
		Task poll = (Task)Call ("PollAsync", _client, CancellationToken.None);
		try
			{
			await TestSupport.Complete (history.Entered.Task);
			Assert.That (poll.IsCompleted, Is.False, "Actual optional history read must still be blocked.");
			Assert.That (_driver.TileDisplay, Does.Contain ("0.3 kW"));
			Assert.That (_driver.OnlineIndicatorIsOnline, Is.True);
			Assert.That (_driver.ReadyIndicatorIsReady, Is.True);
			Clear ();
			}
		finally { history.Release.TrySetResult (true); }
		try { await TestSupport.Complete (poll); } catch (OperationCanceledException) { }
		Assert.That (_driver.StatusSummary, Is.EqualTo ("Configuration cleared"));
		Assert.That (_driver.OnlineIndicatorIsOnline, Is.False);
		Assert.That (_driver.ReadyIndicatorIsReady, Is.False);
		}




	[TestCase (true)]
	[TestCase (false)]
	public async Task RequestCancellationMustNotTerminateValidRefreshLifetime (bool initial)
		{
		using var cts = ConfigureLocal (3600);
		_transport.CompletePoll = true;
		_transport.CancelOnForcedRead = initial ? 1 : 2;
		_transport.Release.TrySetResult (true);
		var loop = (Task)Call ("RefreshLoopAsync", cts.Token);
		try
			{
			if (!initial)
				{
				await TestSupport.Complete (_transport.PollFinished.Task);
				((SemaphoreSlim)Field ("_refreshWake")).Release ();
				}
			await TestSupport.Complete (_transport.RequestCancelled.Task);
			await Task.Delay (100);
			Assert.That (cts.IsCancellationRequested, Is.False);
			Assert.That (loop.IsCompleted, Is.False, "A request-owned cancellation must not kill the driver lifetime.");
			Assert.That (_driver.ReadyIndicatorIsReady, Is.False);
			Assert.That (_driver.TileDisplay, Is.EqualTo ("Offline"));
			_transport.PollFinished = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
			((SemaphoreSlim)Field ("_refreshWake")).Release ();
			await TestSupport.Complete (_transport.PollFinished.Task);
			await Task.Delay (100);
			Assert.That (_driver.ReadyIndicatorIsReady, Is.True);
			Assert.That (_driver.OnlineIndicatorIsOnline, Is.True);
			Assert.That (_transport.ForceReads, Is.EqualTo (initial ? 2 : 3));
			}
		finally { cts.Cancel (); await TestSupport.Complete (loop); }
		}


	[Test]
	public async Task PinnedSignedLanClientPropagatesRequestTimeoutWithValidCallerToken ()
		{
		using var key = System.Security.Cryptography.RSA.Create (4096);
		var options = new PowerwallOptions { Host = "192.0.2.1", Password = "synthetic", LocalSigningKey = key, LocalProtocol = TeslaPowerwallLibrary.PowerwallLocalProtocol.TedapiSigned, Timeout = TimeSpan.FromMilliseconds (50) };
		var type = typeof (Powerwall).Assembly.GetType ("TeslaPowerwallLibrary.Tedapi.PowerwallTedapiClient");
		var handler = new TimeoutHandler ();
		using var transport = (TeslaPowerwallLibrary.Tedapi.PowerwallTedapiClient)Activator.CreateInstance (type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { options, handler, null }, null);
		type.GetField ("_din", Private).SetValue (transport, "1234567-00-A--SYNTHETIC");
		using var client = new Powerwall (options);
		typeof (Powerwall).GetField ("_client", Private).SetValue (client, transport);
		using var cts = new CancellationTokenSource ();
		await Assert.CatchAsync<OperationCanceledException> (async () => await client.IsConnectedAsync (cts.Token));
		Assert.That (cts.IsCancellationRequested, Is.False);
		Assert.That (handler.Reads, Is.EqualTo (1));
		}

	[Test]
	public async Task ExplicitLifetimeCancellationStopsSleepingRefreshWithoutAnotherRead ()
		{
		using var cts = ConfigureLocal (3600);
		_transport.CompletePoll = true;
		_transport.Release.TrySetResult (true);
		var loop = (Task)Call ("RefreshLoopAsync", cts.Token);
		await TestSupport.Complete (_transport.PollFinished.Task);
		await Task.Delay (50);
		int reads = _transport.Reads;
		cts.Cancel ();
		await TestSupport.Complete (loop);
		Assert.That (_transport.Reads, Is.EqualTo (reads));
		}

 [TestCase("{\"percentage\":75}", 73.6842105263158)]
 [TestCase("{\"percentage\":0}", -5.26315789473684)]
 public async Task ForcedFirstDataReadIsIssuedOnceAndUsesLibraryScale(string json,double expected)
 {
  _transport.SoePayload=json;_transport.Release.TrySetResult(true);
  double actual=await TeslaPowerwallDriver.ReadFreshLocalLevelAsync(_client,CancellationToken.None);
  Assert.That(actual,Is.EqualTo(expected).Within(0.000001));
  Assert.That(_transport.Reads,Is.EqualTo(1));Assert.That(_transport.ForceReads,Is.EqualTo(1));
 }
 [TestCase(null)] [TestCase("null")] [TestCase("{}")] [TestCase("{\"percentage\":\"invalid\"}")] [TestCase("not JSON")]
 public async Task InvalidFreshBatteryReadingCannotRestoreCachedTile(string payload)
 {
  using var cts=ConfigureLocal();_transport.SoePayload=payload ?? "null";_transport.Release.TrySetResult(true);
  Property("TileDisplay","cached");await TestSupport.Complete((Task)Call("PollAsync",_client,cts.Token));
  Assert.That(_transport.Reads,Is.EqualTo(1));Assert.That(_driver.TileDisplay,Is.EqualTo("Offline"));
  Assert.That(_driver.OnlineIndicatorIsOnline,Is.False);
 }
 [TestCase(15)] [TestCase(3600)]
 public async Task NormalRefreshRetainsConfiguredIdleAndMakesNoBackgroundHealthRequests(int interval)
 {
  using var cts=ConfigureLocal(interval);_transport.CompletePoll=true;_transport.Release.TrySetResult(true);
  var loop=(Task)Call("RefreshLoopAsync",cts.Token);
  try {
   await TestSupport.Complete(_transport.PollFinished.Task);
   await Task.Delay(100);int reads=_transport.Reads;
   await Task.Delay(150);Assert.That(_transport.Reads,Is.EqualTo(reads));
   Assert.That(_transport.SoeReads,Is.EqualTo(1));
   Assert.That((int)typeof(TeslaPowerwallDriver).GetProperty("PollIntervalSeconds",Private).GetValue(_driver),Is.EqualTo(interval));
  }finally{cts.Cancel();await TestSupport.Complete(loop);}
 }
 [Test]
 public async Task WholeLocalCoreBudgetCancelsStalledReadAndNextPollCanRecover()
 {
  using var cts=ConfigureLocal();_transport.RespectCancellation=true;
  var poll=(Task)Call("PollAsync",_client,cts.Token);await TestSupport.Complete(_transport.Entered.Task);
  Assert.That(await Task.WhenAny(poll,Task.Delay(TimeSpan.FromSeconds(20))),Is.SameAs(poll));await poll;
  Assert.That(cts.IsCancellationRequested,Is.False);Assert.That(_driver.TileDisplay,Is.EqualTo("Offline"));
  Assert.That(_transport.Reads,Is.EqualTo(1));
  _transport.CompletePoll=true;_transport.Release.TrySetResult(true);
  await TestSupport.Complete((Task)Call("PollAsync",_client,cts.Token));
  Assert.That(_driver.OnlineIndicatorIsOnline,Is.True);Assert.That(_driver.ReadyIndicatorIsReady,Is.True);
 }

 [TestCase(15)] [TestCase(60)] [TestCase(3600)]
 public async Task RepeatedLocalFailuresBackOffAndFreshSuccessResetsConfiguredDelay(int interval)
 {
  using var cts=ConfigureLocal(interval);
  _transport.Fail=true;_transport.Release.TrySetResult(true);
  int expected=interval;
  for(int failure=1;failure<=7;failure++)
  {
   await TestSupport.Complete((Task)Call("PollAsync",_client,cts.Token));
   Assert.That((int)typeof(TeslaPowerwallDriver).GetProperty("NextPollDelaySeconds",Private).GetValue(_driver),Is.EqualTo(expected));
   Assert.That((int)Field("_localPollFailures"),Is.EqualTo(failure),"One failed API poll counts once.");
   expected=Math.Min(Math.Max(interval,300),expected*2);
  }
  Assert.That(_transport.Reads,Is.EqualTo(7),"No independent health requests or cached projections after failed first read.");
  _transport.Fail=false;_transport.CompletePoll=true;
  await TestSupport.Complete((Task)Call("PollAsync",_client,cts.Token));
  Assert.That((int)Field("_localPollFailures"),Is.Zero);
  Assert.That((int)typeof(TeslaPowerwallDriver).GetProperty("NextPollDelaySeconds",Private).GetValue(_driver),Is.EqualTo(interval));
 }
 [Test]
 public async Task CloudFailureDoesNotChangeCloudCadence()
 {
  Set("_refreshIntervalSeconds",300);_transport.Fail=true;_transport.Release.TrySetResult(true);
  await TestSupport.Complete((Task)Call("PollAsync",_client,CancellationToken.None));
  Assert.That((int)Field("_localPollFailures"),Is.Zero);
  Assert.That((int)typeof(TeslaPowerwallDriver).GetProperty("NextPollDelaySeconds",Private).GetValue(_driver),Is.EqualTo(300));
 }
 [Test]
 public async Task SleepingFailureBackoffIsCancellableWithoutAnotherRead()
 {
  using var cts=ConfigureLocal(15);_transport.Fail=true;_transport.Release.TrySetResult(true);
  var loop=(Task)Call("RefreshLoopAsync",cts.Token);
  try {
   await TestSupport.Complete(_transport.Entered.Task);await Task.Delay(100);
   Assert.That((int)Field("_localPollFailures"),Is.EqualTo(1));
   int reads=_transport.Reads;await Task.Delay(150);Assert.That(_transport.Reads,Is.EqualTo(reads));
   cts.Cancel();await TestSupport.Complete(loop);Assert.That(_transport.Reads,Is.EqualTo(reads));
  }finally{cts.Cancel();await TestSupport.Complete(loop);}
 }
 private void EnableSyntheticControl(CancellationTokenSource cts)
 {
  var settings=((LocalConnectionSettings)Field("_local")).Merge(new Dictionary<string,Crestron.DeviceDrivers.EntityModel.Data.DriverEntityValue?>
   {["AllowLocalControl"]=new Crestron.DeviceDrivers.EntityModel.Data.DriverEntityValue(true)},new Dictionary<string,string>());
  Set("_local",settings);Set("_refreshCancellationTokenSource",cts);
 }
 [Test]
 public async Task CommandWaitsForWholePollAndLifetimeCancellationPreventsAction()
 {
  using var cts=ConfigureLocal();EnableSyntheticControl(cts);
  var poll=(Task)Call("RunPollAsync",cts.Token);await TestSupport.Complete(_transport.Entered.Task);
  int actions=0;Func<Powerwall,CancellationToken,Task> action=(client,token)=>{actions++;return Task.CompletedTask;};
  var command=(Task)Call("ExecuteControlAsync",action,(Action)(()=>{}),"synthetic command");
  await Task.Delay(100);Assert.That(actions,Is.Zero);Assert.That(command.IsCompleted,Is.False);
  cts.Cancel();await TestSupport.Complete(command);_transport.Release.TrySetResult(true);
  await Assert.CatchAsync<OperationCanceledException>(async()=>await TestSupport.Complete(poll));
  Assert.That(actions,Is.Zero);Assert.That(((SemaphoreSlim)Field("_connectionGate")).CurrentCount,Is.EqualTo(1));
 }
 [Test]
 public async Task PollWaitsForWholeCommandAndActionReceivesDriverLifetimeToken()
 {
  using var cts=ConfigureLocal();EnableSyntheticControl(cts);
  var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
  CancellationToken observed=default;
  Func<Powerwall,CancellationToken,Task> action=async(client,token)=>{observed=token;entered.TrySetResult(true);await Task.Delay(Timeout.Infinite,token);};
  var command=(Task)Call("ExecuteControlAsync",action,(Action)(()=>{}),"synthetic command");
  await TestSupport.Complete(entered.Task);var poll=(Task)Call("RunPollAsync",cts.Token);
  await Task.Delay(100);Assert.That(_transport.Reads,Is.Zero);Assert.That(observed,Is.EqualTo(cts.Token));
  cts.Cancel();await TestSupport.Complete(command);
  await Assert.CatchAsync<OperationCanceledException>(async()=>await TestSupport.Complete(poll));
  Assert.That(_transport.Reads,Is.Zero);Assert.That((int)Field("_refreshInProgress"),Is.Zero);
  Assert.That(((SemaphoreSlim)Field("_connectionGate")).CurrentCount,Is.EqualTo(1));
 }

	private sealed class TimeoutHandler : HttpMessageHandler
		{
		internal int Reads;
		protected override async Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
			{
			Assert.That (request.RequestUri.Host, Is.EqualTo ("192.0.2.1"));
			Reads++;
			await Task.Delay (Timeout.Infinite, cancellationToken);
			throw new AssertionException ("Synthetic stalled request unexpectedly completed");
			}
		}

	private void EnableSyntheticHistory (DelayedHistoryHandler handler)
		{
		var settings = ((LocalConnectionSettings)Field ("_local")).Merge (new Dictionary<string, Crestron.DeviceDrivers.EntityModel.Data.DriverEntityValue?>
			{ ["UseCloudHistory"] = new Crestron.DeviceDrivers.EntityModel.Data.DriverEntityValue (true) }, new Dictionary<string, string> ());
		Set ("_local", settings); Set ("_siteId", "1"); Set ("_refreshToken", "synthetic");
		var cloud = new PowerwallCloudClient ("synthetic@example.invalid", 15, TimeSpan.FromSeconds (15), accessToken: "synthetic", siteId: "1", noCloudTokenPersistence: true);
		Type connectionType = typeof (Powerwall).Assembly.GetType ("TeslaPowerwallLibrary.Cloud.TeslaCloudConnection");
		object connection = Activator.CreateInstance (connectionType, new object[] { "synthetic", null, TimeSpan.FromSeconds (15), null });
		var httpField = connectionType.GetField ("_httpClient", Private);
		((HttpClient)httpField.GetValue (connection)).Dispose ();
		httpField.SetValue (connection, new HttpClient (handler));
		typeof (PowerwallCloudClient).GetField ("_connection", Private).SetValue (cloud, connection);
		typeof (PowerwallCloudClient).GetField ("_resolvedSiteId", Private).SetValue (cloud, "1");
		var history = new Powerwall (new PowerwallOptions { CloudMode = true, RefreshToken = "synthetic", NoCloudTokenPersistence = true });
		typeof (Powerwall).GetField ("_client", Private).SetValue (history, cloud);
		Set ("_historyClient", history);
		}

	private sealed class DelayedHistoryHandler : HttpMessageHandler
		{
		internal readonly TaskCompletionSource<bool> Entered = new (TaskCreationOptions.RunContinuationsAsynchronously);
		internal readonly TaskCompletionSource<bool> Release = new (TaskCreationOptions.RunContinuationsAsynchronously);
		protected override async Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
			{
			Assert.That (request.Method, Is.EqualTo (HttpMethod.Get));
			Assert.That (request.RequestUri.AbsolutePath, Does.Contain ("calendar_history"));
			Entered.TrySetResult (true);
			await Release.Task;
			return new HttpResponseMessage (HttpStatusCode.OK) { Content = new StringContent ("{\"response\":{\"time_series\":[]}}") };
			}
		}

	private sealed class DelayedClient : PowerwallClientBase
		{
		internal DelayedClient () : base ("test@example.invalid") { }
		internal readonly TaskCompletionSource<bool> Entered = new (TaskCreationOptions.RunContinuationsAsynchronously);
		internal readonly TaskCompletionSource<bool> Release = new (TaskCreationOptions.RunContinuationsAsynchronously);
		internal bool Fail;
        internal bool RespectCancellation;
		internal bool CompletePoll;
		internal int ForceReads;
		internal int CancelOnForcedRead;
		internal readonly TaskCompletionSource<bool> RequestCancelled = new (TaskCreationOptions.RunContinuationsAsynchronously);
		internal TaskCompletionSource<bool> PollFinished = new (TaskCreationOptions.RunContinuationsAsynchronously);
		internal int Reads;
        internal int SoeReads;
        internal string SoePayload;
		public override async Task<string> PollAsync (string api, bool force = false, bool recursive = false, CancellationToken cancellationToken = default)
			{
			Reads++;
            if(api == "/api/system_status/soe") SoeReads++;
			if (force) { ForceReads++; if (ForceReads == CancelOnForcedRead) { RequestCancelled.TrySetResult (true); throw new TaskCanceledException ("Synthetic request timeout while lifetime remains active"); } }
			if (!CompletePoll) Assert.That (api, Is.EqualTo ("/api/system_status/soe"));
			Entered.TrySetResult (true);
            if(RespectCancellation && !Release.Task.IsCompleted) { var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); using var registration=cancellationToken.Register(()=>cancelled.TrySetCanceled()); await await Task.WhenAny(Release.Task,cancelled.Task); } else await Release.Task;
			if (Fail) throw new PowerwallException ("Synthetic late failure");
			if (api == "/api/operation") PollFinished.TrySetResult (true);
			return api switch
				{
				"/api/system_status/soe" => SoePayload ?? "{\"percentage\":75}",
				"/api/meters/aggregates" => "{\"site\":{\"instant_power\":100},\"solar\":{\"instant_power\":200},\"battery\":{\"instant_power\":0},\"load\":{\"instant_power\":300}}",
				"/api/system_status/grid_status" => "{\"grid_status\":\"SystemGridConnected\"}",
				"/api/operation" => "{\"backup_reserve_percent\":20,\"real_mode\":\"self_consumption\"}",
				_ => throw new AssertionException ("Unexpected read: " + api)
				};
			}
		public override Task AuthenticateAsync (CancellationToken cancellationToken = default) => Task.CompletedTask;
		public override Task CloseSessionAsync (CancellationToken cancellationToken = default) => Task.CompletedTask;
		public override Task<byte[]> PollRawAsync (string api, bool force = false, bool recursive = false, CancellationToken cancellationToken = default) => throw new AssertionException ("Unexpected raw read");
		public override Task<string> PostAsync (string api, object payload, string din = null, bool recursive = false, CancellationToken cancellationToken = default) => throw new AssertionException ("Tests must not operate a Powerwall");
		public override Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>>> VitalsAsync (CancellationToken cancellationToken = default) => throw new AssertionException ("Unexpected vitals read");
		public override Task<double?> GetTimeRemainingAsync (CancellationToken cancellationToken = default) => throw new AssertionException ("Unexpected time read");
		}
	}
