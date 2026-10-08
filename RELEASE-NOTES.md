# TeslaPowerwallCrestronDriver 1.2.0

This minor release adds local Powerwall monitoring and configuration using the published TeslaPowerwallLibrary 2.1.0. Existing Owner and Fleet installations retain their cloud configuration and programmable interfaces.

- Select Gateway or signed Powerwall 3 local access using an IP address or a hostname resolvable by the processor. Signed access works over Ethernet or normal home-network Wi-Fi.
- Configure local polling from 15 to 3600 seconds, with a default of 15 seconds between completed polls. Cloud history has a separate interval.
- Local connections start read-only. Explicitly enable supported setting changes when required; unavailable settings are hidden and missing readings remain unavailable.
- Enable cloud history separately with its own credentials and the exact numeric ID of the local site. Local monitoring continues independently of cloud history.
- Use the included Windows local provisioning tool to prepare and enroll an independent signing key. Normal local operation does not need that tool or a cloud connection.

## Installation and upgrade

Download `TeslaPowerwallCrestronDriver.pkg`, or use the `CrestronHomeDriver.Tesla.Powerwall` NuGet distribution through the Driver Feed Installer. Import the package, then apply the available update to an existing instance in Crestron Home Setup. Import alone does not update an installed instance. Confirm version **1.2.000.0000** and retained configuration. See the [installation and configuration guide](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver#installation).

For new signed local connections, download `TeslaPowerwallLocalSetup-win-x64.zip` and follow its README. Existing cloud installations need no key enrollment. The Windows tool includes its runtime. Processor test assets are separate from the installable energy driver.

## Validation

Offline driver, configuration, provisioning and processor tests passed. Read-only live checks passed separately for Owner, Fleet, Gateway and signed LAN. Signed LAN was exercised on both test processors and both Windows test computers with portable credentials. Three Android NUnit checks passed against a separately installed read-only candidate, covering live readings, settings and saved connection details.

Gateway telemetry is less complete than signed TEDAPI. Processor Gateway validation used an IP address; `.local` name resolution was not established. Temporary setup Wi-Fi and Mac UI acceptance are outside this release's live validation. No Powerwall control changes were made during driver acceptance.
