# TeslaPowerwallCrestronDriver 1.2.1

This patch removes an incorrectly exposed connection option. The driver connects locally over the home network using Ethernet or normal Powerwall Wi-Fi, through Gateway or signed Powerwall 3 access.

- Remove the setup Wi-Fi choice from Configure and reject it in configuration and diagnostic inputs.
- Correct the README, configuration descriptions and provisioning instructions.
- Preserve Owner, Fleet, Gateway and Signed LAN configurations, polling intervals, controls and optional cloud history.

Import the updated `.pkg`, then apply the driver update in Crestron Home Setup and confirm version **1.2.001.0000**. Existing supported connections need no credential changes or key enrollment. An installation configured with the removed option must be reconfigured for a supported connection.

The release includes the driver package, Windows provisioning tool and separate processor test assets. Regression coverage checks the actual installer choices and rejection of unsupported modes on Windows and the processor. No Powerwall setting changes are needed for this correction.
