# Driver tests

Use NUnit 5.0.0 and NUnit3TestAdapter 6.3.0. The net472 project validates the production assembly; the .NET 10 lifecycle project compiles the same driver and fixtures against the desktop SDK. Processor execution uses the separate processor test package and the CrestronHomeNUnit adapter workflow.

## Read-only live matrix

Run Owner, Fleet, Gateway and signed LAN separately. Ordinary home-network Wi-Fi and Ethernet use the same local API; either IP address or a resolvable hostname may be supplied.

Use `LiveTestSettings.json` in NUnit `TestDataDirectory`, or set `TESLA_LIVE_TEST_DATA_DIRECTORY` on Windows. Set NUnit `EnableLiveTests=true`; the input normally retains `enabled: false`. An explicit false parameter always disables live tests. The filename and portable JSON fields are identical for the library, Windows driver and processor tests. Neither suite enrolls keys or sends power-control commands.

Owner/Fleet use the library's dedicated credential helper. Keep its session open across all Windows and processor runs; do not copy refresh tokens or Windows encrypted credential stores to the processor.

Local inputs use `mode: local`, `localProtocol: Gateway` or `Signed LAN`, `host`, `password`, optional `localRefreshIntervalSeconds` (default 15), and `localSigningKey` for signed access. The password is the customer password for both modes. `localSigningKey` contains an already registered RSA-4096 private key in XML form. Keep input files private and outside Git. Portable inputs can be copied unchanged in either direction.

The optional `localSigningKeyName` field can reuse an existing Windows CNG key without exporting or registering it. This is explicitly Windows-only and mutually exclusive with portable XML. It cannot authorize a processor connection. The test fixture borrows that key and injects a read-only client, like the connected-client entity tests in the model driver. Production provisioning continues to require portable credentials.

```powershell
dotnet test TeslaPowerwallCrestronDriver.Lifecycle.Tests -c Release --filter 'FullyQualifiedName~LiveSiteTests&TestCategory!=LocalLive' --settings PRIVATE-Live.runsettings
# Local API and UI-property checks:
dotnet test TeslaPowerwallCrestronDriver.Lifecycle.Tests -c Release --filter FullyQualifiedName~LiveSiteTests --settings PRIVATE-Live.runsettings
```

On the processor, select `live` for the three common checks, or `local-live` for all six local checks. Supply the same private input through the runner. The local suite checks observed power publication, subsequent polling, packaged tile/settings navigation, disabled write controls and cloud-only visibility. These are entity/UI-binding tests using the real driver and layout, not rendered touch-panel or emulator acceptance tests.

Gateway data is less complete than signed TEDAPI on Powerwall 3. Missing site names remain unavailable. Missing operating settings hide the corresponding controls; a valid zero reserve remains visible. Signed LAN and cloud runs require actual reserve and mode values. Local refresh tests wait past the configured cache interval.

Keep installed-driver Configure-screen and rendered UI acceptance separate. Test instances are temporary and do not replace the installed energy driver.

Rendered Android acceptance is implemented in [AndroidTests](../TeslaPowerwallCrestronDriver.AndroidTests/README.md), using the published TestAdapter 2.3.0 helpers. It is a separate, explicitly selected workflow stage. HPNeil is the preferred existing emulator host; the primary Windows computer can also run the fixtures. Mac testing is on hold.
