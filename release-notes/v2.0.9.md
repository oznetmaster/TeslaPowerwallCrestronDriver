# TeslaPowerwallCrestronDriver 2.0.9

This patch corrects the dependency and lifecycle issues described below.

- Use TeslaPowerwallLibrary 2.5.1 in the driver and local provisioning tool.
- Close connection admission and drain refresh/command work before retiring clients and their owned RSA signing key.
- Reject retired token callbacks and post-shutdown client recreation; preserve existing connection settings and stored credentials.

## Installation

Use the driver package from [GitHub](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/releases/tag/v2.0.9) or the matching NuGet distribution. Import the package, then update the existing driver in Home Setup or Configure Pro. Importing alone does not update the running instance. Existing pairing and configuration formats are retained; removal and re-add is not a routine update step.

## Validation

Passed 303 workstation Debug and 303 Release test executions, plus 195 native net472/Mono processor tests. Processor validation used fake transports and synthetic credentials; no new physical-device acceptance or endurance run is claimed. Existing processor configuration and all pairing files were preserved. See [development history](DEVELOPMENT-HISTORY.md) for provenance and limits.

This GitHub/NuGet release does not replace any submitted Crestron package. Submission-specific PDFs and correspondence are excluded from public artifacts.
