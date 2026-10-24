# Changelog

All notable changes to this project are documented in this file.

This changelog records shipped features, fixes, compatibility and runtime dependency changes. See [development and validation history](DEVELOPMENT-HISTORY.md) for tests, CI, build tooling and work not yet released.

## 2.0.7 — 2026-10-24

- Update TeslaPowerwallLibrary to 2.3.1, including the signed-request expiry correction for slower RSA signing and inherited wired/Wi-Fi meter mapping fixes. Maximum-power ratings and additional PW2 diagnostic readings remain library capabilities; this driver does not expose them.
- Hide the redundant Refresh Now button for local connections; retain the programmable refresh command.

- Record local poll failure stage, elapsed time and request/lifetime cancellation origin without adding requests.

- Publish explicit Offline feedback after a failed required API observation; recover only through a fresh successful API/data poll.
- Remove the separate recurring TCP connection probes. Reuse the required battery read and retain serialized polling and stateful commands.
- Retain the 15-second timeout for each API request instead of cancelling individually valid sequential reads after a shared 15-second whole-poll deadline. Back off repeated local failures and reset configured cadence on success.
- Pass driver lifetime cancellation into control requests and reject updates from superseded clients.
- Preserve configured cloud/history cadence and document that default polling is not a universal processor resource or 15-second physical-loss guarantee.

## 2.0.0 — 2026-10-08

- **Reconfiguration required:** update the existing Entity V2 driver in place, then complete its new configuration dialogs. Removing and re-adding the driver is not required. Configuration retention is not guaranteed; have settings and credentials ready to re-enter.
- Add Gateway and signed Powerwall 3 local access over Ethernet or home-network Wi-Fi using TeslaPowerwallLibrary 2.1.0.
- Add independent signing-key provisioning and configurable local polling (15 seconds by default), with local controls read-only by default.
- Allow separately configured cloud history alongside local live readings, while preserving Owner/Fleet connections and programmable interfaces.

## Withdrawn 1.2 series

The 1.2.0 publication is superseded by 2.0.0 to make the required reconfiguration explicit. The prepared 1.2.1 correction was not published.

## 1.1.8 — 2026-09-22

- Update TeslaPowerwallLibrary to 2.0.0, including fixes for partial operation changes, numeric zero reserve and stale settings caches.
- Route library diagnostics through the owning driver's Crestron logger and filters.
- Package System.Text.Json and Microsoft logging dependencies instead of Newtonsoft.Json and log4net. Preserve private resource helpers and serialized type metadata while merging assemblies.
- Existing configuration, commands, properties and UI remain unchanged.

## 1.1.7 — 2026-09-15

- Correct the driver tile to appear on the Home screen only, rather than both Home and room screens. Room assignment, configuration and public commands are unchanged.

## 1.1.6 — 2026-09-15

- Update TeslaPowerwallLibrary to 1.2.5, correcting Fleet token requests and automatically discovering the account's regional API endpoint. Existing Fleet configuration still requires only Client ID and refresh token.

## 1.1.5 — 2026-09-14

[Driver release notes](RELEASE-NOTES.md). Test-only changes do not require a driver release.

- Reject callbacks and refresh results from superseded clients before updating credentials, cached state or availability.

- Fix monthly history row boundaries using the wrong UTC offset across daylight-saving changes.

## 1.1.4 — 2026-09-07

### Fixed

- Updated the `TeslaPowerwallLibrary` dependency to `1.2.4`, which fixes a bug where setting the backup reserve percent via the Tesla Owner API could unintentionally overwrite the operation mode. Mode-only and reserve-only writes are now fully independent, matching the behavior already present for Tesla Fleet API connections.

- Updated the `Crestron.SimplSharp.SDK.Library` dependency to `2.22.15`.