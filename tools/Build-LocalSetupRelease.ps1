# Copyright (c) 2026 Neil Colvin.
# Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Use a new output directory for each release build.' }
$version = ([version](Get-Content "$root/TeslaPowerwallCrestronDriver/TeslaPowerwallCrestronDriver.json" -Raw | ConvertFrom-Json).GeneralInformation.DriverVersion).ToString(3)
$publish = Join-Path $destination 'publish'
dotnet publish "$root/TeslaPowerwallCrestronDriver.LocalSetup/TeslaPowerwallCrestronDriver.LocalSetup.csproj" -c Release -r win-x64 --self-contained true -p:Version=$version -o $publish
if ($LASTEXITCODE) { throw 'Local provisioning tool publish failed.' }
Copy-Item "$root/TeslaPowerwallCrestronDriver/IncludeInPkg/Licenses" -Destination $publish -Recurse
$runtime = (Get-Content "$publish/TeslaPowerwallCrestronDriver.LocalSetup.runtimeconfig.json" -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks | Where-Object name -eq Microsoft.NETCore.App
$assets = Get-Content "$root/TeslaPowerwallCrestronDriver.LocalSetup/obj/project.assets.json" -Raw | ConvertFrom-Json
$noticeRoot = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ "microsoft.netcore.app.runtime.win-x64/$($runtime.version)" } | Where-Object { Test-Path "$_/THIRD-PARTY-NOTICES.TXT" }) | Select-Object -First 1
if (!$noticeRoot) { throw 'Bundled runtime license notices were not found.' }
Copy-Item "$noticeRoot/LICENSE.TXT" "$publish/Licenses/Bundled-Runtime-LICENSE.txt"
Copy-Item "$noticeRoot/THIRD-PARTY-NOTICES.TXT" "$publish/Licenses/Bundled-Runtime-NOTICES.txt"
$exe = Join-Path $publish 'TeslaPowerwallCrestronDriver.LocalSetup.exe'
& $exe --help
if ($LASTEXITCODE) { throw 'Published provisioning tool did not start.' }
$forbidden = @(Get-ChildItem $publish -File -Recurse | Where-Object { $_.Name -match '^(LiveTestSettings|DriverConfiguration|profile)\.(json|dat)$' -or $_.Extension -in @('.pfx','.pem','.key') })
if ($forbidden.Count) { throw 'Private material must not be included in release assets.' }
$zip = Join-Path $destination 'TeslaPowerwallLocalSetup-win-x64.zip'
Compress-Archive -Path "$publish/*" -DestinationPath $zip
Write-Output $zip
