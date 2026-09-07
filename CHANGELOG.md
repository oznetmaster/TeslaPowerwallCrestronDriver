# Changelog

All notable changes to this project are documented in this file.

## [1.1.4] - 2026-09-07

### Fixed

- Updated the `TeslaPowerwallLibrary` dependency to `1.2.4`, which fixes a bug where setting the backup reserve percent via the Tesla Owner API could unintentionally overwrite the operation mode. Mode-only and reserve-only writes are now fully independent, matching the behavior already present for Tesla Fleet API connections.
- Updated the `Crestron.SimplSharp.SDK.Library` dependency to `2.22.15`.
