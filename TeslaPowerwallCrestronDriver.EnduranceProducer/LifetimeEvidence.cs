// Copyright (c) 2026 Neil Colvin. Licensed under the MIT License with Commons Clause.
// Read-only mechanism adapted from the public DevTools endurance sample.
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
sealed record ManagedRuntimeIdentity(int ParentPid, int[] ChildPids)
{
    public static ManagedRuntimeIdentity Parse(string text)
    {
        var rows = new List<(int Pid, int Parent, string Name)>();
        foreach (var line in text.Split('\n'))
        {
            var parts = Regex.Split(line.Trim(), @"\s+");
            if (parts.Length < 13 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var pid) || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var parent)) continue;
            rows.Add((pid, parent, parts[12]));
        }
        var parents = rows.Where(row => Regex.IsMatch(row.Name, @"^SSP\[\d+\]$", RegexOptions.CultureInvariant)).ToArray();
        if (parents.Length != 1 || parents[0].Pid <= 0) throw new InvalidDataException("TASKSTAT has no unambiguous SSP identity.");
        var children = rows.Where(row => row.Parent == parents[0].Pid && row.Name == "mono-sgen").Select(row => row.Pid).Order().ToArray();
        if (children.Length == 0 || children.Any(pid => pid <= 0) || children.Distinct().Count() != children.Length) throw new InvalidDataException("TASKSTAT has no unambiguous managed child identity.");
        return new(parents[0].Pid, children);
    }
    public bool Matches(ManagedRuntimeIdentity other) => ParentPid == other.ParentPid && ChildPids.SequenceEqual(other.ChildPids);
}
static class LifetimeEvidence
{
    public const string Limitation = "TASKSTAT samples shared driver runtime PIDs; individual driver reloads within an unchanged runtime and PID reuse between samples are not detectable.";
    private static readonly Regex Prompt = new(@"(?:^|[\r\n])[A-Za-z0-9][A-Za-z0-9_.:-]{0,80}>", RegexOptions.CultureInvariant);
    public static async Task<ManagedRuntimeIdentity> ReadAsync(string host, NetworkCredential credential, string fingerprint, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var client = new SshClient(host, credential.UserName, credential.Password); client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
        client.HostKeyReceived += (_, key) => key.CanTrust = string.Equals(key.FingerPrintSHA256, fingerprint, StringComparison.Ordinal);
        await client.ConnectAsync(deadline.Token);
        using var shell = client.CreateShellStream("xterm", 100, 24, 800, 600, 65536);
        var text = new StringBuilder(); bool sent = false;
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!client.IsConnected) throw new IOException("Processor disconnected before TASKSTAT was read.");
            if (shell.DataAvailable) text.Append(shell.Read());
            if (text.Length > 65536) throw new InvalidDataException("TASKSTAT exceeded its response limit.");
            if (!sent && Prompt.IsMatch(text.ToString())) { text.Clear(); sent = true; shell.WriteLine("TASKSTAT"); }
            else if (sent && Prompt.IsMatch(text.ToString())) return ManagedRuntimeIdentity.Parse(text.ToString());
            await Task.Delay(50, deadline.Token);
        }
    }
}