// Copyright (c) 2026 Neil Colvin. Licensed under MIT with Commons Clause.
using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using TeslaPowerwall.CrestronDriver;
using TeslaPowerwallLibrary;
using TeslaPowerwallLibrary.Local;
namespace TeslaPowerwallCrestronDriver.Tests;
[TestFixture, Category("Offline")]
public sealed class LocalAvailabilityTests
{
 [Test]
 public async Task ExistingGatewayTransportReusesRequiredFreshBatteryResponseWithoutAnotherRequest()
 {
  var options=new PowerwallOptions {Host="192.0.2.1",Password="synthetic",LocalProtocol=PowerwallLocalProtocol.Gateway,
   CacheExpireSeconds=60,NoLocalSessionPersistence=true};
  var handler=new BatteryHandler();
  var type=typeof(Powerwall).Assembly.GetType("TeslaPowerwallLibrary.Local.PowerwallLocalClient");
  using var transport=(PowerwallLocalClient)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{options,handler},null);
  var flags=BindingFlags.Instance|BindingFlags.NonPublic;
  type.GetField("_httpClient",flags).SetValue(transport,new HttpClient(handler));
  type.GetField("_hasAuth",flags).SetValue(transport,true);
  using var client=new Powerwall(options);
  typeof(Powerwall).GetField("_client",flags).SetValue(client,transport);
  double fresh=await TeslaPowerwallDriver.ReadFreshLocalLevelAsync(client,CancellationToken.None);
  double? reused=await client.LevelAsync(scale:true);
  Assert.That(fresh,Is.EqualTo(reused));
  Assert.That(handler.Requests,Is.EqualTo(1),"The actual HTTP transport/cache must not duplicate the required battery read.");
 }
 [Test, Category("Processor")]
 public async Task FullAuthenticatedGatewayCorePollUsesFourSerializedRequestsAndCachedOperationProjection()
 {
#if NETFRAMEWORK
  if (Type.GetType("Mono.Runtime") == null) Assert.Ignore("Requires the SDK desktop harness or processor runtime.");
#endif
  var options=new PowerwallOptions{Host="192.0.2.1",Password="synthetic",LocalProtocol=PowerwallLocalProtocol.Gateway,CacheExpireSeconds=60,NoLocalSessionPersistence=true};
  var handler=new CoreHandler();var flags=BindingFlags.Instance|BindingFlags.NonPublic;
  var type=typeof(Powerwall).Assembly.GetType("TeslaPowerwallLibrary.Local.PowerwallLocalClient");
  using var transport=(PowerwallLocalClient)Activator.CreateInstance(type,flags,null,new object[]{options,handler},null);
  type.GetField("_httpClient",flags).SetValue(transport,new HttpClient(handler));type.GetField("_hasAuth",flags).SetValue(transport,true);
  using var client=new Powerwall(options);typeof(Powerwall).GetField("_client",flags).SetValue(client,transport);
  using var logger=new Crestron.DeviceDrivers.SDK.DriverLogger("request-count-test");
  using var driver=new TeslaPowerwallDriver(new DriverControllerCreationArgs("request-count-test",TestSupport.DataDirectory,logger.AppLogger,null),TestSupport.Resources(logger));
  typeof(TeslaPowerwallDriver).GetField("_client",flags).SetValue(driver,client);
  var settings=new LocalConnectionSettings().Merge(new System.Collections.Generic.Dictionary<string,Crestron.DeviceDrivers.EntityModel.Data.DriverEntityValue?>
   {["ConnectionMode"]=new Crestron.DeviceDrivers.EntityModel.Data.DriverEntityValue("Gateway")},new System.Collections.Generic.Dictionary<string,string>());
  typeof(TeslaPowerwallDriver).GetField("_local",flags).SetValue(driver,settings);
  await TestSupport.Complete((Task)typeof(TeslaPowerwallDriver).GetMethod("PollAsync",flags).Invoke(driver,new object[]{client,CancellationToken.None}));
  Assert.That(handler.Paths,Is.EqualTo(new[]{"/api/system_status/soe","/api/meters/aggregates","/api/system_status/grid_status","/api/operation"}));
  Assert.That(driver.OnlineIndicatorIsOnline,Is.True);Assert.That(driver.ReadyIndicatorIsReady,Is.True);
 }
 [Test]
 public void TransportTimingSignerSupportsFrameworkByteRangeAndStreamHashing()
 {
  using var key=new SignedLanCoreDiagnosticTests.TransportTestRsa(new IdentityOnlyRsa());
  byte[] data={1,2,3};
  Assert.That(key.SignData(data,System.Security.Cryptography.HashAlgorithmName.SHA512,System.Security.Cryptography.RSASignaturePadding.Pkcs1),Has.Length.EqualTo(512));
  Assert.That(key.SignData(data,1,2,System.Security.Cryptography.HashAlgorithmName.SHA512,System.Security.Cryptography.RSASignaturePadding.Pkcs1),Has.Length.EqualTo(512));
  using var stream=new System.IO.MemoryStream(data);
  Assert.That(key.SignData(stream,System.Security.Cryptography.HashAlgorithmName.SHA512,System.Security.Cryptography.RSASignaturePadding.Pkcs1),Has.Length.EqualTo(512));
 }
 private sealed class IdentityOnlyRsa:System.Security.Cryptography.RSA
 {
  public override int KeySize {get=>4096;set=>throw new NotSupportedException();}
  public override System.Security.Cryptography.RSAParameters ExportParameters(bool includePrivateParameters)=>new(){Modulus=new byte[512],Exponent=new byte[]{1,0,1}};
  public override void ImportParameters(System.Security.Cryptography.RSAParameters parameters)=>throw new NotSupportedException();
 }
 private sealed class CoreHandler:HttpMessageHandler
 {
  internal readonly System.Collections.Generic.List<string> Paths=new();
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   Assert.That(request.Method,Is.EqualTo(HttpMethod.Get));Assert.That(request.RequestUri.Host,Is.EqualTo("192.0.2.1"));
   var path=request.RequestUri.AbsolutePath;Paths.Add(path);
   var body=path switch {
    "/api/system_status/soe"=>"{\"percentage\":75}",
    "/api/meters/aggregates"=>"{\"site\":{\"instant_power\":100},\"solar\":{\"instant_power\":200},\"battery\":{\"instant_power\":0},\"load\":{\"instant_power\":300}}",
    "/api/system_status/grid_status"=>"{\"grid_status\":\"SystemGridConnected\"}",
    "/api/operation"=>"{\"backup_reserve_percent\":20,\"real_mode\":\"self_consumption\"}",
    _=>throw new AssertionException("Unexpected request "+path)};
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)});
  }
 }
 private sealed class BatteryHandler:HttpMessageHandler
 {
  internal int Requests;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   Assert.That(request.Method,Is.EqualTo(HttpMethod.Get));
   Assert.That(request.RequestUri.Host,Is.EqualTo("192.0.2.1"));
   Assert.That(request.RequestUri.AbsolutePath,Is.EqualTo("/api/system_status/soe"));
   Requests++;
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"percentage\":75}")});
  }
 }
}
