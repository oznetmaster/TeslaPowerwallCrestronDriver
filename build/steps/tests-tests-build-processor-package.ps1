dotnet build TeslaPowerwallCrestronDriver.ProcessorTests/TeslaPowerwallCrestronDriver.ProcessorTests.csproj -c Release -p:BuildProcessorTestPackages=true -p:ReleaseVersion=2.0.0 -p:DeployAfterBuild=false "-p:ManifestUtilExe=$env:MANIFEST_UTIL_EXE"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
