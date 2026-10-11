// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TeslaPowerwallLibrary;
using TeslaPowerwallLibrary.Models;
namespace TeslaPowerwall.CrestronDriver;
public sealed partial class TeslaPowerwallDriver
{
 // Use the existing local request timeout as one whole core-poll budget instead
 // of allowing every sequential data read to consume another fifteen seconds.
 internal static readonly TimeSpan LocalCorePollTimeout = TimeSpan.FromSeconds(15);
 private int _localPollFailures;

 // Retry only the normal data poll. First failure retains its configured delay;
 // repeated local failures ramp to five minutes, never shortening a longer delay.
 private int NextPollDelaySeconds
 {
  get
  {
   lock (_stateLock)
   {
    int baseline = PollIntervalSeconds;
    if (!IsLocal || _localPollFailures < 2) return baseline;
    int ceiling = Math.Max(baseline, 300);
    int delay = baseline;
    for (int count = 1; count < _localPollFailures && delay < ceiling; count++)
     delay = Math.Min(ceiling, delay * 2);
    return delay;
   }
  }
 }
 private void RecordLocalPollFailure(CancellationToken token)
 {
  lock (_stateLock)
  {
   token.ThrowIfCancellationRequested();
   if (IsLocal) _localPollFailures = Math.Min(16, _localPollFailures + 1);
  }
 }


 // This is the poll's required battery read, not a separate availability request.
 // A forced read also populates the signed client's telemetry cache used by
 // subsequent power/grid projections; keep one owned HTTP client and no TCP loop.
 internal static async Task<double> ReadFreshLocalLevelAsync(Powerwall client, CancellationToken token)
 {
  string payload = await client.PollAsync("/api/system_status/soe", force: true, cancellationToken: token).ConfigureAwait(false);
  token.ThrowIfCancellationRequested();
  try
  {
   using var document = JsonDocument.Parse(payload ?? "null");
   if(document.RootElement.ValueKind != JsonValueKind.Object ||
      !document.RootElement.TryGetProperty("percentage", out var percentage) ||
      percentage.ValueKind != JsonValueKind.Number || !percentage.TryGetDouble(out var value) ||
      double.IsNaN(value) || double.IsInfinity(value))
    throw new PowerwallException("The local Powerwall did not provide a fresh battery reading.");
   // Match Powerwall.LevelAsync(scale:true) exactly, including real zero values.
   return (value / 0.95) - (5 / 0.95);
  }
  catch(JsonException ex)
  {
   throw new PowerwallException("The local Powerwall returned an invalid battery reading: " + ex.Message);
  }
 }
}
