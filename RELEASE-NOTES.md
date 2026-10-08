# TeslaPowerwallCrestronDriver 2.0.0

**Update in place, then reconfigure.** This Entity V2 driver can update the existing installed instance. After applying the update, reopen Configure and complete the new configuration dialogs, even when retaining Owner or Fleet cloud access. Do not remove and re-add the driver. The required reconfiguration is why this is a major release.

## New local access

- Connect through Gateway or signed Powerwall 3 access over Ethernet or normal home-network Wi-Fi, using TeslaPowerwallLibrary 2.1.0.
- Configure local polling from 15 to 3600 seconds, with a default of 15 seconds between completed polls.
- Local controls are read-only by default. Explicitly enable supported setting changes when required; unavailable device values remain unavailable.
- Optionally configure cloud history using separate credentials and the exact numeric ID of the local site. Local monitoring stays independent of cloud history.
- Use the included Windows provisioning tool to enroll an independent signing key. Normal local operation needs neither that tool nor a cloud connection.

## Upgrade instructions

1. Import `TeslaPowerwallCrestronDriver.pkg`, then apply the update to the existing instance in Crestron Home Setup.
2. Reopen Configure and complete the new dialogs. Choose **Cloud** for Owner/Fleet, or **Gateway**/**Signed LAN** for a local connection. Review the credentials, site and polling intervals. Set local control permission and optional cloud history explicitly when using local access.
3. Save and confirm version **2.0.000.0000**, the room assignment, online status and live readings. Check any programmed commands/events before returning the installation to service.

Importing the package without updating the instance and completing configuration is not sufficient. Existing Owner/Fleet modes and programmable interfaces remain available. The 1.2.0 publication is superseded; the prepared 1.2.1 version was not published.

## Downloads and validation

The release includes the driver `.pkg`, Windows x64 provisioning tool with its runtime and documentation, and separate processor test assets with source identities and checksums. The NuGet distribution is `CrestronHomeDriver.Tesla.Powerwall`.

Offline suites cover the installer choices, unsupported-mode rejection, driver lifecycle and provisioning. Read-only live validation covered Owner, Fleet, Gateway and signed LAN; rendered Android checks covered the signed-LAN candidate. Gateway data is less complete than signed TEDAPI, and processor Gateway validation used an IP address rather than `.local` discovery. No Powerwall setting changes are needed for this update.
