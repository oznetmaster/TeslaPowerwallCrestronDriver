# Changelog

## 1.1.5 — 2026-09-14

[Draft driver release notes](RELEASE-NOTES.md). Test-only changes do not require a driver release.

- Cover Owner/Fleet token callbacks from current and replaced clients, and delayed successful/failed refreshes after configuration is cleared or the driver is disposed. Reject callbacks and refresh results from superseded clients before updating credentials, cached state or availability.

- Standardize driver versioning: Debug project/package metadata follows the manifest including its build increment; local Release builds preserve it; three-part release tags select the exact CI release without another patch increment. Verify source and built package versions before publication.


- Expand driver coverage to 53 offline tests and 20 SDK entity/lifecycle tests, with a desktop SDK harness and the same lifecycle fixtures in the net472 processor package.

- Add 53 NUnit driver unit tests and a processor lifecycle suite in the existing solution.
- Add a standalone Utility processor test package with private Debug deployment settings.
- Fix monthly history row boundaries using the wrong UTC offset across daylight-saving changes.

All notable changes to this project are documented in this file.

## [1.1.4] - 2026-09-07

### Fixed

- Updated the `TeslaPowerwallLibrary` dependency to `1.2.4`, which fixes a bug where setting the backup reserve percent via the Tesla Owner API could unintentionally overwrite the operation mode. Mode-only and reserve-only writes are now fully independent, matching the behavior already present for Tesla Fleet API connections.
- Updated the `Crestron.SimplSharp.SDK.Library` dependency to `2.22.15`.