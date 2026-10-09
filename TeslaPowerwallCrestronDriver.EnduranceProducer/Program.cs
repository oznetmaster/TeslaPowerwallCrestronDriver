// Copyright (c) 2026 Neil Colvin. Licensed under the MIT License.
// Read-only Tesla submission endurance probe. It never sends Tesla controls.

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrestronHomeDevTools;

var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true, AllowDuplicateProperties = false, Converters = { new JsonStringEnumConverter() } };
if (args.Length == 4 && args[0] == "--diagnose-local")
{
    try { using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var result = await IndependentObservation.ReadAsync(args[1], args[2], args[3], timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, json)); }
    catch (Exception error) when (error is not OutOfMemoryException) { Console.Error.WriteLine($"Independent diagnostic failed ({error.GetType().Name}); no evidence accepted."); Environment.ExitCode = 2; }
    return;
}
if (args.Length == 2 && args[0] == "--prepare-lifetime-baseline")
{
    try { await PrepareLifetimeAsync(args[1]); }
    catch (Exception error) when (error is not OutOfMemoryException) { Console.Error.WriteLine($"Lifetime preparation stopped ({error.GetType().Name}); no sample accepted."); Environment.ExitCode = 2; }
    return;
}
if (args.SequenceEqual(["--self-test"])) { Checks.Run(); Console.WriteLine("self-test-passed"); return; }
try { await RunAsync(); }
catch (Exception error) when (error is not OutOfMemoryException) { Console.Error.WriteLine($"Producer stopped ({error.GetType().Name}); no observation accepted."); Environment.ExitCode = 2; }

async Task RunAsync()
{
    var request = JsonSerializer.Deserialize<SubmissionEnduranceProbeRequest>(await Console.In.ReadToEndAsync(), json)
        ?? throw new InvalidDataException("Probe request is missing.");
    if (request.SchemaVersion != 1 || string.IsNullOrWhiteSpace(request.SettingsFile) || !Path.IsPathFullyQualified(request.SettingsFile))
        throw new InvalidDataException("Probe settings must be an absolute private file.");
    var evidence = new Dictionary<string, object?> { ["schemaVersion"] = 1, ["startedUtc"] = DateTimeOffset.UtcNow };
    var outcome = SubmissionEvidenceOutcome.Failed; var boot = "unverified";
    try
    {
        SubmissionEndurance.ValidatePlan(request.Plan);
        var settings = await ReadSettingsAsync(request.SettingsFile);
        if (request.Plan.Identity != settings.Identity || request.Plan.InstallationIdentity != settings.InstallationIdentity ||
            request.Plan.Requirement.Id != settings.RequirementId || request.Plan.ReservationId != settings.ReservationId || request.Plan.ProcessorIdentity != $"processor:{settings.ProcessorHost}")
            throw new InvalidDataException("Candidate, processor, or endurance policy differs from pinned settings.");
        using var deadline = new CancellationTokenSource(request.Plan.ProbeTimeout);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Saved processor credentials require Windows.");
        var credential = DevToolsCredentialBindings.Read(settings.CredentialBindings).Resolve(DevToolsCredentialPurpose.Processor, settings.ProcessorHost);
        if (string.IsNullOrWhiteSpace(credential.CertificateSha256) || string.IsNullOrWhiteSpace(credential.SshFingerprint))
            throw new InvalidDataException("Verified processor trust pins are required.");
        var network = new NetworkCredential(credential.UserName, credential.Password);
        var uptime = await ProcessorUptime.ReadAsync(settings.ProcessorHost, network, credential.SshFingerprint, TimeSpan.FromSeconds(30), deadline.Token);
        var runtime = await LifetimeEvidence.ReadAsync(settings.ProcessorHost, network, credential.SshFingerprint, deadline.Token);
        boot = await Baseline.ReadAsync(settings.BaselineFile, settings, uptime, runtime, deadline.Token);
        evidence["bootIdentity"] = boot; evidence["managedRuntime"] = runtime; evidence["lifetimeLimitation"] = LifetimeEvidence.Limitation;
        await using var configuration = await ConfigurationClient.ConnectAsync(new ProcessorConnectionOptions { Host = settings.ProcessorHost, CertificateSha256 = credential.CertificateSha256 }, network, deadline.Token);
        var device = await configuration.GetDeviceAsync(settings.DeviceId, deadline.Token) ?? throw new InvalidDataException("Tesla device is missing.");
        VerifyDevice(device, settings, evidence);
        var driverReadUtc = DateTimeOffset.UtcNow;
        var processorLocalNow = uptime.LocalStartedAt.Add(uptime.Uptime).Add(driverReadUtc - uptime.ObservedUtc);
        var status = device.PropertyValues["statusSummary"].GetString() ?? "";
        var age = ReadingFreshness.AgeSeconds(status, processorLocalNow);
        evidence["driverReadUtc"] = driverReadUtc; evidence["driverAgeSeconds"] = age;
        if (!ReadingFreshness.IsCurrent(status, processorLocalNow, settings.MaximumReadingAgeSeconds)) throw new InvalidDataException("Published Tesla readings are stale or from a future clock.");
        var independent = await IndependentObservation.ReadAsync(settings.LiveInputFile, settings.LiveInputSha256, settings.LocalHost, deadline.Token);
        evidence["independentReadings"] = independent;
        IndependentObservation.Compare(device, independent, evidence);
        var payload = await DriverPayloadInspection.CompareAsync(settings.ProcessorHost, network, credential.SshFingerprint,
            settings.PackagePath, settings.Identity.PackageSha256, settings.CatalogueId, TimeSpan.FromMinutes(2), deadline.Token);
        evidence["payloadFileCount"] = payload.Files.Count; evidence["bootIdentity"] = boot; evidence["readOnly"] = true;
        outcome = SubmissionEvidenceOutcome.Passed;
    }
    catch (Exception error) when (error is not OutOfMemoryException) { evidence["reason"] = error is InvalidDataException ? error.Message : "probe-observation-failed"; }
    Console.Write(JsonSerializer.Serialize(new SubmissionEnduranceProbeResult(request.Plan.Identity, request.Plan.ProcessorIdentity,
        request.Plan.InstallationIdentity, request.Plan.ReservationId, request.Plan.ProducerId, boot, outcome,
        JsonSerializer.SerializeToUtf8Bytes(evidence, json)), json));
}


