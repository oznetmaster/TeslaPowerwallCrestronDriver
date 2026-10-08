# TeslaPowerwallCrestronDriver processor tests

## Test package 1.2.0

The release includes the standalone test `.pkg`, documentation, source revisions and checksums. It uses NUnit 5.0.0 and the public CrestronHomeNUnit SDK 2.3.0. Unit and lifecycle suites run without cloud or Powerwall access. Manual live suites require private inputs and are read-only.

This standalone Entity V2 **Utility** package runs the driver test assembly on Crestron Home's Mono runtime. It has its own identity and NUnit tile. The production driver can remain installed alongside it. The test package does not start or configure the production driver's installed instance.

## Build and deploy

Open `TeslaPowerwallCrestronDriver.slnx` in Visual Studio with a current .NET SDK, the .NET Framework 4.7.2 targeting pack, and the Crestron Driver SDK installed. Clone [CrestronHomeNUnit](https://github.com/oznetmaster/CrestronHomeNUnit) beside this repository, or set `ProcessorTestSdkRoot` privately. Build **TeslaPowerwallCrestronDriver.ProcessorTests**, Debug. Enable `DeployAfterBuild` in a private `.csproj.user` file with the same deployment properties as the production driver. Debug deployment is performed only by Visual Studio. Keep credentials and machine paths in files excluded through `.git/info/exclude`; never commit them.

For a command-line package build without deployment:

```powershell
dotnet build TeslaPowerwallCrestronDriver.ProcessorTests/TeslaPowerwallCrestronDriver.ProcessorTests.csproj -c Debug -p:BuildProcessorTestPackages=true -p:DeployAfterBuild=false
```

The package appears at `bin/Debug/net472/TeslaPowerwallCrestronDriver.ProcessorTests.pkg`. Add **TeslaPowerwallCrestronDriver Tests** from **Utility** in Configure. No separate NUnit host package is required.

## Suites

- **Unit Tests**: 84 offline driver cases. Run on Windows through the NUnit Visual Studio adapter or on the processor. No account credentials or physical devices are needed.
- **Processor Lifecycle**: 29 SDK lifecycle checks. Changing authentication mode or Fleet application requires a fresh token; rejected edits preserve active configuration; refresh interval boundaries are validated; clearing configuration removes active and pending credentials and resets availability. Run these separately on the processor; the shared desktop harness provides additional validation.

Use the Windows runner's **Find packages**, select this package, connect, then select a suite and **Run all**. Discovery uses a dynamically assigned port. The standalone tile exposes the same suites and results. Nothing runs automatically on deployment. Original driver assets are under `DriverTestData`; the test tile's assets retain their own root paths.

The automatic unit and lifecycle suites do not authenticate with external services or operate physical devices. Processor lifecycle results must be verified on real hardware; desktop unit success does not establish processor lifecycle compatibility.

This project targets only `net472`. It is not packable or publishable to NuGet. See [third-party notices](THIRD-PARTY-NOTICES.md), the root LICENSE, and [runner documentation](https://github.com/oznetmaster/CrestronHomeNUnit#readme).


## Expanded coverage

Changing authentication mode or Fleet application requires a fresh token; rejected edits preserve active configuration; refresh interval boundaries are validated; clearing configuration removes active and pending credentials and resets availability.

The package contains 84 offline cases and 29 lifecycle cases. Lifecycle tests exercise newly constructed test entities, not the installed production driver. Both suites are selectable in the Windows runner and through the standalone Utility tile. Processor hardware validation remains required.


Hosted and release validation compare the exact discovered test identities with execution results and the merged package, rather than maintaining a duplicate expected test count. Live tests are discovered but not operated in hosted CI. Only documented processor-runtime skips are accepted by the Windows net472 check; the desktop SDK harness must execute every automatic test successfully.

The 1.2.0 driver uses TeslaPowerwallLibrary 2.1.0. Offline cases cover cloud compatibility, local configuration, capability-dependent UI and read-only controls, portable inputs, refresh lifetime, and per-driver logging. Desktop test-runner dependencies and their Newtonsoft.Json dependency are excluded from the processor merge.

## Read-only live API matrix

The manual `live` suite contains three common read-only tests. Run Owner and Fleet separately using dedicated credential-helper sessions. The `local-live` suite contains those three tests plus three live UI-binding checks for Gateway or signed LAN. See the [shared input contract and connection matrix](../TeslaPowerwallCrestronDriver.Tests/README.md). Supply the same private `LiveTestSettings.json` used by the library and Windows tests. Use a registered portable RSA key for processor signed LAN; a Windows CNG key name is not portable. Home-network Wi-Fi uses the same local API as Ethernet; temporary setup Wi-Fi is excluded.


Read-only live validation on 22 September 2026 passed separately against the Owner and Fleet APIs. In each credential session, all three direct library tests passed on net472, .NET 10 and the development processor; all three driver tests passed in the desktop SDK harness and on the processor. The 142 library and 81 driver offline cases also passed twice on the processor in each API run, with no failures or skips. Temporary test instances and package archives were removed and reservations released. No installed driver update, Powerwall setting changes or processor reboot was performed. The release preparation report records package hashes and full test counts.
