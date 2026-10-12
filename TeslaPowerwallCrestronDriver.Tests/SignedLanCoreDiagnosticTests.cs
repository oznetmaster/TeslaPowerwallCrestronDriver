// Copyright (c) 2026 Neil Colvin. MIT with Commons Clause; see LICENSE.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using Google.Protobuf;
using NUnit.Framework;
using TeslaPowerwall.CrestronDriver;
using TeslaPowerwallLibrary;
using TeslaPowerwallLibrary.Tedapi;
namespace TeslaPowerwallCrestronDriver.Tests;
[TestFixture, Category("Offline")]
public sealed class SignedLanCoreDiagnosticTests
{
 private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
 [Test]
 public async Task FullSignedCoreUsesTwoDelayedRequestsWithCachedProjectionsAndNoProbe()
 {
  using var rig = await Rig.Create();
  await TestSupport.Complete(rig.Poll(CancellationToken.None));
  Assert.That(rig.Handler.Stages, Is.EqualTo(new[]{"telemetry","configuration"}));
  Assert.That(rig.Handler.MaximumActive, Is.EqualTo(1));
  Assert.That(rig.Driver.OnlineIndicatorIsOnline, Is.True);
  Assert.That(rig.Driver.ReadyIndicatorIsReady, Is.True);
  Assert.That(rig.Driver.OperationModeDisplay, Does.Contain("Self"));
  // Published GetReserveAsync applies the Tesla-app 5% reserve scale; driver rounds.
  Assert.That(rig.Driver.BackupReservePercent, Is.EqualTo(16));
 }
 [TestCase("telemetry")]
 [TestCase("configuration")]
 public async Task CancelledRealSignedStageReleasesTransportAndDriverGatesThenFreshPollRecovers(string stage)
 {
  using var rig = await Rig.Create();
  rig.Handler.BlockStage = stage;
  using var cancel = new CancellationTokenSource();
  var poll = rig.Poll(cancel.Token);
  await TestSupport.Complete(rig.Handler.Entered.Task);
  cancel.Cancel();
  await Assert.CatchAsync<OperationCanceledException>(async () => await TestSupport.Complete(poll));
  Assert.That(rig.Driver.ReadyIndicatorIsReady,Is.False,"Lifetime cancellation must prevent complete publication.");
  rig.Handler.BlockStage = null;
  await TestSupport.Complete(rig.Poll(CancellationToken.None));
  Assert.That(rig.Driver.OnlineIndicatorIsOnline, Is.True);
  Assert.That(rig.Driver.ReadyIndicatorIsReady, Is.True);
  Assert.That(rig.Handler.MaximumActive, Is.EqualTo(1));
  Assert.That((int)typeof(TeslaPowerwallDriver).GetField("_refreshInProgress", Private).GetValue(rig.Driver), Is.Zero);
  Assert.That(((SemaphoreSlim)typeof(TeslaPowerwallDriver).GetField("_connectionGate", Private).GetValue(rig.Driver)).CurrentCount, Is.EqualTo(1));
 }
 [Test]
 public async Task CancelledQueuedSignedReadDoesNotCancelOwnedRequestOrLeakGate()
 {
  using var rig = await Rig.Create();
  rig.Handler.BlockStage = "telemetry";
  using var ownerCancel = new CancellationTokenSource();
  var poll = rig.Poll(ownerCancel.Token);
  await TestSupport.Complete(rig.Handler.Entered.Task);
  using var queuedCancel = new CancellationTokenSource();
  var queued = rig.Transport.GetTelemetryAsync(force:true, cancellationToken:queuedCancel.Token);
  queuedCancel.Cancel();
  await Assert.CatchAsync<OperationCanceledException>(async () => await TestSupport.Complete(queued));
  Assert.That(poll.IsCompleted, Is.False);
  ownerCancel.Cancel();
  await Assert.CatchAsync<OperationCanceledException>(async () => await TestSupport.Complete(poll));
  rig.Handler.BlockStage = null;
  await TestSupport.Complete(rig.Poll(CancellationToken.None));
  Assert.That(rig.Driver.ReadyIndicatorIsReady, Is.True);
  Assert.That(rig.Handler.MaximumActive, Is.EqualTo(1));
 }
 [TestCase("telemetry")]
 [TestCase("configuration")]
 public async Task RequestTimeoutAtRealSignedStagePropagatesReleasesGatesAndNextPollRecovers(string stage)
 {
  using var rig=await Rig.Create();
  rig.Handler.TimeoutStage=stage;
  // RunPollAsync releases gates and propagates non-lifetime request failures;
  // RefreshLoopAsync owns the offline-publication boundary.
  await Assert.CatchAsync<OperationCanceledException>(async()=>await TestSupport.Complete(rig.Poll(CancellationToken.None)));
  Assert.That((int)typeof(TeslaPowerwallDriver).GetField("_refreshInProgress",Private).GetValue(rig.Driver),Is.Zero);
  Assert.That(((SemaphoreSlim)typeof(TeslaPowerwallDriver).GetField("_connectionGate",Private).GetValue(rig.Driver)).CurrentCount,Is.EqualTo(1));
  rig.Handler.TimeoutStage=null;
  await TestSupport.Complete(rig.Poll(CancellationToken.None));
  Assert.That(rig.Driver.OnlineIndicatorIsOnline,Is.True);
  Assert.That(rig.Driver.ReadyIndicatorIsReady,Is.True);
  Assert.That(rig.Handler.MaximumActive,Is.EqualTo(1));
 }
 [TestCase("telemetry")]
 [TestCase("configuration")]
 public async Task ActualFifteenSecondHttpTimeoutPublishesOfflineReleasesGatesAndSameLifetimeRecovers(string stage)
 {
  using var rig=await Rig.Create();using var lifetime=new CancellationTokenSource();
  rig.Handler.BlockStage=stage;
  var clock=System.Diagnostics.Stopwatch.StartNew();
  var loop=(Task)typeof(TeslaPowerwallDriver).GetMethod("RefreshLoopAsync",Private).Invoke(rig.Driver,new object[]{lifetime.Token});
  try
  {
   await TestSupport.Complete(rig.Handler.Entered.Task);
   await Until(()=>rig.Driver.StatusSummary.StartsWith("Unable to connect:",StringComparison.Ordinal),20);
   Assert.That(clock.Elapsed.TotalSeconds,Is.InRange(14,20));
   Assert.That(rig.Driver.TileDisplay,Is.EqualTo("Offline"));
   Assert.That(rig.Driver.OnlineIndicatorIsOnline,Is.False);
   Assert.That(rig.Driver.ReadyIndicatorIsReady,Is.False);
   Assert.That(lifetime.IsCancellationRequested,Is.False);
   Assert.That(loop.IsCompleted,Is.False);
   Assert.That(((SemaphoreSlim)typeof(TeslaPowerwallDriver).GetField("_connectionGate",Private).GetValue(rig.Driver)).CurrentCount,Is.EqualTo(1));
   Assert.That((int)typeof(TeslaPowerwallDriver).GetField("_refreshInProgress",Private).GetValue(rig.Driver),Is.Zero);
   rig.Handler.BlockStage=null;
   ((SemaphoreSlim)typeof(TeslaPowerwallDriver).GetField("_refreshWake",Private).GetValue(rig.Driver)).Release();
   await Until(()=>rig.Driver.ReadyIndicatorIsReady);
   Assert.That(rig.Driver.OnlineIndicatorIsOnline,Is.True);
   Assert.That(rig.Handler.MaximumActive,Is.EqualTo(1));
  }finally{lifetime.Cancel();await TestSupport.Complete(loop);}
 }
 [TestCase("telemetry")]
 [TestCase("configuration")]
 public async Task RealSignedRequestTimeoutPublishesOfflineThroughRefreshLoopAndSameLifetimeRecovers(string stage)
 {
  using var rig=await Rig.Create();using var lifetime=new CancellationTokenSource();
  rig.Handler.TimeoutStage=stage;
  var loop=(Task)typeof(TeslaPowerwallDriver).GetMethod("RefreshLoopAsync",Private).Invoke(rig.Driver,new object[]{lifetime.Token});
  try
  {
   await Until(()=>rig.Driver.StatusSummary.StartsWith("Unable to connect:",StringComparison.Ordinal));
   Assert.That(rig.Driver.TileDisplay,Is.EqualTo("Offline"));
   Assert.That(rig.Driver.OnlineIndicatorIsOnline,Is.False);
   Assert.That(rig.Driver.ReadyIndicatorIsReady,Is.False);
   Assert.That(lifetime.IsCancellationRequested,Is.False);Assert.That(loop.IsCompleted,Is.False);
   rig.Handler.TimeoutStage=null;
   ((SemaphoreSlim)typeof(TeslaPowerwallDriver).GetField("_refreshWake",Private).GetValue(rig.Driver)).Release();
   await Until(()=>rig.Driver.ReadyIndicatorIsReady);
   Assert.That(rig.Driver.OnlineIndicatorIsOnline,Is.True);
   Assert.That(rig.Handler.MaximumActive,Is.EqualTo(1));
  }finally{lifetime.Cancel();await TestSupport.Complete(loop);}
 }
 [Test]
 public async Task IndividuallyValidSignedReadsMayExceedFifteenSecondsTogetherWithoutOverlapOrPartialPublication()
 {
  using var rig=await Rig.Create();
  rig.Handler.StageDelay=TimeSpan.FromSeconds(8);
  var clock=System.Diagnostics.Stopwatch.StartNew();
  var poll=rig.Poll(CancellationToken.None);
  await Task.Delay(TimeSpan.FromSeconds(9));
  Assert.That(poll.IsCompleted,Is.False,"Second valid request must still be owned.");
  Assert.That(rig.Driver.ReadyIndicatorIsReady,Is.False,"No partial data publication.");
  await TestSupport.Complete(rig.Poll(CancellationToken.None));
  Assert.That(rig.Handler.Stages,Is.EqualTo(new[]{"telemetry","configuration"}),"Concurrent refresh must not issue extra requests.");
  Assert.That(await Task.WhenAny(poll,Task.Delay(TimeSpan.FromSeconds(12))),Is.SameAs(poll));
  await poll;
  Assert.That(clock.Elapsed.TotalSeconds,Is.GreaterThanOrEqualTo(16));
  Assert.That(rig.Driver.OnlineIndicatorIsOnline,Is.True);
  Assert.That(rig.Driver.ReadyIndicatorIsReady,Is.True);
  Assert.That(rig.Handler.MaximumActive,Is.EqualTo(1));
  Assert.That(rig.Handler.Stages,Is.EqualTo(new[]{"telemetry","configuration"}));
 }
 private static async Task Until(Func<bool> observed,int timeoutSeconds=5)
 {
  var clock=System.Diagnostics.Stopwatch.StartNew();
  while(!observed()&&clock.Elapsed.TotalSeconds<timeoutSeconds)await Task.Delay(10);
  Assert.That(observed(),Is.True,"Synthetic bounded state observation did not complete.");
 }
 private sealed class Rig : IDisposable
 {
  internal RSA Key; internal DriverLogger Logger; internal TeslaPowerwallDriver Driver;
  internal SignedHandler Handler; internal PowerwallTedapiClient Transport;
  internal Task Poll(CancellationToken token) => (Task)typeof(TeslaPowerwallDriver).GetMethod("RunPollAsync", Private).Invoke(Driver,new object[]{token});
  internal static async Task<Rig> Create(int httpTimeoutSeconds=15)
  {
   var rig = new Rig();
   try
   {
    rig.Key = new RSACryptoServiceProvider(4096){PersistKeyInCsp=false};
    var options = new PowerwallOptions {Host="192.0.2.1", Password="synthetic",
     LocalProtocol=PowerwallLocalProtocol.TedapiSigned, LocalSigningKey=rig.Key,
     CacheExpireSeconds=15, Timeout=TimeSpan.FromSeconds(httpTimeoutSeconds), NoLocalSessionPersistence=true,AllowLocalControl=false};
    rig.Handler = new SignedHandler();
    rig.Transport = (PowerwallTedapiClient)Activator.CreateInstance(typeof(PowerwallTedapiClient),Private,null,new object[]{options,rig.Handler,null},null);
    await TestSupport.Complete(rig.Transport.AuthenticateAsync());
    rig.Handler.Stages.Clear();
    var client = new Powerwall(options);
    typeof(Powerwall).GetField("_client",Private).SetValue(client,rig.Transport);
    rig.Logger = new DriverLogger("signed-core-diagnostic");
    rig.Driver = new TeslaPowerwallDriver(new DriverControllerCreationArgs("signed-core-diagnostic",TestSupport.DataDirectory,rig.Logger.AppLogger,null),TestSupport.Resources(rig.Logger));
    var errors = new Dictionary<string,string>();
    var local = new LocalConnectionSettings().Merge(new Dictionary<string,DriverEntityValue?>{
      ["ConnectionMode"]=new DriverEntityValue("Signed LAN"),["LocalHost"]=new DriverEntityValue("192.0.2.1"),
      ["LocalPassword"]=new DriverEntityValue("synthetic"),["LocalSigningKey"]=new DriverEntityValue(rig.Key.ToXmlString(true))},errors);
    Assert.That(errors,Is.Empty);
    typeof(TeslaPowerwallDriver).GetField("_local",Private).SetValue(rig.Driver,local);
    typeof(TeslaPowerwallDriver).GetField("_client",Private).SetValue(rig.Driver,client);
    return rig;
   } catch {rig.Dispose();throw;}
  }
  public void Dispose(){Driver?.Dispose();if(Driver==null)Transport?.Dispose();Logger?.Dispose();Key?.Dispose();}
 }
 private sealed class SignedHandler : HttpMessageHandler
 {
  internal TimeSpan StageDelay=TimeSpan.FromMilliseconds(25);
  internal string BlockStage;
  internal string TimeoutStage;
  internal readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
  internal readonly List<string> Stages = new();
  private int active; internal int MaximumActive;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   int current=Interlocked.Increment(ref active);MaximumActive=Math.Max(MaximumActive,current);
   try
   {
    Assert.That(request.RequestUri.Host,Is.EqualTo("192.0.2.1"));
    if(request.RequestUri.AbsolutePath=="/api/login/Basic")return Text("""{"token":"synthetic"}""");
    if(request.RequestUri.AbsolutePath=="/tedapi/din")return Text("1707000-00-A--TEST0000000000");
    Assert.That(request.Method,Is.EqualTo(HttpMethod.Post));Assert.That(request.RequestUri.AbsolutePath,Is.EqualTo("/tedapi/v1r"));
    byte[] bytes=await request.Content.ReadAsByteArrayAsync();
    var envelope=Value<ByteString>(Parse("Signed.RoutableMessage",bytes),"ProtobufMessageAsBytes");
    var legacy=Parse("Legacy.MessageEnvelope",envelope.ToByteArray());
    bool configuration=Value<object>(legacy,"Config")!=null;
    string stage=configuration?"configuration":"telemetry";Stages.Add(stage);
    if(stage==TimeoutStage)throw new TaskCanceledException("Synthetic HTTP request timeout; caller lifetime is not cancelled.");
    if(stage==BlockStage){Entered.TrySetResult(true);await Task.Delay(Timeout.Infinite,token);}
    await Task.Delay(StageDelay,token);
    ByteString reply;
    if(configuration)
    {
     var file=Message("Signed.FileStoreAPIFile",("Blob",ByteString.CopyFromUtf8("""{"default_real_mode":"self_consumption","site_info":{"backup_reserve_percent":20}}""")));
     var read=Message("Signed.FileStoreAPIReadFileResponse",("File",file));
     var store=Message("Signed.FileStoreMessages",("ReadFileResponse",read));
     reply=Message("Signed.MessageEnvelope",("Filestore",store)).ToByteString();
    }
    else
    {
     var query=Parse("Graphql.Envelope",envelope.ToByteArray());
     Assert.That(Value<ByteString>(Value<IMessage>(Value<IMessage>(query,"Graphql"),"QueryRequest"),"Query").ToStringUtf8(),Does.Contain("DeviceControllerQuery"));
     reply=Message("Graphql.Envelope",("Graphql",Message("Graphql.GraphQLMessages",("QueryResponse",Message("Graphql.QueryResponse",("Status",1),("Data","""{"control":{"systemStatus":{"nominalFullPackEnergyWh":13500,"nominalEnergyRemainingWh":7000},"islanding":{"contactorClosed":true},"meterAggregates":[{"location":"SITE","realPowerW":100},{"location":"SOLAR","realPowerW":200},{"location":"BATTERY","realPowerW":0},{"location":"LOAD","realPowerW":300}]}}""")))))).ToByteString();
    }
    return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Message("Signed.RoutableMessage",("ProtobufMessageAsBytes",reply)).ToByteArray())};
   }finally{Interlocked.Decrement(ref active);}
  }
  // Published protocol models are internal; reflection retains the published assembly unchanged.
  private static Type ProtocolType(string name)=>typeof(PowerwallTedapiClient).Assembly.GetType("TeslaPowerwallLibrary.Tedapi.Protocol."+name,true);
  private static IMessage Parse(string name,byte[] bytes)=>((MessageParser)ProtocolType(name).GetProperty("Parser").GetValue(null)).ParseFrom(bytes);
  private static T Value<T>(IMessage message,string name)=>(T)message.GetType().GetProperty(name).GetValue(message);
  private static IMessage Message(string name,params (string Name,object Value)[] values)
  {
   var message=(IMessage)Activator.CreateInstance(ProtocolType(name),true);
   foreach(var value in values)message.GetType().GetProperty(value.Name).SetValue(message,value.Value);
   return message;
  }
  private static HttpResponseMessage Text(string value)=>new(HttpStatusCode.OK){Content=new StringContent(value)};
 }
}
