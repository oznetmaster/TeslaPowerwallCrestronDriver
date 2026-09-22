# TeslaPowerwallCrestronDriver Tests

## 1.1.8

- Validate TeslaPowerwallLibrary 2.0.0 with System.Text.Json and caller-owned Microsoft ILogger logging.
- Include 84 discovered cases: 61 unit, 20 lifecycle and three manual live tests. The eight new unit cases validate severity mapping, filtering and isolated logging scopes.
- Run the live suite separately with dedicated Owner and Fleet credential-helper sessions. Both API configurations are required for local release validation; refresh tokens remain on Windows with the helper.
- Exclude desktop runner dependencies and their Newtonsoft.Json dependency from the processor package.

## 1.1.7

- Rebuild with CrestronHomeNUnit 1.2.1. Test execution now participates in the shared processor reservation used by the runner, Test Explorer, CLI and hardware CI.
- The net472 package contains 76 discovered cases, with 73 in automatic suites. Live suites remain optional and require private inputs where documented.
- Use the standalone Utility tile, Windows runner, or the solution's Test Explorer workflow project. Private workflow plans can remove the temporary instance after testing.
- This is an independent processor-test package release on GitHub; it does not publish or update a driver/library NuGet package.

- Include 76 cases: 53 unit, 20 lifecycle and 3 optional live site tests. The package remains net472-only and uses Configure's Utility category.
- Live inputs support Owner and Fleet sessions prepared by the library's dedicated test credential helper. Installed drivers keep their independent credentials. The helper persists test refresh-token rotation on Windows; processor inputs contain an access token only. The three Owner live tests passed on Windows and the processor, and the full gated driver-update workflow passed. Three dedicated Fleet read-only tests have also passed on Windows and the processor. The automatic-region behavior in library 1.2.5 passed the full gated workflow: 73 local tests, 73 processor tests and three read-only Fleet live tests, followed by the actual driver update and three installed-driver health checks. No processor reboot was required. Local driver access remains pending.
- Use current processor-host locking for coordinated runner, CLI, tile and deployment operations.


## 1.0.0 — 2026-09-14

- 53 offline tests and 20 SDK lifecycle tests, shared between desktop validation and the net472 processor package.
- Cover Owner/Fleet token callbacks from current and replaced clients, and delayed successful/failed refreshes after configuration is cleared or the driver is disposed. Reject callbacks and refresh results from superseded clients before updating credentials, cached state or availability.
- Install the standalone test package from Configure’s **Utility** category. Select suites using its Home tile or the Windows NUnit runner.
- Processor test packages are GitHub release assets and are not published to NuGet. Private test inputs and deployment settings are excluded.