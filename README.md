# TeslaPowerwallCrestronDriver

For shipped changes, see the [changelog](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/CHANGELOG.md). Test, CI and build history is recorded separately in [development and validation history](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/DEVELOPMENT-HISTORY.md).


The driver tile appears on the **Home screen only**. It is not displayed on room screens; its room assignment remains available for configuration.

See the [changelog](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/CHANGELOG.md) for release history and the [release notes](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/RELEASE-NOTES.md) for the current driver update. Driver releases are made for runtime fixes or dependency changes; adding tests alone does not require a driver release.

A **Crestron Home** Entity V2 driver that integrates a **Tesla Powerwall** energy site via local access, the Tesla Owner API or the official Tesla Fleet API, providing live power flow, battery status, energy history, and site control from the Crestron Home app.

Tesla and Powerwall are trademarks of Tesla, Inc. This project is an independent, unofficial driver and is not affiliated with, endorsed by, or sponsored by Tesla, Inc. Crestron and Crestron Home are trademarks or registered trademarks of Crestron Electronics, Inc. This project is not affiliated with, endorsed by, or sponsored by Crestron Electronics, Inc.

[![License: MIT + Commons Clause](https://img.shields.io/badge/License-MIT%20%2B%20Commons%20Clause-blue.svg)](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/LICENSE)

---

## Driver Architecture

This driver is a **Crestron Home energy-automation Entity V2 driver** implemented on the **Crestron Home SDK Entity V2 Model**. It derives directly from `ReflectedAttributeDriverEntity` and exposes all configuration items, properties, commands, and extension UI bindings through SDK attributes and the entity model.

The driver connects to a single Tesla Powerwall energy site through the [TeslaPowerwallLibrary](https://github.com/oznetmaster/TeslaPowerwallLibrary) NuGet package, using TeslaPowerwallLibrary 2.1.0. Cloud connections obtain and renew access tokens from their configured refresh token. Local connections use their own endpoint and credentials; they do not require a running Windows application.

---

## Supported Connection Modes

Select **Connection** in Configure. For Owner or Fleet, choose **Cloud**: leave **Tesla Fleet API Client ID** blank for Owner, or set it for Fleet. Local choices are described below. Upgrading from a previous version requires completing the configuration again; see [Upgrading to 2.0.0](#upgrading-to-200).

### Tesla Owner API (Cloud)

Uses Tesla's unofficial, undocumented Owner API. Requires a one-time login via the companion `TeslaPowerwallLibrary.Setup` tool (see [Obtaining a Tesla Refresh Token](#obtaining-a-tesla-refresh-token) below), using your own Tesla account credentials via Tesla's own login page — this driver never sees or stores your password.

⚠️ **Stability notice:** The Owner API is unofficial and undocumented. As of July 2026, Tesla has begun deprecating Owner API access for **vehicle** endpoints (confirmed via multiple independent reports and Tesla's own error responses pointing to the official Fleet API). Energy/Powerwall endpoints appear unaffected at this time, but there is no guarantee this will remain the case. If Owner API access breaks for your site, please open an issue on this repository.

### Tesla Fleet API (Official)

Uses Tesla's officially supported, documented Fleet API. Requires a Tesla Fleet API application (with its own Client ID) and a Fleet API refresh token for your Tesla account. Configuring a **Tesla Fleet API Client ID** switches the driver into this mode, and the **Tesla Refresh Token** field must then contain a Fleet API refresh token rather than an Owner API one.

The Fleet API covers everything this driver uses — live status, energy/impact history, backup reserve, operating mode, and grid export control — with one exception: **Storm Watch is not available through the Fleet API**. When a Client ID is configured, the Storm Watch toggle is hidden from the Settings page and the `setStormWatchEnabled` command is a no-op.

### Local access

The driver connects over your home network using Ethernet or normal Powerwall Wi-Fi. These are two network interfaces to the same local API, not different connection modes.

- **Gateway** — customer-authenticated local HTTPS reads and supported reserve/mode controls. Available telemetry depends on the device.
- **Signed LAN** — Powerwall 3 TEDAPI over a hostname or IP address, using the local customer password and a separately enrolled RSA-4096 driver key. Follow [local provisioning](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/TeslaPowerwallCrestronDriver.LocalSetup/README.md).

Local status defaults to a configurable 15-second delay between completed polls. Settings changes require **Allow Local Setting Changes**. Energy and Impact pages require explicit **Use Cloud History with Local Access**, separate cloud credentials and the numeric ID of the same physical site. Cloud failure does not switch live monitoring away from local access. Storm Watch is available through Owner cloud mode only.

See the [TeslaPowerwallLibrary](https://github.com/oznetmaster/TeslaPowerwallLibrary) repository for the underlying library's full roadmap and known issues.

---

## Features

- Live house, solar, grid, and Powerwall power flow on the Main page
- Battery level, backup reserve, operation mode, and grid export status
- Energy page with Day/Week/Month/Year/Lifetime period selection, back/forward period navigation, and a per-sub-period breakdown (hourly/daily/weekly/monthly rows depending on the selected period)
- Impact page showing self-powered percentage, home usage, and grid usage for the selected period
- Settings page to adjust backup reserve, grid charging, Storm Watch, operation mode, and grid export mode directly from Crestron Home
- Automatic background polling with a configurable refresh interval
- Manual "Refresh Now" command
- Programmable commands and events for the Crestron Home programming environment (grid status changed to backup, grid status restored, Storm Watch activated/deactivated, battery reserve low, battery fully charged)

---

## Usage

The pages below roughly follow the layout of the Tesla mobile app, but everything shown is generated by this driver from the selected connection and any explicitly enabled cloud history — no app access is required to use it.

### Home tile and Main page

The device tile shows current home power consumption (with a `BKUP` suffix whenever the grid is down and the Powerwall is supplying the house) plus a compact summary of which sources are currently contributing. The Main page breaks that out into four rows — House, Solar, Grid, and Powerwall — each showing that source's live power, plus a status block showing a plain-language summary, the current operation mode, and the current grid export mode. From the Main page you can open Settings. Energy and Impact are available for cloud connections, or when cloud history is explicitly enabled for a local connection. Local values not supplied by the device remain unavailable.

### Energy page

Shows energy (kWh), not instantaneous power. Two selector rows control what's displayed:

- **Period** — Day, Week, Month, Year, or Lifetime. Back/forward arrows step through prior instances of the selected period (for example, Day steps one calendar day at a time); the forward arrow disables once you're back at the current period. Lifetime has no history to step through, so the arrows are hidden for it.
- **Type** — Solar, Powerwall, Grid, or House. Changes which totals are displayed for the selected period:
  - **Solar**: energy produced, home usage, and grid export (export line is hidden when grid export is set to Never)
  - **Powerwall**: energy discharged, energy charged, and the net of the two
  - **Grid**: energy imported, energy exported, and the net of the two
  - **House**: energy used, a Solar/Grid breakdown of where it came from, and how much the Powerwall contributed

Beneath the totals, a per-sub-period breakdown lists the same figures split into rows — Day shows up to 24 hourly rows, Week shows 7 daily rows, Month shows its weekly rows, and Year shows 12 monthly rows. Lifetime has no breakdown and hides this section entirely.

### Impact page

Shows how self-sufficient the site was for the selected period: a gauge with the self-powered percentage, plus how much of that usage came from solar/Powerwall versus the grid. It uses the same Period selector and back/forward navigation as the Energy page.

### Settings page

Shows the settings supplied by the selected connection. Local controls are disabled until **Allow Local Setting Changes** is enabled. Unsupported settings are hidden. Available controls include:

- **Backup Reserve** — the minimum battery charge percentage reserved for outages
- **Charge From Grid** — allow the battery to charge from the grid, not just from solar
- **Storm Watch** — Tesla's predictive pre-charge ahead of severe weather forecasts (only shown when connected via the Tesla Owner API)
- **Operation Mode** — Self Powered, Backup Only, or Autonomous
- **Grid Export** — Battery OK, Solar Only, or Never
- **Refresh Now** — forces an immediate refresh instead of waiting for the next scheduled poll

### Programming

For Crestron Home programmers, every Settings control above is also exposed as a programmable command, and the driver raises programmable events for grid status changed to backup/restored, Storm Watch activated/deactivated, battery reserve low, and battery fully charged — see [PowerwallDriver.Commands.cs](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/TeslaPowerwallCrestronDriver/PowerwallDriver.Commands.cs) for the full list.

---

## Prerequisites

| Requirement | Details |
|---|---|
| Crestron Home processor | Running a firmware version compatible with Entity V2 drivers |
| Cloud access or initial signed-key enrollment | A Tesla account with access to the site. Normal local reads do not require cloud credentials. |
| Cloud-mode refresh token | Required for cloud monitoring or optional cloud history. Obtained using the [TeslaPowerwallLibrary](https://github.com/oznetmaster/TeslaPowerwallLibrary) login tool — see [Obtaining a Tesla Refresh Token](#obtaining-a-tesla-refresh-token) below |

---

## Installation

The best way to download and install this driver on a Crestron Home system is to use the [Crestron Home Driver Feed Installer](https://github.com/oznetmaster/Crestron-Home-Driver-Feed-Installer) repository and application.

If you prefer to install manually, use the attached `.pkg` asset from the relevant GitHub Release. The automatic GitHub `Source code (zip)` and `Source code (tar.gz)` assets are repository snapshots, not installable Crestron driver packages.

NuGet package availability: this driver is also published as the `CrestronHomeDriver.Tesla.Powerwall` NuGet package. This NuGet package conforms to the **Crestron Home Driver NuGet Publishing Standard v1**. It is a distribution wrapper for the final `.pkg` artifact, includes the required `crestron-driver-package.json` manifest, and is not intended as a direct DLL reference package.

Crestron Home Driver NuGet Publishing Standard v1 is **not** an official Crestron product or specification. It is an open source packaging standard created to facilitate community distribution and discovery of Crestron Home drivers through NuGet.

1. Download `TeslaPowerwallCrestronDriver.pkg` from the release assets, or extract it from the NuGet package.
2. Connect to the processor using SFTP and your processor credentials. Upload the `.pkg` to `/user/ThirdPartyDrivers/Import` and allow the import to finish.
3. Open **Crestron Home Setup**, connect to the processor, and open **Pair Devices**. Select **Tesla Powerwall** under manufacturer **Tesla**, category **Energy Automation**.
4. Select the connection method and enter its configuration below. For signed local access, complete [local provisioning](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/TeslaPowerwallCrestronDriver.LocalSetup/README.md) first.
5. Save the configuration and assign the driver to a room. Its tile appears on the **Home screen**, not the room screen.
6. Open Home on a touch panel or app. Confirm that the tile is online and opens the live power page.

### Upgrading to 2.0.0

**An in-place driver update is supported, but reconfiguration is required.** This is an Entity V2 driver: keep the existing instance and apply the update in Crestron Home Setup. You do not need to remove and re-add the driver.

1. Before updating, record the connection type, site ID, Fleet Client ID if used, polling intervals, and any local control/history settings. Have the required credentials available to enter again. **Do not assume Crestron Home will retain configuration values during reconfiguration.**
2. Import the new package, then apply the available update to the existing **Tesla Powerwall** instance in Setup. Importing the package alone does not update that instance.
3. Reopen the driver's configuration and complete the new configuration dialogs. Do this even if you are keeping the existing cloud connection.
4. Review every field and re-enter any missing or incorrect values. Choose **Cloud** for Owner or Fleet and review the site, credentials and cloud interval. For local access, choose **Gateway** or **Signed LAN**, supply the home-network endpoint and local credentials, and review the local interval and control permissions. Signed LAN requires an enrolled signing key; optional cloud history is configured separately.
5. Save the configuration. Confirm installed version **2.0.000.0000**, the room assignment, online status, live readings and any programmed commands/events before returning the installation to service.

For cloud access, be prepared to obtain a new refresh token through the linked setup tool if the current credential is unavailable. Do not borrow another running client's rotating refresh token. For signed local access, keep the private provisioning profile/export so the same registered key can be supplied again; do not assume a masked field can be recovered from Crestron Home. Keep credential copies private.

The major version marks this required configuration step. An automatic, unattended upgrade is not sufficient. Existing cloud authentication modes and programmable interfaces remain available.

### Configuration

| Field | Description |
|---|---|
| Tesla Fleet API Client ID | Optional. Leave blank to use the Tesla Owner API. Set to a Tesla Fleet API application Client ID to use the Tesla Fleet API instead — when set, the refresh token below must be a Fleet API refresh token, and Storm Watch is unavailable. |
| Tesla Refresh Token | Required for Cloud or explicitly enabled cloud history; otherwise unused. OAuth refresh token for the Tesla account — an Owner API refresh token if Client ID above is blank, or a Fleet API refresh token if it is set. See [Obtaining a Tesla Refresh Token](#obtaining-a-tesla-refresh-token) below. |
| Tesla Energy Site ID | In Cloud mode: numeric ID or exact site name; blank selects the account's default site. For cloud history with local access: required exact numeric ID of the same physical site. |
| Cloud Refresh Interval Seconds | Delay after completed cloud reads, or between optional history refreshes in local mode. 30–3600 seconds; default 60. |
| Connection | Cloud (existing default), Gateway, or Signed LAN. Local access works over Ethernet or home-network Wi-Fi. |
| Local Hostname or IP Address | Required for local access. Hostnames are retained for DNS resolution; an optional HTTPS port is accepted. |
| Local Password | Local customer password for Gateway/Signed LAN. Masked and persistent. |
| Registered Local Signing Key | Signed LAN only: the registered RSA-4096 private key exported by the independent provisioning tool. Masked and persistent. |
| Allow Local Setting Changes | Default false. Enables supported controls for Gateway/Signed LAN. |
| Local Polling Interval Seconds | 15–3600 seconds; default 15. The same interval governs the local response cache. Slow responses extend the gap; requests do not overlap. |
| Use Cloud History with Local Access | Default false. Requires cloud credentials and the numeric ID of this exact site. Local-only monitoring never contacts the cloud. |

⚠️ **Changing the Client ID invalidates the cached refresh token.** Owner API and Fleet API refresh tokens are not interchangeable, and Fleet API refresh tokens are themselves scoped to the application (Client ID) that requested them. Adding, removing, or changing the Client ID therefore requires entering a new, matching refresh token in the same configuration update — the driver will reject the update with a validation error on the Tesla Refresh Token field if one isn't supplied.

### Obtaining a Tesla Refresh Token

Cloud monitoring and optional cloud history require an OAuth refresh token. Local monitoring uses its own credentials; signed-key enrollment is a separate provisioning step. Tesla's login flow requires an interactive browser sign-in, which isn't something a Crestron Home driver can perform on its own, so token retrieval is handled by a companion tool from the [TeslaPowerwallLibrary](https://github.com/oznetmaster/TeslaPowerwallLibrary) repository:

1. Go to the [TeslaPowerwallLibrary releases](https://github.com/oznetmaster/TeslaPowerwallLibrary/releases) page and download `TeslaPowerwallSetup-net10.0-windows.zip`.
2. Run the tool and sign in with your Tesla account credentials in the interactive login window. For Fleet API mode, supply your application's Client ID, Client Secret and registered redirect URI, select its region, then complete authorization in the embedded Tesla sign-in window; callback capture and token exchange are automatic. Initial partner registration also requires the registered domain and hosted public key. The Setup app offers **Sign in to Tesla** to skip registration for an existing application, and can remember application settings encrypted for your Windows account.
3. Copy the resulting refresh token shown by the tool.
4. Paste it into the **Tesla Refresh Token** field when configuring this driver in Crestron Home (and, for Fleet API mode, paste the Client ID into the **Tesla Fleet API Client ID** field).

Full step-by-step instructions, including screenshots, are in the [Tesla cloud login guide](https://oznetmaster.github.io/TeslaPowerwallLibrary/articles/login.html).

Tesla may periodically rotate the refresh token; this driver automatically persists an updated token if Tesla rotates it during normal use, so re-running the login tool should only be necessary if the stored token is lost or revoked.

---

## Building from Source

### Dependencies

- [TeslaPowerwallLibrary](https://www.nuget.org/packages/TeslaPowerwallLibrary) NuGet package
- [Crestron.DeviceDrivers.DevKit](https://www.nuget.org/packages/Crestron.DeviceDrivers.DevKit) NuGet package
- [Crestron.SimplSharp.SDK.Library](https://www.nuget.org/packages/Crestron.SimplSharp.SDK.Library) NuGet package
- `.NET Framework 4.7.2`
- [ILRepack](https://github.com/gluck/il-repack) via `ILRepackMerge.ps1`
- `PatchMergedAssembly.ps1` to rewrite merged assemblies for Crestron Home runtime compatibility
- `ManifestUtil.exe` from the Crestron.DeviceDrivers.ManifestUtil 29.0.10 NuGet package to produce the final `.pkg`

### Build

```powershell
dotnet build TeslaPowerwallCrestronDriver.slnx -c Release
```

The build pipeline:
1. Compiles the driver targeting `net472`
2. Bumps `DriverVersion` and `VersionDate` in `TeslaPowerwallCrestronDriver.json`
3. ILRepacks runtime dependencies into the driver assembly
4. Runs `PatchMergedAssembly.ps1` against the merged assembly
5. Packages the driver into a `.pkg` using Crestron's ManifestUtil

### GitHub Release Asset

This repository includes a GitHub Actions workflow that builds the Release package and attaches the generated `.pkg` to a GitHub Release.

The same release workflow also publishes the `CrestronHomeDriver.Tesla.Powerwall` NuGet package, which wraps the final generated `.pkg` artifact.

Typical release flow:
1. Push the release commit and tag
2. Publish the GitHub Release for that tag
3. Let the workflow build and attach the `.pkg` asset automatically

---

## Repository Notes

- XML documentation generation is enabled in the project build
- The release workflow builds the package on `windows-latest`
- The repository includes the driver package/build scripts needed for packaging and deployment

---

## License

MIT + Commons Clause © 2026 Neil Colvin — see [LICENSE](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/LICENSE).

Free to use and modify. You may not sell the Software as a standalone product or sublicense it.
Commercial system integration work (for example, a Crestron installer commissioning a customer system) is explicitly permitted, even where a fee is charged for that service.

Tesla and Powerwall are trademarks of Tesla, Inc.

> **Note:** This project references [Crestron.DeviceDrivers.DevKit](https://www.nuget.org/packages/Crestron.DeviceDrivers.DevKit),
> which is subject to Crestron's SDK license agreement. That license governs the SDK libraries only;
> the source code in this repository is licensed independently under the terms above.


## Automated tests

The solution includes `TeslaPowerwallCrestronDriver.Tests` (NUnit 5 with the Visual Studio NUnit adapter) and `TeslaPowerwallCrestronDriver.ProcessorTests` (a standalone Crestron Home Utility test package). The 87 offline unit cases exercise driver logic without credentials or real device commands. The 29 processor lifecycle cases are skipped by the net472 Windows run; the desktop SDK harness executes them alongside 11 Windows provisioning cases. See the [test guide](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/TeslaPowerwallCrestronDriver.Tests/README.md) and [Android UI tests](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/TeslaPowerwallCrestronDriver.AndroidTests/README.md).

```powershell
dotnet test TeslaPowerwallCrestronDriver.Tests/TeslaPowerwallCrestronDriver.Tests.csproj -c Release
```

Build the processor project in Debug in Visual Studio to build and deploy using private deployment settings. See [processor test instructions](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/TeslaPowerwallCrestronDriver.ProcessorTests/README.md) for setup, suites, tile operation and UI separation. Processor packages are not published to NuGet. See [CHANGELOG](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/CHANGELOG.md) for changes.


### Expanded driver behavior tests

Cover Owner/Fleet token callbacks from current and replaced clients, and delayed successful/failed refreshes after configuration is cleared or the driver is disposed. Reject callbacks and refresh results from superseded clients before updating credentials, cached state or availability.

Changing authentication mode or Fleet application requires a fresh token; rejected edits preserve active configuration; refresh interval boundaries are validated; clearing configuration removes active and pending credentials and resets availability.

The current source includes offline, SDK lifecycle and optional live API/UI-binding tests; see the [test matrix](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/TeslaPowerwallCrestronDriver.Tests/README.md). The processor package remains **net472 only**, appears under **Utility** in Configure, and can run independently through its own tile or the Windows NUnit runner. The offline and lifecycle fixtures use synthetic data. The optional Live Site suite exercises real driver polling and state publication against the selected Tesla energy site, without sending control commands.

`TeslaPowerwallCrestronDriver.Lifecycle.Tests` runs the entity checks against the real desktop SDK on .NET 10. It compiles the relevant driver sources and shares fixture sources with the net472 processor tests. Building this project does not deploy a driver. A locally supplied `Newtonsoft.Json.Compact.dll` is needed by the SDK's manifest reader; it is supplied by the processor at runtime and must not be added to source control or bundled with the processor test package.

```powershell
dotnet test TeslaPowerwallCrestronDriver.Tests/TeslaPowerwallCrestronDriver.Tests.csproj --filter "TestCategory!=Processor"
dotnet test TeslaPowerwallCrestronDriver.Lifecycle.Tests/TeslaPowerwallCrestronDriver.Lifecycle.Tests.csproj
```

Set `CompactJsonPath` in the desktop test project's private `DesktopTest.Local.props`, excluded through `.git/info/exclude`, or pass it as an MSBuild property. Keep machine paths and credentials out of tracked files.

Desktop success does not establish Mono compatibility. Build the processor test project in Visual Studio, deploy it, and run both suites on the processor. The fixtures cover configuration, restoration, refresh/reconnect races and disposal using simulated responses. Real installed-driver health and optional live-device checks remain separate from these repeatable suites.


### Driver build and release versions

The driver's JSON manifest is the source of its four-component build version. Debug builds increment only the fourth component; for example, `2.0.001.0005` becomes `2.0.001.0006`. MSBuild's `Version` and default `PackageVersion` are derived from that same manifest and refreshed after the increment; their numeric form is `2.0.1.6`. Assembly binding versions remain separate. Test-only references and IDE design-time builds do not increment the production driver version.

GitHub tags and NuGet releases retain three components: `v2.0.1` and `2.0.1`. Prepare the manifest's first three components for the intended release before tagging. Release CI checks that the tag matches, resets the fourth component to zero, and verifies the generated `.pkg` version against the manifest and release version before publishing. It does not increment the selected patch again. Local Release builds preserve the manifest. A later Debug build can legitimately be newer than a published release; the processor test package has its own independent version.

Deployment validation compares the exact built `.pkg` against the imported catalogue entry and installed instance, numerically including all four components. Upload/import alone does not activate the new version. Keep the tested package and its hash: rebuilding creates a new artifact that must be validated again.

Run `pwsh -File tools/Test-DriverVersioning.ps1` to check these rules with temporary manifests; this does not change the working driver manifest or deploy anything.

See [versioning details](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/docs/Versioning.md) for build, release and installed-instance verification rules.
### Desktop SDK dependency in CI

The SDK's desktop manifest reader needs its `Newtonsoft.Json.Compact.dll` runtime dependency. Supply a local SDK/runtime copy through the `CompactJsonPath` MSBuild property (or private `DesktopTest.Local.props`). Maintainer CI restores the same verified copy from encrypted Actions secrets into its temporary directory; it is not committed, attached to release assets or included in processor packages. Fork pull requests do not receive these secrets and require a trusted maintainer validation run.


For automated local tests, processor tests and gated driver deployment, see the [Crestron Home NUnit CI development guide](https://github.com/oznetmaster/CrestronHomeNUnit/blob/HEAD/docs/ContinuousIntegration.md). It covers private configuration, live-test gates, install/update waits, results and optional test-package removal.

Local build/deployment overrides can be created by copying [TeslaPowerwallCrestronDriver.Local.targets.example](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/TeslaPowerwallCrestronDriver/TeslaPowerwallCrestronDriver.Local.targets.example) to `TeslaPowerwallCrestronDriver.Local.targets` beside the project. Fill in your own paths privately and exclude the resulting local file with `.git/info/exclude`; it is not part of the published source.

### Optional live site tests

Use a separately issued test credential; each installed driver keeps its existing credentials and independent rotation. Do not copy one driver's refresh token into the tests. Owner API (`cloud`) and Fleet API (`fleet`) use separate test profiles. Local tests accept the same portable `LiveTestSettings.json` on Windows and the processor, including the local endpoint, password and registered signing key. See the [local provisioning guide](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/TeslaPowerwallCrestronDriver.LocalSetup/README.md).

The library's [test credential helper](https://github.com/oznetmaster/TeslaPowerwallLibrary/blob/HEAD/TeslaPowerwallLibrary.TestCredentials/README.md) accepts the initial refresh token and, for Fleet, the client ID. It maintains subsequent tokens in an encrypted Windows store and holds exclusive ownership while tests run. The helper is included with library 1.2.5; these live fixtures are included with driver 1.1.6. Dedicated Owner authentication passed all three live tests on Windows and the processor. The complete gated workflow also passed 73 local tests, 73 processor tests and three installed-driver health checks after updating the actual driver. Dedicated Fleet read-only tests have also passed on Windows and the processor.

Start a helper `session`, select **Live Site** in the Windows runner and supply its freshly generated `RunInputs/LiveTestSettings.json` using **Test inputs**. Keep the helper open until all tests stop. The runner enables the selected live suite for that session. For desktop automation, wrap the desktop SDK harness with the helper's `run` command, filter `TestCategory=Live`, and enable NUnit's `EnableLiveTests` parameter. The fixture reads `TestDataDirectory`, then `TESLA_LIVE_TEST_DATA_DIRECTORY`, then the private `%LOCALAPPDATA%/TeslaPowerwallCrestronDriver` folder. Ordinary CI excludes Live. The [input example](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/TeslaPowerwallCrestronDriver.Tests/LiveTestSettings.example.json) documents the generated format; users do not need to obtain access tokens manually.

The fixture shares one connection per run, verifies site selection and published battery/operating state, and refreshes site data. Internally, only an access token is sent to the processor, so package removal cannot discard the rotating test credential. If it expires, stop and prepare a new session. This live fixture does not validate the driver's credential-storage path or operate battery controls; separate lifecycle tests cover token callbacks and installed-driver workflow checks cover deployed health. Private settings, paths and credentials stay outside source control, packages and CI artifacts.

### Fleet account region — 1.1.6

Driver 1.1.6 uses TeslaPowerwallLibrary 1.2.5 to discover the authenticated account's Fleet region automatically. No new driver configuration field is required: supply the Fleet Client ID and refresh token as before. Earlier versions implicitly used North America / Asia-Pacific. The account region can differ from the energy site's physical location. The full gated update workflow passed with automatic region discovery, including read-only Fleet tests and installed-driver health checks.

## Visual Studio processor workflow

The solution includes [TeslaPowerwallCrestronDriver.WorkflowTests](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/TeslaPowerwallCrestronDriver.WorkflowTests/README.md), using the published Crestron Home Test Adapter. It exposes the complete gated workflow in Test Explorer while the ordinary NUnit fixtures remain available for local testing. Configure its private settings before execution; hosted CI verifies discovery without accessing hardware.

## Publishing when local hardware is unavailable

The publish/release workflows support an explicit manual override when the processor or local self-hosted GitHub Actions runner is unavailable. Select `skip_hardware_checks` and provide a single-line `hardware_skip_reason`. Use the workflow's normal source and version controls. The override applies only to that invocation and is recorded with the exact source revision in its warning and job summary; it does not create a passing hardware-test result.

GitHub-hosted validation remains mandatory for the checked-out source, and the normal build, tests and packaging steps still run. Wait for the configured hosted workflows to pass, or run them on the same source revision first. None of these hosted checks needs the local runner or processor. Automatic tag/release-triggered runs retain the normal hardware checks; use a manual invocation of the updated release workflow when an offline override is needed.
## NUnit 5 test tooling

All maintained suites use NUnit 5.0.0 and NUnit3TestAdapter 6.3.0. Workflow and Android tests use CrestronHomeNUnit.TestAdapter 2.3.0; processor packages use the pinned public SDK 2.3.0 source. See the [test dependency and live-input guide](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/TeslaPowerwallCrestronDriver.Tests/README.md).

The release includes `TeslaPowerwallCrestronDriver.ProcessorTests.pkg`, its documentation, exact source revisions and checksums. Automatic suites contain 87 unit and 29 lifecycle cases. Live suites are explicitly selected and require private inputs; they never change power settings. The desktop SDK harness additionally tests Windows provisioning. See [processor instructions](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/blob/master/TeslaPowerwallCrestronDriver.ProcessorTests/README.md) for installation and execution.
