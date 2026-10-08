# Reconfiguration investigation

These post-2.0.0 experiments characterize the Crestron SDK configuration state machine. They do not change the released driver or promise that Crestron Home retains configuration through an update.

## Method

`ConfigurationReconfigurationTests` loads the actual driver manifest into `DelegateDataDrivenConfigurationController`. Its callback records values and actions but never creates a Powerwall client. All credentials are synthetic. The fixture uses the same NUnit source on the desktop SDK harness and the processor test package.

The tests call the public SDK entry points: `ApplyConfiguration`, `GetAllConfigurationValues`, `GetFirstConfigurationStep` and `ApplyConfigurationStep`. They do not infer SDK behavior from calls to the driver's private configuration callback.

## Findings

| Scenario | SDK result |
| --- | --- |
| Restore the previous four cloud fields, with Owner or Fleet selected | Existing fields remain available; newly introduced defaults are not automatically persisted. |
| Export and restore those fields into a fresh controller | The supplied values survive the round trip. |
| Apply only a changed cloud polling interval | The omitted token is retained. |
| Explicitly submit a blank token | The SDK stores the blank. Driver-level credential validation is separate. |
| Restart the first configuration step | Stored values are cleared and the SDK invokes `ClearValues`. The masked token is absent, not just visually hidden. |
| Restart with local credentials and permissions present | The same reset clears those fields for Cloud, Gateway and Signed LAN configurations. |
| Copy a snapshot before restarting, then explicitly combine it with the new form defaults | The complete form can be applied successfully for synthetic Owner and Fleet configurations. |

The driver currently responds to `ClearValues` by stopping its refresh loop and clearing active and pending configuration. The SDK reset therefore provides a concrete explanation for why reconfiguration can lose values; it does not establish which calls every Crestron Home version makes during an in-place update.

## Possible later improvement

Investigate an in-memory snapshot or configuration-controller wrapper that can prepopulate appropriate fields before the SDK resets them. A complete implementation must distinguish restarting configuration from an intentional clear, respect blank credentials and Owner/Fleet application changes, use the latest rotated token, and require explicit handling of local-control permission. These experiments do not add credential persistence or automatic restoration to the driver.

Before shipping such a change, use isolated Crestron Home instances to test an actual update, reopening and cancellation, successful save, missing credentials, connection changes and processor restart. Keep the 2.0.0 reconfiguration caveat until that behavior is demonstrated end to end. No installed energy-driver configuration or Powerwall setting was changed by these experiments.