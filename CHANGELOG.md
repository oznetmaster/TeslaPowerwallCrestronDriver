# Changelog

All notable changes to this project are documented in this file.

This changelog records shipped features, fixes, compatibility and runtime dependency changes. See [development and validation history](DEVELOPMENT-HISTORY.md) for tests, CI, build tooling and work not yet released.

## 1.1.8 - 2026-09-22

- Update TeslaPowerwallLibrary to 2.0.0, including fixes for partial operation changes, numeric zero reserve and stale settings caches.
- Route library diagnostics through the owning driver's Crestron logger and filters.
- Package System.Text.Json and Microsoft logging dependencies instead of Newtonsoft.Json and log4net. Preserve private resource helpers and serialized type metadata while merging assemblies.
- Existing configuration, commands, properties and UI remain unchanged.

## 1.1.7 - 2026-09-15

- Correct the driver tile to appear on the Home screen only, rather than both Home and room screens. Room assignment, configuration and public commands are unchanged.

## [1.1.6] - 2026-09-15

- Update TeslaPowerwallLibrary to 1.2.5, correcting Fleet token requests and automatically discovering the account's regional API endpoint. Existing Fleet configuration still requires only Client ID and refresh token.

## 1.1.5 — 2026-09-14

[Driver release notes](RELEASE-NOTES.md). Test-only changes do not require a driver release.

- Reject callbacks and refresh results from superseded clients before updating credentials, cached state or availability.

- Fix monthly history row boundaries using the wrong UTC offset across daylight-saving changes.

## [1.1.4] - 2026-09-07

### Fixed

- Updated the `TeslaPowerwallLibrary` dependency to `1.2.4`, which fixes a bug where setting the backup reserve percent via the Tesla Owner API could unintentionally overwrite the operation mode. Mode-only and reserve-only writes are now fully independent, matching the behavior already present for Tesla Fleet API connections.

- Updated the `Crestron.SimplSharp.SDK.Library` dependency to `2.22.15`.