async Task PrepareLifetimeAsync(string settingsPath)
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Saved processor credentials require Windows.");
    var settings = await ReadSettingsAsync(settingsPath);
    if (File.Exists(settings.BaselineFile)) throw new InvalidDataException("Existing lifetime preparation must be inspected, never replaced.");
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
    var stored = DevToolsCredentialBindings.Read(settings.CredentialBindings).Resolve(DevToolsCredentialPurpose.Processor, settings.ProcessorHost);
    var network = new NetworkCredential(stored.UserName, stored.Password);
    var uptime = await ProcessorUptime.ReadAsync(settings.ProcessorHost, network, stored.SshFingerprint!, TimeSpan.FromSeconds(30), deadline.Token);
    var runtime = await LifetimeEvidence.ReadAsync(settings.ProcessorHost, network, stored.SshFingerprint!, deadline.Token);
    await using var client = await ConfigurationClient.ConnectAsync(new ProcessorConnectionOptions { Host = settings.ProcessorHost, CertificateSha256 = stored.CertificateSha256 }, network, deadline.Token);
    var device = await client.GetDeviceAsync(settings.DeviceId, deadline.Token) ?? throw new InvalidDataException("Tesla device is missing.");
    var evidence = new Dictionary<string,object?>(); VerifyDevice(device, settings, evidence);
    var processorNow = uptime.LocalStartedAt.Add(uptime.Uptime).Add(DateTimeOffset.UtcNow - uptime.ObservedUtc);
    if (!ReadingFreshness.IsCurrent(device.PropertyValues["statusSummary"].GetString() ?? "", processorNow, settings.MaximumReadingAgeSeconds)) throw new InvalidDataException("Driver readings are not current.");
    var independent = await IndependentObservation.ReadAsync(settings.LiveInputFile, settings.LiveInputSha256, settings.LocalHost, deadline.Token);
    IndependentObservation.Compare(device, independent, evidence);
    await DriverPayloadInspection.CompareAsync(settings.ProcessorHost, network, stored.SshFingerprint!, settings.PackagePath, settings.Identity.PackageSha256, settings.CatalogueId, TimeSpan.FromMinutes(2), deadline.Token);
    await Baseline.CreateAsync(settings.BaselineFile, settings, uptime, runtime, deadline.Token);
    using var input = File.OpenRead(settings.BaselineFile);
    Console.WriteLine(JsonSerializer.Serialize(new { baselineSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)), settings.ReservationId, readOnlyPreparation = true, acceptedAsEnduranceSample = false }, json));
}

async Task<Settings> ReadSettingsAsync(string path)
{
    var full = Path.GetFullPath(path); var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)) + Path.DirectorySeparatorChar;
    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full) || new FileInfo(full).Length > 65536)
        throw new InvalidDataException("Settings must be a pinned file inside the published producer.");
    var settings = JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(full), json) ?? throw new InvalidDataException("Producer settings are missing.");
    settings.Validate(); return settings;
}

