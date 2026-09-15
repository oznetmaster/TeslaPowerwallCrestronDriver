# TeslaPowerwallCrestronDriver processor tests — driver release 1.1.6

## Unreleased

- Include 76 cases: 53 unit, 20 lifecycle and 3 optional live site tests. The package remains net472-only and uses Configure's Utility category.
- Live inputs support Owner and Fleet sessions prepared by the library's dedicated test credential helper. Installed drivers keep their independent credentials. The helper persists test refresh-token rotation on Windows; processor inputs contain an access token only. The three Owner live tests passed on Windows and the processor, and the full gated driver-update workflow passed. Three dedicated Fleet read-only tests have also passed on Windows and the processor. The automatic-region behavior in library 1.2.5 passed the full gated workflow: 73 local tests, 73 processor tests and three read-only Fleet live tests, followed by the actual driver update and three installed-driver health checks. No processor reboot was required. Local driver access remains pending.
- Use current processor-host locking for coordinated runner, CLI, tile and deployment operations.


## 1.0.0 — 2026-09-14

- 53 offline tests and 20 SDK lifecycle tests, shared between desktop validation and the net472 processor package.
- Cover Owner/Fleet token callbacks from current and replaced clients, and delayed successful/failed refreshes after configuration is cleared or the driver is disposed. Reject callbacks and refresh results from superseded clients before updating credentials, cached state or availability.
- Install the standalone test package from Configure’s **Utility** category. Select suites using its Home tile or the Windows NUnit runner.
- Processor test packages are GitHub release assets and are not published to NuGet. Private test inputs and deployment settings are excluded.
