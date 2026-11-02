# TeslaPowerwallCrestronDriver 2.0.8

This patch prevents negative battery percentages at or below the raw 5% reserve and uses TeslaPowerwallLibrary 2.5.0 for battery validation and scaling.

## Changes

- Consume the library's typed `GetBatteryLevelAsync(scale: true, force: true)` result for local availability. Remove direct battery-response JSON parsing and duplicated scaling from the driver.
- Retain one required fresh battery observation, shared transport caches, cancellation and offline recovery behavior. Missing or invalid fresh readings cannot restore Online feedback; numeric zero remains a genuine reading and displays 0%.
- Inherit the shared 0% reserve floor for cloud readings through the compatible legacy library API.
- Update the companion Windows local provisioning tool to the same library version. The library's PW2 instantaneous power limits remain unexposed by this driver; no battery dispatch controls are added.

## Installation and upgrade

Use the [Crestron Home Driver Feed Installer](https://github.com/oznetmaster/Crestron-Home-Driver-Feed-Installer), or download `TeslaPowerwallCrestronDriver.pkg` from this release and update the existing instance through Crestron Home Setup. Confirm version **2.0.008.0000**. UI, commands and configuration remain unchanged from 2.0.7; no remove/re-add or configuration migration is required. Installations older than 2.0.0 must follow the major-version reconfiguration instructions in the README.

The NuGet package `CrestronHomeDriver.Tesla.Powerwall` wraps the identical final `.pkg`; it is not a DLL-reference package or an official Crestron publishing specification. GitHub's automatic source archives are not installable driver packages. Crestron's catalog version may differ from this public GitHub/NuGet release.