static void VerifyDevice(DeviceInfo device, Settings settings, IDictionary<string, object?> evidence)
{
    if (device.Id != settings.DeviceId || device.LocationId != settings.LocationId || device.Name != settings.DriverName || device.Model != settings.DriverModel)
        throw new InvalidDataException("Tesla instance identity changed.");
    var p = device.PropertyValues;
    string Text(string key) => p.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    bool Flag(string key) => p.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.True;
    if (Text("cp.driverInformation:version") != settings.DriverVersion || Text("cp.driverConfiguration:driverLoadingStatus") != "Loaded" ||
        !Flag("cp.driverConfiguration:isConfigured") || !Flag("onlineIndicator:isOnline") || !Flag("readyIndicator:isReady"))
        throw new InvalidDataException("Tesla driver is not loaded, configured, online, and ready.");
    var readings = new Dictionary<string,string> { ["battery"] = Text("batteryLevelDisplay"), ["solar"] = Text("solarPowerDisplay"), ["grid"] = Text("gridPowerDisplay"), ["load"] = Text("loadPowerDisplay"), ["status"] = Text("statusSummary") };
    if (readings.Any(pair => pair.Value is "" or "--") || !readings["battery"].Contains('%') || !readings["status"].StartsWith("Updated ", StringComparison.Ordinal))
        throw new InvalidDataException("Tesla driver has no current read-only telemetry.");
    evidence["telemetry"] = readings; evidence["deviceId"] = device.Id; evidence["driverVersion"] = settings.DriverVersion;
}

