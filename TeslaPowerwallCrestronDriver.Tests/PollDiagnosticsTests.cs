// Copyright (c) 2026 Neil Colvin. MIT with Commons Clause; see LICENSE.
using System;
using System.Threading;
using NUnit.Framework;
using TeslaPowerwall.CrestronDriver;
namespace TeslaPowerwallCrestronDriver.Tests;
[TestFixture,Category("Offline")]
public sealed class PollDiagnosticsTests
{
 [TestCase(false,"request-or-operation")]
 [TestCase(true,"lifetime")]
 public void CancellationOriginIsExplicitAndExceptionMessageIsNotRecorded(bool lifetimeCancel,string expected)
 {
  using var lifetime=new CancellationTokenSource();
  if(lifetimeCancel)lifetime.Cancel();
  string recorded=null;var diagnostics=new LocalPollDiagnostics(lifetime.Token,value=>recorded=value);
  diagnostics.Begin(LocalPollStage.ReserveConfiguration);diagnostics.Report(new OperationCanceledException("secret-token-synthetic"));
  Assert.That(recorded,Does.Contain("stage=ReserveConfiguration"));
  Assert.That(recorded,Does.Contain("cancellation="+expected));
  Assert.That(recorded,Does.Contain("exceptionType=System.OperationCanceledException"));
  Assert.That(recorded,Does.Not.Contain("secret-token"));
  Assert.That(recorded,Does.Match(@"coreMs=\d+\.\d; stageMs=\d+\.\d"));
 }
 [Test]
 public void DiagnosticSinkFailureCannotReplaceOriginalPollFailure()
 {
  var diagnostics=new LocalPollDiagnostics(CancellationToken.None,_=>throw new InvalidOperationException("synthetic logging failure"));
  Assert.DoesNotThrow(()=>diagnostics.Report(new TimeoutException("original request failure")));
 }
}