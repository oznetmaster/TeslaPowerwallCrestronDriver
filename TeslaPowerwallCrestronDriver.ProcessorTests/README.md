# TeslaPowerwallCrestronDriver processor tests

This standalone Entity V2 **Utility** package runs the driver test assembly on Crestron Home's Mono runtime. It has its own identity and NUnit tile. The production driver can remain installed alongside it. The test package does not start or configure the production driver's installed instance.

## Build and deploy

Open `TeslaPowerwallCrestronDriver.slnx` in Visual Studio with a current .NET SDK, the .NET Framework 4.7.2 targeting pack, and the Crestron Driver SDK installed. Clone [CrestronHomeNUnit](https://github.com/oznetmaster/CrestronHomeNUnit) beside this repository, or set `ProcessorTestSdkRoot` privately. Build **TeslaPowerwallCrestronDriver.ProcessorTests**, Debug. Enable `DeployAfterBuild` in a private `.csproj.user` file with the same deployment properties as the production driver. Debug deployment is performed only by Visual Studio. Keep credentials and machine paths in files excluded through `.git/info/exclude`; never commit them.

For a command-line package build without deployment:

```powershell
dotnet build TeslaPowerwallCrestronDriver.ProcessorTests/TeslaPowerwallCrestronDriver.ProcessorTests.csproj -c Debug -p:BuildProcessorTestPackages=true -p:DeployAfterBuild=false
```

The package appears at `bin/Debug/net472/TeslaPowerwallCrestronDriver.ProcessorTests.pkg`. Add **TeslaPowerwallCrestronDriver Tests** from **Utility** in Configure. No separate NUnit host package is required.

## Suites

- **Unit Tests**: 61 offline driver cases. Run on Windows through the NUnit Visual Studio adapter or on the processor. No account credentials or physical devices are needed.
- **Processor Lifecycle**: 20 SDK lifecycle checks. Changing authentication mode or Fleet application requires a fresh token; rejected edits preserve active configuration; refresh interval boundaries are validated; clearing configuration removes active and pending credentials and resets availability. Run these separately on the processor; the shared desktop harness provides additional validation.

Use the Windows runner's **Find packages**, select this package, connect, then select a suite and **Run all**. Discovery uses a dynamically assigned port. The standalone tile exposes the same suites and results. Nothing runs automatically on deployment. Original driver assets are under `DriverTestData`; the test tile's assets retain their own root paths.

The automatic unit and lifecycle suites do not authenticate with external services or operate physical devices. Processor lifecycle results must be verified on real hardware; desktop unit success does not establish processor lifecycle compatibility.

This project targets only `net472`. It is not packable or publishable to NuGet. See [third-party notices](THIRD-PARTY-NOTICES.md), the root LICENSE, and [runner documentation](https://github.com/oznetmaster/CrestronHomeNUnit#readme).


## Expanded coverage

Changing authentication mode or Fleet application requires a fresh token; rejected edits preserve active configuration; refresh interval boundaries are validated; clearing configuration removes active and pending credentials and resets availability.

The package contains 61 offline cases and 20 lifecycle cases. Lifecycle tests exercise newly constructed test entities, not the installed production driver. Both suites are selectable in the Windows runner and through the standalone Utility tile. Processor hardware validation remains required.


Hosted and release validation compare the exact discovered test identities with execution results and the merged package, rather than maintaining a duplicate expected test count. Live tests are discovered but not operated in hosted CI. Only documented processor-runtime skips are accepted by the Windows net472 check; the desktop SDK harness must execute every automatic test successfully.

The 1.1.8 driver candidate uses TeslaPowerwallLibrary 2.0.0. The eight additional offline cases validate library-to-Crestron log filtering, severity and per-driver scopes. Desktop test-runner dependencies and their Newtonsoft.Json dependency are excluded from the processor merge.

## Read-only live API matrix

The manual `live` suite contains three real-site tests covering ready state and site selection, battery/operating-state publication and subsequent refresh. Run it twice, once under a dedicated Owner credential-helper session and once under a dedicated Fleet session. These are distinct APIs and passing one does not validate the other. Supply the helper-generated `LiveTestSettings.json` as the live suite input and explicitly enable live tests. Run the same three fixtures in the desktop SDK harness using `TestCategory=Live`. Never copy refresh tokens to the processor. Both live configurations are separate required local release-validation runs.


Read-only live validation on 22 September 2026 passed separately against the Owner and Fleet APIs. In each credential session, all three direct library tests passed on net472, .NET 10 and the development processor; all three driver tests passed in the desktop SDK harness and on the processor. The 142 library and 81 driver offline cases also passed twice on the processor in each API run, with no failures or skips. Temporary test instances and package archives were removed and reservations released. No installed driver update, Powerwall setting changes or processor reboot was performed. The release preparation report records package hashes and full test counts.
