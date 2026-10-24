# Copyright (c) 2026 Neil Colvin. Licensed under the terms in LICENSE.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Commit,
    [Parameter(Mandatory)][string]$BuildRunId
)
$ErrorActionPreference = 'Stop'
$receipt = Get-Content -LiteralPath (Join-Path $Directory 'StagedRelease.json') -Raw | ConvertFrom-Json
if ($receipt.Repository -cne 'oznetmaster/TeslaPowerwallCrestronDriver' -or $receipt.Version -cne $Version -or $receipt.Commit -cne $Commit -or $receipt.BuildRunId -cne $BuildRunId) { throw 'Staging provenance mismatch.' }
$required = @('TeslaPowerwallCrestronDriver.pkg', "CrestronHomeDriver.Tesla.Powerwall.$Version.nupkg", 'TeslaPowerwallLocalSetup-win-x64.zip', 'PRODUCT-SHA256SUMS.txt', 'TeslaPowerwallCrestronDriver.ProcessorTests.pkg', 'TeslaPowerwallCrestronDriver.ProcessorTests-Documentation.zip', 'TeslaPowerwallCrestronDriver.ProcessorTests.sources.json', 'PROCESSOR-TESTS-SHA256SUMS.txt')
if (@($receipt.Files).Count -ne $required.Count) { throw 'Incomplete or unexpected staged inventory.' }
foreach ($name in $required) {
    $entry = @($receipt.Files | Where-Object Name -CEQ $name)
    if ($entry.Count -ne 1 -or $entry[0].Sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "Invalid inventory entry: $name" }
    if ((Get-FileHash -LiteralPath (Join-Path $Directory $name) -Algorithm SHA256).Hash -ine $entry[0].Sha256) { throw "Staged asset changed: $name" }
}
if (@(Get-ChildItem -LiteralPath $Directory -File).Count -ne ($required.Count + 1)) { throw 'Unexpected staged files.' }
& "$PSScriptRoot/Assert-DriverPackageVersion.ps1" -PackagePath (Join-Path $Directory 'TeslaPowerwallCrestronDriver.pkg') -ManifestPath "$PSScriptRoot/../TeslaPowerwallCrestronDriver/TeslaPowerwallCrestronDriver.json" -ReleaseVersion $Version
Add-Type -AssemblyName System.IO.Compression.FileSystem
$driverArchive = [IO.Compression.ZipFile]::OpenRead((Join-Path $Directory 'TeslaPowerwallCrestronDriver.pkg'))
try {
    $names = @($driverArchive.Entries | ForEach-Object FullName)
    if (@($names | Where-Object { $_.Contains('\') }).Count) { throw 'Driver ZIP entries must use forward slashes.' }
    if (@($names | Sort-Object -Unique -CaseSensitive).Count -ne $names.Count) { throw 'Duplicate driver ZIP entries.' }
} finally { $driverArchive.Dispose() }
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $Directory "CrestronHomeDriver.Tesla.Powerwall.$Version.nupkg"))
try {
    $entry = @($archive.Entries | Where-Object FullName -CEQ 'crestron-driver-package.json')
    if ($entry.Count -ne 1) { throw 'The NuGet distribution manifest is missing.' }
    $reader = [IO.StreamReader]::new($entry[0].Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.packageId -cne 'CrestronHomeDriver.Tesla.Powerwall' -or $manifest.packageVersion -cne $Version -or $manifest.payloadFile -cne 'TeslaPowerwallCrestronDriver.pkg' -or $manifest.containsManagedDllPayload -ne $false -or $manifest.packageType -cne 'CrestronHomeDriver') { throw 'Invalid NuGet distribution manifest.' }
    $payload = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.pkg', [StringComparison]::OrdinalIgnoreCase) })
    if ($payload.Count -ne 1 -or $payload[0].FullName -cne $manifest.payloadFile) { throw 'Invalid NuGet payload inventory.' }
    $stream = $payload[0].Open(); $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') } finally { $sha.Dispose(); $stream.Dispose() }
    if ($hash -ine (Get-FileHash -LiteralPath (Join-Path $Directory $manifest.payloadFile)).Hash) { throw 'NuGet and standalone driver payloads differ.' }
} finally { $archive.Dispose() }
Write-Output 'Staged source, inventory, driver version and NuGet payload verified.'