sealed record Settings(string ProcessorHost, string CredentialBindings, string PackagePath, string CatalogueId, int DeviceId, int LocationId,
    string DriverName, string DriverModel, string DriverVersion, SubmissionEvidenceIdentity Identity, string InstallationIdentity, string RequirementId, string ReservationId, string BaselineFile, string LiveInputFile, string LiveInputSha256, string LocalHost, int MaximumReadingAgeSeconds)
{
    public void Validate()
    {
        if (DeviceId <= 0 || LocationId <= 0 || new[] { ProcessorHost, CredentialBindings, PackagePath, CatalogueId, DriverName, DriverModel, DriverVersion, InstallationIdentity, RequirementId, ReservationId, BaselineFile }.Any(string.IsNullOrWhiteSpace) ||
            !Path.IsPathFullyQualified(CredentialBindings) || !Path.IsPathFullyQualified(PackagePath) || !Path.IsPathFullyQualified(BaselineFile) || !Path.IsPathFullyQualified(LiveInputFile) || string.IsNullOrWhiteSpace(LocalHost) || LiveInputSha256.Length != 64 || !LiveInputSha256.All(Uri.IsHexDigit) || MaximumReadingAgeSeconds <= 0 || !Guid.TryParseExact(ReservationId, "N", out _)) throw new InvalidDataException("Complete reviewed Tesla producer settings.");
        var published = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(BaselineFile).StartsWith(published, StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(LiveInputFile).StartsWith(published, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Lifetime baseline must be inside the pinned publication; private Tesla input must stay outside.");
    }
}
static class Baseline
{
    sealed record Saved(string Settings, DateTimeOffset Earliest, DateTimeOffset Latest, ManagedRuntimeIdentity Runtime, string Identity);
    static string Binding(Settings settings) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(settings)));
    public static async Task CreateAsync(string path, Settings settings, ProcessorUptimeSnapshot uptime, ManagedRuntimeIdentity runtime, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var saved = new Saved(Binding(settings), uptime.EarliestStartUtc, uptime.LatestStartUtc, runtime, $"processor-boot:{uptime.EarliestStartUtc:O}:{uptime.LatestStartUtc:O};ssp:{runtime.ParentPid};children:{string.Join(',', runtime.ChildPids)}");
        await JsonSerializer.SerializeAsync(stream, saved, cancellationToken: token); stream.Flush(true);
    }
    public static async Task<string> ReadAsync(string path, Settings settings, ProcessorUptimeSnapshot uptime, ManagedRuntimeIdentity runtime, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > 65536) throw new InvalidDataException("Lifetime preparation is incomplete or invalid.");
        var saved = await JsonSerializer.DeserializeAsync<Saved>(stream, cancellationToken: token) ?? throw new InvalidDataException("Lifetime baseline is invalid.");
        if (saved.Settings != Binding(settings) || uptime.EarliestStartUtc > saved.Latest.AddSeconds(2) || uptime.LatestStartUtc < saved.Earliest.AddSeconds(-2) || saved.Runtime is null || !saved.Runtime.Matches(runtime)) throw new InvalidDataException("Processor boot or managed driver runtime identity changed.");
        return saved.Identity;
    }
    public static void CheckReadOnlyPreparation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tesla-baseline-selftest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "baseline.json");
        var settings = new Settings("test.invalid", path, path, "test", 1, 1, "test", "test", "1.0.0.0", new(new('a',64),new('b',40),new('c',64),new('d',64)), "test", "test", Guid.NewGuid().ToString("N"), path,path,new('e',64),"local.invalid",120);
        var now = DateTimeOffset.UtcNow; var uptime = new ProcessorUptimeSnapshot(TimeSpan.FromHours(1), DateTime.Now.AddHours(-1), now, now); var runtime = new ManagedRuntimeIdentity(101,[102]);
        bool Reject(Func<Task> operation) { try { operation().GetAwaiter().GetResult(); return false; } catch (IOException) { return true; } catch (InvalidDataException) { return true; } }
        try
        {
            if (!Reject(async () => { await ReadAsync(path,settings,uptime,runtime,CancellationToken.None); }) || File.Exists(path)) throw new InvalidDataException("Missing baseline was created by observation.");
            CreateAsync(path,settings,uptime,runtime,CancellationToken.None).GetAwaiter().GetResult();
            if (!Reject(() => CreateAsync(path,settings,uptime,runtime,CancellationToken.None))) throw new InvalidDataException("Baseline replacement was allowed.");
            var bytes = File.ReadAllBytes(path); File.SetAttributes(path,FileAttributes.ReadOnly);
            ReadAsync(path,settings,uptime,runtime,CancellationToken.None).GetAwaiter().GetResult();
            if (!bytes.SequenceEqual(File.ReadAllBytes(path)) || !Reject(async () => { await ReadAsync(path,settings with { ReservationId = Guid.NewGuid().ToString("N") },uptime,runtime,CancellationToken.None); }) || !Reject(async () => { await ReadAsync(path,settings,uptime,new ManagedRuntimeIdentity(101,[103]),CancellationToken.None); })) throw new InvalidDataException("Immutable baseline/run/runtime guards failed.");
        }
        finally { if(File.Exists(path)) { File.SetAttributes(path,FileAttributes.Normal);File.Delete(path); } Directory.Delete(directory); }
    }
}
static class Checks
{
    public static void Run()
    {
        Baseline.CheckReadOnlyPreparation();
        bool rejected = false;
        try { new Settings("", "x", "x", "x", 0, 0, "x", "x", "x", null!, "", "", "", "x", "x", "x", "x", 0).Validate(); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new InvalidDataException("Settings validation self-test failed.");
        string Row(int pid, int parent, string name) => $"{pid} {parent} 0 0 0 0 0 0 0 0 0 0 {name}";
        var baseline = ManagedRuntimeIdentity.Parse(string.Join('\n', Row(101, 1, "SSP[1]"), Row(102, 101, "mono-sgen")));
        var changed = ManagedRuntimeIdentity.Parse(string.Join('\n', Row(101, 1, "SSP[1]"), Row(103, 101, "mono-sgen")));
        if (!ReadingFreshness.IsCurrent("Updated 00:00", new DateTime(2026,10,9,0,0,30), 120) || ReadingFreshness.IsCurrent("Updated 23:50", new DateTime(2026,10,9,0,0,30), 120)) throw new InvalidDataException("Fresh/stale midnight checks failed.");
        var independent = new IndependentReadings(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 10, 0, 1000, 1000, 5, "self_consumption");
        DeviceInfo State(string mode, object reserve) => new() { PropertyValues = new() { ["operationMode"] = JsonSerializer.SerializeToElement(mode), ["backupReservePercent"] = JsonSerializer.SerializeToElement(reserve) } };
        IndependentObservation.Compare(State("self_consumption", 5), independent, new Dictionary<string, object?>());
        foreach (var mismatch in new[] { State("backup", 5), State("self_consumption", 6), State("self_consumption", "5"), new DeviceInfo() })
        {
            var retained = new Dictionary<string, object?>(); bool denied = false;
            try { IndependentObservation.Compare(mismatch, independent, retained); } catch (InvalidDataException) { denied = true; }
            if (!denied || !retained.ContainsKey("installedOperatingState")) throw new InvalidDataException("Independent mismatch rejection/evidence failed.");
        }
        if (!baseline.Matches(baseline) || baseline.Matches(changed)) throw new InvalidDataException("Managed runtime reload detection failed.");
        try { ManagedRuntimeIdentity.Parse("malformed"); throw new Exception("Malformed TASKSTAT was accepted."); }
        catch (InvalidDataException) { }
    }
}


