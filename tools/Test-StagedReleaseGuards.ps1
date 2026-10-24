# Copyright (c) 2026 Neil Colvin. Licensed under the terms in LICENSE.
$ErrorActionPreference = 'Stop'
$version = ([version](Get-Content "$PSScriptRoot/../TeslaPowerwallCrestronDriver/TeslaPowerwallCrestronDriver.json" -Raw | ConvertFrom-Json).GeneralInformation.DriverVersion).ToString(3)
$commit = 'a' * 40
$directory = Join-Path ([IO.Path]::GetTempPath()) ('tesla-stage-guards-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $directory
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Write-Zip([string]$Path, [hashtable]$Contents) {
    $archive = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Contents.Keys) {
            $entry = $archive.CreateEntry($name); $stream = $entry.Open()
            try { $bytes = [Text.Encoding]::UTF8.GetBytes($Contents[$name]); $stream.Write($bytes) } finally { $stream.Dispose() }
        }
    } finally { $archive.Dispose() }
}
function Save-Receipt {
    $files = @(Get-ChildItem $directory -File | Where-Object Name -NE 'StagedRelease.json' | ForEach-Object { @{ Name=$_.Name; Sha256=(Get-FileHash $_.FullName).Hash } })
    @{ Repository='oznetmaster/TeslaPowerwallCrestronDriver'; Version=$version; Commit=$commit; BuildRunId='123'; Files=$files } | ConvertTo-Json -Depth 4 | Set-Content "$directory/StagedRelease.json"
}
function Verify { & "$PSScriptRoot/Assert-StagedRelease.ps1" -Directory $directory -Version $version -Commit $commit -BuildRunId 123 | Out-Null }
$checks = 0
function Reject([scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (!$rejected) { throw 'A modified or misbound staged release was accepted.' }
    $script:checks++
}
try {
    $pkg = Join-Path $directory 'TeslaPowerwallCrestronDriver.pkg'
    Write-Zip $pkg @{ 'driver.dat'=(@{ driverVersion="$version.0" } | ConvertTo-Json) }
    foreach ($name in @('TeslaPowerwallLocalSetup-win-x64.zip','PRODUCT-SHA256SUMS.txt','TeslaPowerwallCrestronDriver.ProcessorTests.pkg','TeslaPowerwallCrestronDriver.ProcessorTests-Documentation.zip','TeslaPowerwallCrestronDriver.ProcessorTests.sources.json','PROCESSOR-TESTS-SHA256SUMS.txt')) { Set-Content (Join-Path $directory $name) 'synthetic fixture' }
    $nupkg = Join-Path $directory "CrestronHomeDriver.Tesla.Powerwall.$version.nupkg"
    $manifest = @{ packageId='CrestronHomeDriver.Tesla.Powerwall'; packageVersion=$version; payloadFile='TeslaPowerwallCrestronDriver.pkg'; containsManagedDllPayload=$false; packageType='CrestronHomeDriver' }
    Write-Zip $nupkg @{ 'crestron-driver-package.json'=($manifest | ConvertTo-Json) }
    $archive = [IO.Compression.ZipFile]::Open($nupkg,[IO.Compression.ZipArchiveMode]::Update)
    try { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$pkg,'TeslaPowerwallCrestronDriver.pkg') | Out-Null } finally { $archive.Dispose() }
    Save-Receipt
    Verify; $checks++
    $cleanPkg = [IO.File]::ReadAllBytes($pkg)
    $cleanNuget = [IO.File]::ReadAllBytes($nupkg)
    $archive = [IO.Compression.ZipFile]::Open($pkg,[IO.Compression.ZipArchiveMode]::Update)
    try { $archive.CreateEntry('Licenses\fixture.txt') | Out-Null } finally { $archive.Dispose() }
    $archive = [IO.Compression.ZipFile]::Open($nupkg,[IO.Compression.ZipArchiveMode]::Update)
    try {
        $archive.GetEntry('TeslaPowerwallCrestronDriver.pkg').Delete()
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$pkg,'TeslaPowerwallCrestronDriver.pkg') | Out-Null
    } finally { $archive.Dispose() }
    Save-Receipt
    Reject { Verify }
    [IO.File]::WriteAllBytes($pkg,$cleanPkg)
    [IO.File]::WriteAllBytes($nupkg,$cleanNuget)
    Save-Receipt
    Reject { & "$PSScriptRoot/Assert-StagedRelease.ps1" -Directory $directory -Version $version -Commit ('b'*40) -BuildRunId 123 }
    Reject { & "$PSScriptRoot/Assert-StagedRelease.ps1" -Directory $directory -Version $version -Commit $commit -BuildRunId 124 }
    $original = [IO.File]::ReadAllBytes($pkg)
    [IO.File]::AppendAllText($pkg,'changed')
    Reject { Verify }
    [IO.File]::WriteAllBytes($pkg,$original)
    Set-Content "$directory/unexpected.txt" 'unexpected'
    Reject { Verify }
    Remove-Item -LiteralPath "$directory/unexpected.txt"
    $receipt = Get-Content "$directory/StagedRelease.json" -Raw
    $bad = $receipt | ConvertFrom-Json; $bad.Files = @($bad.Files | Select-Object -Skip 1)
    $bad | ConvertTo-Json -Depth 4 | Set-Content "$directory/StagedRelease.json"
    Reject { Verify }
    [IO.File]::WriteAllText("$directory/StagedRelease.json",$receipt)
    $archive = [IO.Compression.ZipFile]::Open($nupkg,[IO.Compression.ZipArchiveMode]::Update)
    try {
        $archive.GetEntry('TeslaPowerwallCrestronDriver.pkg').Delete()
        $stream = $archive.CreateEntry('TeslaPowerwallCrestronDriver.pkg').Open()
        try { $stream.Write([byte[]](1,2,3)) } finally { $stream.Dispose() }
    } finally { $archive.Dispose() }
    Save-Receipt
    Reject { Verify }
    Write-Output "$checks staged-release provenance and payload guard checks passed."
} finally {
    $resolved = [IO.Path]::GetFullPath($directory)
    if (!$resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected temporary fixture path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
