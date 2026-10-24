# TeslaPowerwallCrestronDriver 2.0.7

This patch updates TeslaPowerwallLibrary to 2.3.1 and includes the local polling reliability fixes developed since 2.0.0.

## Changes

- Publish Offline feedback after a failed required API observation and recover only after a fresh successful observation and data poll.
- Reuse the required battery read for availability instead of sending recurring TCP probes. Keep data polls and state-changing commands serialized.
- Apply the 15-second timeout to each API request, allowing individually valid sequential requests to take longer in total. Back off repeated local failures and restore the configured interval after recovery.
- Pass driver-lifetime cancellation into control requests and reject results from superseded clients.
- Record the failed polling stage, elapsed time and cancellation origin without additional requests.
- Hide the redundant Refresh Now button for local connections. The programmable refresh command remains available.
- Fix signed LAN requests expiring during slower processor RSA signing through the library's finite 60-second signature window. The HTTP timeout stays 15 seconds.
- Inherit the library's wired/Wi-Fi remote-meter mapping fixes. Newly available maximum-power ratings and PW2 vitals are not added to the driver's UI or programmable interface.

## Installation and upgrade

Use the [Crestron Home Driver Feed Installer](https://github.com/oznetmaster/Crestron-Home-Driver-Feed-Installer), or download `TeslaPowerwallCrestronDriver.pkg` from this release and update the existing instance through Crestron Home Setup. Confirm version **2.0.007.0000**, the configured site and live readings. The package does not require a configuration-schema migration from 2.0.0. Installations older than 2.0.0 must follow the major-version reconfiguration instructions in the README.

The NuGet package `CrestronHomeDriver.Tesla.Powerwall` is a distribution wrapper for the final `.pkg`, with the community `crestron-driver-package.json` manifest; it is not a DLL-reference package or an official Crestron publishing specification. GitHub's automatic source archives are not installable driver packages.

Cloud/history cadence and existing commands, properties and events remain compatible. The local polling interval is not a universal processor-resource or physical-loss-detection guarantee. See the development history for validation evidence and limitations.
