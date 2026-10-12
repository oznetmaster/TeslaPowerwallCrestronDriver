// Copyright (c) 2026 Neil Colvin. MIT with Commons Clause; see LICENSE.
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
namespace TeslaPowerwall.CrestronDriver;

internal enum LocalPollStage { CoreEntry, BatteryTelemetry, PowerProjection, GridProjection, ReserveConfiguration, ModeProjection, GridChargingProjection, GridExportProjection, Publication }

// Per-poll diagnostics only: no shared state, timer, request, retry or value logging.
internal sealed class LocalPollDiagnostics
{
 private readonly Stopwatch _core=Stopwatch.StartNew();
 private readonly Stopwatch _stage=Stopwatch.StartNew();
 private readonly CancellationToken _lifetime;
 private readonly Action<string> _write;
 private LocalPollStage _current=LocalPollStage.CoreEntry;
 internal LocalPollDiagnostics(CancellationToken lifetime,Action<string> write)
 { _lifetime=lifetime;_write=write; }
 internal void Begin(LocalPollStage stage) { _current=stage;_stage.Restart(); }
 internal string Describe(Exception error)
 {
  string origin=_lifetime.IsCancellationRequested?"lifetime":error is OperationCanceledException?"request-or-operation":"none";
  return string.Format(CultureInfo.InvariantCulture,"Local poll diagnostic: stage={0}; coreMs={1:F1}; stageMs={2:F1}; exceptionType={3}; cancellation={4}",_current,_core.Elapsed.TotalMilliseconds,_stage.Elapsed.TotalMilliseconds,error.GetType().FullName,origin);
 }
 internal void Report(Exception error)
 {
  // A failed diagnostic sink must never replace the original state/exception outcome.
  try { _write?.Invoke(Describe(error)); } catch { }
 }
}