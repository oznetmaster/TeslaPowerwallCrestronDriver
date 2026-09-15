# TeslaPowerwallCrestronDriver 1.1.6

Patch release correcting Fleet authentication and regional routing through TeslaPowerwallLibrary 1.2.5. Existing driver settings and public commands remain unchanged.

- Discover the authenticated account's Fleet region automatically. Continue to configure the Fleet Client ID and refresh token; no region field is required.
- Use the corrected Fleet token endpoint and form-encoded refresh requests supplied by the updated library.
- Add opt-in read-only Owner/Fleet site tests using independent test authorizations, and coordinate Debug deployment with the shared processor lock.

The gated workflow passed 73 local tests, 73 processor tests and three read-only Fleet tests, then updated the actual driver and passed three installed-driver health checks. No processor reboot was required. Test-package updates run separately from the installed driver; updating the actual driver may briefly interrupt its service.

GitHub includes the actual driver package and a separate processor test package. The test package appears under **Utility** in Configure and supports standalone tiles and the Windows NUnit runner. Only the actual driver package is published to NuGet. Credentials and private deployment settings are excluded from source and release assets.

See [README](README.md), [changelog](CHANGELOG.md) and [processor test release notes](TeslaPowerwallCrestronDriver.ProcessorTests/RELEASE-NOTES.md).
