# TeslaPowerwallCrestronDriver v1.1.7

Patch release correcting tile placement in the Crestron Home app.

- Display the driver tile on the Home screen only, instead of both Home and room screens.
- Preserve the driver's room assignment, configuration, authentication and control APIs.

Validation: all 73 existing offline and desktop SDK fixture cases passed. The UI definition parses successfully and explicitly enables Home placement while disabling room placement.

Update the existing driver instance with this package. No reconfiguration is required.
