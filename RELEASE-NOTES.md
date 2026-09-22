# TeslaPowerwallCrestronDriver 1.1.8

This patch release updates the driver to the published TeslaPowerwallLibrary 2.0.0 NuGet package.

- Correct mode-only and reserve-only operation writes, including numeric zero reserve, through the updated library.
- Route library diagnostics through the owning driver's Crestron logger, severity filters and scopes.
- Package System.Text.Json and Microsoft logging dependencies in place of the library's Newtonsoft.Json and log4net dependencies.
- Preserve existing configuration, programming commands, properties and UI.

## Validation

The driver was restored from NuGet into a fresh package cache, rebuilt and retested after library 2.0.0 became publicly available. All 81 offline cases passed in the desktop SDK harness. All 81 also passed twice on the development processor in each of the separate Owner and Fleet runs. All three live tests passed on desktop and processor with each API. Framework desktop validation passed 61 cases and intentionally skipped 20 lifecycle cases subsequently passed on the processor.

Live checks are read-only and cover site selection, ready state, battery and operating-state publication, and refresh. Temporary test instances and package archives were removed and processor reservations released. No installed driver update or Powerwall setting change was performed during validation.

The included processor test package contains 84 cases: 61 unit, 20 lifecycle and three manual live tests. Supply dedicated Owner and Fleet test sessions separately when running its live suite.
