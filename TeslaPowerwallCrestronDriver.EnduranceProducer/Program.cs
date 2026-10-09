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
            request.Plan.Requirement.Id != settings.RequirementId || request.Plan.ProcessorIdentity != $"processor:{settings.ProcessorHost}")
            throw new InvalidDataException("Candidate, processor, or endurance policy differs from pinned settings.");
        using var deadline = new CancellationTokenSource(request.Plan.ProbeTimeout);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Saved processor credentials require Windows.");
        var credential = DevToolsCredentialBindings.Read(settings.CredentialBindings).Resolve(DevToolsCredentialPurpose.Processor, settings.ProcessorHost);
        if (string.IsNullOrWhiteSpace(credential.CertificateSha256) || string.IsNullOrWhiteSpace(credential.SshFingerprint))
            throw new InvalidDataException("Verified processor trust pins are required.");
        var network = new NetworkCredential(credential.UserName, credential.Password);
        var uptime = await ProcessorUptime.ReadAsync(settings.ProcessorHost, network, credential.SshFingerprint, TimeSpan.FromSeconds(30), deadline.Token);
        var runtime = await LifetimeEvidence.ReadAsync(settings.ProcessorHost, network, credential.SshFingerprint, deadline.Token);
        boot = await Baseline.ReadOrCreateAsync(settings.BaselineFile, request.Plan, settings, uptime, runtime, deadline.Token);
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
    string DriverName, string DriverModel, string DriverVersion, SubmissionEvidenceIdentity Identity, string InstallationIdentity, string RequirementId, string BaselineFile, string LiveInputFile, string LiveInputSha256, string LocalHost, int MaximumReadingAgeSeconds)
{
    public void Validate()
    {
        if (DeviceId <= 0 || LocationId <= 0 || new[] { ProcessorHost, CredentialBindings, PackagePath, CatalogueId, DriverName, DriverModel, DriverVersion, InstallationIdentity, RequirementId, BaselineFile }.Any(string.IsNullOrWhiteSpace) ||
            !Path.IsPathFullyQualified(CredentialBindings) || !Path.IsPathFullyQualified(PackagePath) || !Path.IsPathFullyQualified(BaselineFile) || !Path.IsPathFullyQualified(LiveInputFile) || string.IsNullOrWhiteSpace(LocalHost) || LiveInputSha256.Length != 64 || !LiveInputSha256.All(Uri.IsHexDigit) || MaximumReadingAgeSeconds <= 0) throw new InvalidDataException("Complete reviewed Tesla producer settings.");
        var published = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(BaselineFile).StartsWith(published, StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(LiveInputFile).StartsWith(published, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Lifetime baseline and private Tesla input must be outside the pinned producer.");
    }
}
static class Baseline
{
    sealed record Saved(string Plan, string Settings, DateTimeOffset Earliest, DateTimeOffset Latest, ManagedRuntimeIdentity Runtime, string Identity);
    public static async Task<string> ReadOrCreateAsync(string path, SubmissionEndurancePlan plan, Settings settings, ProcessorUptimeSnapshot uptime, ManagedRuntimeIdentity runtime, CancellationToken token)
    {
        var planHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan)));
        var settingsHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(settings)));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bool existing = File.Exists(path);
        await using var stream = new FileStream(path, existing ? FileMode.Open : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        Saved baseline;
        if (!existing) { baseline = new(planHash, settingsHash, uptime.EarliestStartUtc, uptime.LatestStartUtc, runtime, $"processor-boot:{uptime.EarliestStartUtc:O}:{uptime.LatestStartUtc:O};ssp:{runtime.ParentPid};children:{string.Join(',', runtime.ChildPids)}"); await JsonSerializer.SerializeAsync(stream, baseline, cancellationToken: token); stream.Flush(true); }
        else { if (stream.Length is 0 or > 65536) throw new InvalidDataException("Lifetime acquisition is incomplete or invalid."); baseline = await JsonSerializer.DeserializeAsync<Saved>(stream, cancellationToken: token) ?? throw new InvalidDataException("Lifetime baseline is invalid."); }
        if (baseline.Plan != planHash || baseline.Settings != settingsHash || uptime.EarliestStartUtc > baseline.Latest.AddSeconds(2) || uptime.LatestStartUtc < baseline.Earliest.AddSeconds(-2) || baseline.Runtime is null || !baseline.Runtime.Matches(runtime)) throw new InvalidDataException("Processor boot or managed driver runtime identity changed.");
        return baseline.Identity;
    }
}
static class Checks
{
    public static void Run()
    {
        bool rejected = false;
        try { new Settings("", "x", "x", "x", 0, 0, "x", "x", "x", null!, "", "", "x", "x", "x", "x", 0).Validate(); }
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


