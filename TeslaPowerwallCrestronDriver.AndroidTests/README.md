# Android UI tests

These opt-in NUnit 5 fixtures use NUnit3TestAdapter 6.3.0 and the Android helpers bundled in CrestronHomeNUnit.TestAdapter 2.3.0. They run on the Windows test computer against an existing Google Android emulator. They do not install or start an emulator. Mac testing is currently on hold.

Run through the published processor workflow's Android stage or its installed-driver test phase. The coordinator must own both reservations and verify the exact installed candidate package. Ordinary `dotnet test` discovery skips the three live UI cases without connecting.

Select a separately configured, read-only local instance without cloud history. Set `TESLA_ANDROID_EXPECTATIONS` to a private JSON file containing `TileName`, `MainPageTitle`, `ConnectionLabel`, `OperatingSettingsVisible` and `GridSettingsVisible`. Obtain those values from the selected driver's current published state. The private Android profile selects the emulator, expected Home and shared reservation path. Do not use a separate lock path to evade another emulator job.

The cases verify the saved processor endpoint, rendered power/battery readings, hidden cloud history, protocol/endpoint/polling details, capability-dependent settings and disabled power controls. Only the tile, Settings and Close navigation controls are tapped. Each target is selected from a fresh, checked hierarchy. Uncertain inputs are not repeated. Navigation returns to the same Home after each case and before reporting completion.

Private evidence includes masked UI hierarchies, screenshots, NUnit results and the coordinator's package/source receipts. Rendered acceptance is separate from the driver entity/UI-binding tests and from the Configure application's installer screens. Do not report visual acceptance solely from a successful build or offline discovery.

On 2026-10-08, all three cases passed against the separate read-only signed-LAN candidate on .241, using HPNeil. The coordinator confirmed the installed package, restored Home and released both reservations. Live-reading text comparisons accommodate Crestron's capitalization; disabled selectors are checked for the absence of interaction, while reserve and toggle controls must be disabled in the captured hierarchy. No power-setting control is tapped.
