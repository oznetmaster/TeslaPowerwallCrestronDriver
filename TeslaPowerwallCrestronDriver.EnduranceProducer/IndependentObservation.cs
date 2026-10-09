// Copyright (c) 2026 Neil Colvin. Licensed under the MIT License with Commons Clause.
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrestronHomeDevTools;
using TeslaPowerwallLibrary;

sealed record IndependentReadings(DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, double BatteryPercent,
    double SolarWatts, double GridWatts, double LoadWatts, double ReservePercent, string Mode);
static class IndependentObservation
{
    public static async Task<IndependentReadings> ReadAsync(string path, string digest, string expectedHost, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Independent input must be an absolute private file.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 65536) throw new InvalidDataException("Independent input size is invalid.");
        var bytes = new byte[file.Length]; await file.ReadExactlyAsync(bytes, token);
        try
        {
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Independent input differs from its reviewed binding.");
            using var document = JsonDocument.Parse(bytes); var p = document.RootElement;
            string Text(string key) => p.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            if (Text("mode") != "local" || Text("localProtocol") != "Signed LAN" || Text("host") != expectedHost ||
                string.IsNullOrWhiteSpace(Text("password")) || string.IsNullOrWhiteSpace(Text("localSigningKey")))
                throw new InvalidDataException("Independent input does not select the reviewed signed-LAN site.");
            using var key = RSA.Create(); key.FromXmlString(Text("localSigningKey"));
            if (key.KeySize != 4096) throw new InvalidDataException("Independent input requires the registered RSA-4096 key.");
            using var client = new Powerwall(new PowerwallOptions { Host = expectedHost, Password = Text("password"),
                LocalProtocol = PowerwallLocalProtocol.TedapiSigned, LocalSigningKey = key, AllowLocalControl = false,
                NoLocalSessionPersistence = true, CacheExpireSeconds = 0, Timeout = TimeSpan.FromSeconds(20) });
            var started = DateTimeOffset.UtcNow;
            if (!await client.ConnectAsync(token)) throw new InvalidDataException("Independent read-only local connection failed.");
            var level = await client.LevelAsync(cancellationToken: token);
            var power = await client.GetPowerReadingsAsync(token);
            var reserve = await client.GetReserveAsync(force: true, cancellationToken: token);
            var mode = await client.GetModeAsync(force: true, cancellationToken: token);
            double Value(double? value) => value is double number && double.IsFinite(number) ? number : throw new InvalidDataException("Independent Tesla reading is missing or nonfinite.");
            var readings = new IndependentReadings(started, DateTimeOffset.UtcNow, Value(level), Value(power.Solar), Value(power.Site), Value(power.Load), Value(reserve), mode ?? "");
            if (readings.BatteryPercent is < 0 or > 100 || readings.ReservePercent is < 0 or > 100 || string.IsNullOrWhiteSpace(readings.Mode))
                throw new InvalidDataException("Independent Tesla battery/operating state is invalid.");
            return readings;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static void Compare(DeviceInfo device, IndependentReadings independent, IDictionary<string, object?> evidence)
    {
        var p = device.PropertyValues;
        evidence["installedOperatingState"] = new { mode = p.TryGetValue("operationMode", out var actualMode) ? actualMode.ToString() : "<absent>", reserve = p.TryGetValue("backupReservePercent", out var actualReserve) ? actualReserve.ToString() : "<absent>" };
        if (!p.TryGetValue("operationMode", out var mode) || mode.ValueKind != JsonValueKind.String || mode.GetString() != independent.Mode ||
            !p.TryGetValue("backupReservePercent", out var reserve) || reserve.ValueKind != JsonValueKind.Number ||
            reserve.GetDouble() != Math.Round(independent.ReservePercent, MidpointRounding.AwayFromZero))
            throw new InvalidDataException("Published Tesla operating state differs from the independent read.");
        evidence["comparison"] = new { equalityCompared = new[] { "operationMode", "backupReservePercent" },
            validityOnly = new[] { "battery", "solar", "grid", "load" },
            rationale = "Operating mode and rounded reserve are compared exactly. Instantaneous power and battery samples have different timestamps and are retained for validity/diagnostics; no electrical-meter accuracy claim is made." };
    }
}

static class ReadingFreshness
{
    public static double AgeSeconds(string status, DateTime localNow)
    {
        if (!status.StartsWith("Updated ", StringComparison.Ordinal) ||
            !DateTime.TryParseExact(status[8..], new[] { "HH:mm", "H:mm", "h:mm tt" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new InvalidDataException("Published Tesla update time cannot be bounded.");
        var today = new DateTime(localNow.Year, localNow.Month, localNow.Day, time.Hour, time.Minute, 0, DateTimeKind.Unspecified);
        var shown = new[] { today, today.AddDays(-1) }.MinBy(value => Math.Abs((localNow - value).TotalSeconds));
        return (localNow - shown).TotalSeconds;
    }
    public static bool IsCurrent(string status, DateTime localNow, int maximumSeconds)
    {
        var age = AgeSeconds(status, localNow); return maximumSeconds > 0 && age >= -2 && age <= maximumSeconds;
    }
}