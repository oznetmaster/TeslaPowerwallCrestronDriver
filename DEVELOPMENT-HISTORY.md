# Development and validation history

## Local connection correction and major release - 2.0.0

- Supersede the 1.2 series with 2.0.0: existing Entity V2 instances support an in-place update, followed by required reconfiguration. The 1.2.1 candidate was not published.
- Remove the mistakenly exposed setup-network option from installer choices and runtime validation. Local connections use Ethernet or home-network Wi-Fi.
- Add regressions for unsupported modes, the packaged installer choices, and portable diagnostic input validation.

## Local connection development - superseded 1.2.0

- Update to the published TeslaPowerwallLibrary 2.1.0 package.
- Add Gateway and signed Powerwall 3 LAN configuration, preserving existing cloud installations.
- Local polling is configurable from 15 to 3600 seconds (default 15). Cloud refresh remains separately configurable from 30 to 3600 seconds (default 60). Requests are serialized and the polling delay starts after completion.
- Make local controls read-only by default. Optional cloud history requires explicit enablement and the exact numeric site ID.
- Add independent driver key provisioning and portable Windows/processor local live-test inputs.
- Validation on 2026-10-08: 84 net472 offline checks, 124 .NET 10 desktop/provisioning checks, and 113 offline processor checks passed. Owner and Fleet each passed three live driver checks on .244; Gateway passed six. Signed LAN passed six on both .244 and .241. The same portable test input passed six checks on each Windows test computer, including HPNeil.
- Registered a new portable signing key with the owner's approval and confirmed Tesla's physical verification. The existing Windows CNG key was preserved. Normal local tests require neither cloud access nor key enrollment.
- Added an independent read-only candidate on .241 and verified live signed-LAN data with controls disabled and a 15-second configured interval. The existing energy driver on .244 was not changed. Installer SDK validation requires a value for every numeric field; the provisioning export now includes both local and cloud polling defaults.
- The companion library's focused processor checks passed in all four connection modes: eight Windows contract cases, four processor contract cases and three live reads per mode. A broader offline processor run was cancelled at its ten-minute deadline after 189 passing checks and zero failures; that partial run is not full regression acceptance.
- All three read-only Android NUnit checks passed on HPNeil using the published TestAdapter 2.3.0 and the existing Google Android emulator: live readings and hidden cloud history, connection details and disabled controls, and the saved processor endpoint. Home restoration, candidate verification and reservation cleanup passed. Configure-screen visual acceptance is separate; Mac testing remains on hold.
- Android validation first exposed incorrect fixture expectations for Crestron's capitalization and disabled selector rendering, a mismatched HPNeil package-cache binary, and a confirmed Android system-not-responding dialog. Used an isolated cache verified against a fresh NuGet download, corrected assertions against captured UI evidence, and restarted only the existing headless emulator. The successful final run retained the original failed-run evidence.
- Processor Gateway access passed using the home-network IP address. The `.local` hostname attempt failed on the processor; successful Windows resolution does not establish processor DNS/mDNS support.
- Release packaging uses the public CrestronHomeNUnit 2.3.0 source revision and NuGet ManifestUtil 29.0.10. The release checks validate the processor inventory separately from Windows-only provisioning cases.

See the [product changelog](CHANGELOG.md) for shipped changes. This document preserves test, CI and build history. Dated development entries describe work at that time, not a published product version or completed acceptance. Version headings identify the release alongside which development work was recorded; processor-test versions identify separate test packages.

## Where changes belong

- Product changelog and product release notes: shipped behavior, API, compatibility, fixes and runtime dependencies. Mention validation briefly when it helps explain a fix.
- This history: test coverage, CI, build tooling, test-package releases and work on pending candidates. Split mixed entries so the product effect remains easy to find.
- Testing and workflow guides: current setup and operating instructions.
- Test-only or documentation-only changes do not require a product release. Processor-test releases update this history, not the product changelog.

<!-- development-history -->

## Offline release workflow option - 2026-09-15 (no package release)

- Allow an explicit manual release when local hardware or the self-hosted runner is unavailable, with the reason and exact source recorded in the workflow summary.
- Keep hosted source validation mandatory and preserve all build, test and packaging steps. No runtime, API or package-version changes.

## CI package cleanup - 2026-09-15 (no driver or processor package release)

- Update Test Explorer workflow containers to CrestronHomeNUnit.TestAdapter 1.3.0 and document opt-in storage cleanup after successful CI runs.
- Retain original deployment filenames, protect pre-existing/manual packages and preserve failed-run evidence. Cleanup frees archive storage without rebooting; Home can retain cached catalogue entries until its next planned reboot.
- Compare executed test identities and packaged discovery against source discovery instead of duplicated count constants. Live suites remain discovery-only in hosted CI; only the documented processor-runtime skips are accepted on Windows.
- Actual driver/library code is unchanged; no driver release is required.

## CI validation - 2026-09-15 (no package release)

- Revalidate the current default-branch source after successful release workflows, including version commits created by GitHub Actions.
- Allow maintainers to configure exact-source, App-specific checks that must pass before publishing through `RELEASE_REQUIRED_CHECKS`; missing, failed or unconfirmed checks block the release.

## TeslaPowerwallCrestronDriver.ProcessorTests v1.1.7 - 2026-09-15

Published processor test package on GitHub. This is a test-package release only; no driver or library NuGet package is published. See the matching package release notes for changes and validation.

## 2026-09-15 - Test and development tooling (no driver release)

- Add the published Test Explorer workflow adapter, offline discovery CI and independent GitHub processor-test releases. Private workflow plans control optional live tests, actual-driver updates and temporary-instance cleanup.

## [1.1.6] - 2026-09-15

- Add three optional read-only live site tests with independent Owner/Fleet test credentials. Keep refresh-token ownership on Windows; processor inputs contain only a short-lived access token. Local gateway live testing remains future work.

- Keep lifecycle tests in the ordinary processor workflow and coordinate Debug deployment through the shared DevTools processor lock.

- Validate 73 local tests, 73 processor tests and three read-only Fleet tests before updating the actual driver; all three post-update driver health checks passed without a reboot.

- Include the standalone processor test package as a GitHub release asset under Utility in Configure. It is not published to NuGet.

## 1.1.5 — 2026-09-14

- Cover Owner/Fleet token callbacks from current and replaced clients, and delayed successful/failed refreshes after configuration is cleared or the driver is disposed. Reject callbacks and refresh results from superseded clients before updating credentials, cached state or availability.

- Standardize driver versioning: Debug project/package metadata follows the manifest including its build increment; local Release builds preserve it; three-part release tags select the exact CI release without another patch increment. Verify source and built package versions before publication.

- Expand driver coverage to 53 offline tests and 20 SDK entity/lifecycle tests, with a desktop SDK harness and the same lifecycle fixtures in the net472 processor package.

- Add 53 NUnit driver unit tests and a processor lifecycle suite in the existing solution.

- Add a standalone Utility processor test package with private Debug deployment settings.