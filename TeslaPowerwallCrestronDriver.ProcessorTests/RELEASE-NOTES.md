# TeslaPowerwallCrestronDriver Tests

## 1.0.0 — 2026-09-14

- 53 offline tests and 20 SDK lifecycle tests, shared between desktop validation and the net472 processor package.
- Cover Owner/Fleet token callbacks from current and replaced clients, and delayed successful/failed refreshes after configuration is cleared or the driver is disposed. Reject callbacks and refresh results from superseded clients before updating credentials, cached state or availability.
- Install the standalone test package from Configure’s **Utility** category. Select suites using its Home tile or the Windows NUnit runner.
- Processor test packages are GitHub release assets and are not published to NuGet. Private test inputs and deployment settings are excluded.
