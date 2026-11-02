$output = Join-Path $PWD 'TeslaPowerwallCrestronDriver.ProcessorTests/bin/Release/net472'
$extracted = Join-Path $env:RUNNER_TEMP 'driver-package-tests'
[IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $output 'TeslaPowerwallCrestronDriver.ProcessorTests.pkg'), $extracted)
./tools/Test-DiscoveredCoverage.ps1 -Stage Package -SdkRoot ../CrestronHomeNUnit -PackageAssembly (Join-Path $extracted 'TeslaPowerwallCrestronDriver.ProcessorTests.dll') -SourceInventory TestResults/TeslaPowerwallCrestronDriver.Tests/inventory.json -ResultsDirectory TestResults
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
