# Independent local driver provisioning

This Windows tool prepares an independent Powerwall 3 signing key for the installed Crestron Home driver. It does not depend on the Windows dashboard, test credential helper, or their signing keys. Nothing is enrolled automatically by driver startup, configuration validation, or polling.

Gateway connections need only the local host and customer password. Both local protocols connect over the home network using Ethernet or normal Powerwall Wi-Fi. Signed LAN requires the local customer password **and** a verified RSA-4096 key.

## Windows download

Download `TeslaPowerwallLocalSetup-win-x64.zip` from the matching [driver release](https://github.com/oznetmaster/TeslaPowerwallCrestronDriver/releases/latest), extract it into a private working folder, and open PowerShell there. Run `./TeslaPowerwallCrestronDriver.LocalSetup.exe --help`. The Windows x64 download includes its .NET runtime.

Use `./TeslaPowerwallCrestronDriver.LocalSetup.exe` followed by the same command arguments shown below. Developers building from source can use the `dotnet run --project ... --` form instead.

## Prepare signed LAN access

1. Use the [TeslaPowerwallLibrary setup tool](https://github.com/oznetmaster/TeslaPowerwallLibrary/tree/master/TeslaPowerwallLibrary.Setup) to obtain a separately issued Owner or Fleet refresh credential for this provisioning profile. Do not reuse an app, installed driver, or test helper's rotating refresh token.
2. Save a private seed outside the repository with the fields below. `siteId` must be the numeric ID of the exact physical Powerwall site. The local `password` is the customer password, not the Tesla account password.
3. Run `init` once to create the independent key and encrypted profile. No network request is made. Remove the plaintext seed after successful initialization.
4. When ready for any physical verification required by Tesla, run `enroll` explicitly. Only the public key is registered. Follow Tesla's applicable verification instructions; this tool never initiates physical verification, disconnects the grid, or switches household power.
5. Run `status` to check this specific key. Pending or unknown is not verified. After an uncertain enrollment outcome, use `status` before another `enroll` attempt. An interrupted OAuth exchange is held for investigation rather than silently reusing an uncertain token.
6. Once status reports `Verified`, run `export`. Enter the exported values in the driver's Configure fields. Paste the entire `LocalSigningKey` string as one value. It is a masked persistent credential. Local controls default to disabled and local polling defaults to 15 seconds.

```json
{
  "mode": "cloud",
  "clientId": "",
  "refreshToken": "INDEPENDENT_SETUP_REFRESH_TOKEN",
  "siteId": "1234567890",
  "host": "powerwall.example.invalid",
  "password": "LOCAL_CUSTOMER_PASSWORD"
}
```

For Fleet use `mode: "fleet"` and its application `clientId`. Fleet regional discovery is automatic.

```powershell
dotnet run --project TeslaPowerwallCrestronDriver.LocalSetup -- init garden C:\Private\PowerwallSeed.json
dotnet run --project TeslaPowerwallCrestronDriver.LocalSetup -- enroll garden
dotnet run --project TeslaPowerwallCrestronDriver.LocalSetup -- status garden
dotnet run --project TeslaPowerwallCrestronDriver.LocalSetup -- export garden
```

Profiles are encrypted for the current Windows user under `%LOCALAPPDATA%/TeslaPowerwallCrestronDriver/Provisioning/PROFILE/profile.dat`. Commands take an exclusive profile lock. Existing profiles and keys are never replaced by `init`. Rotated setup credentials are saved synchronously. Keep the profile for future explicit status checks; the installed driver does not need this tool running.

`DriverConfiguration.json` is an installation transfer file containing a private key and local password. It contains no cloud refresh token. Keep it private and remove it when no longer needed; never put it in source control, logs, test results or release assets. The driver uses Crestron Home's persistent configuration storage; masking is a UI protection, not a claim about encryption by Crestron Home.

## Optional cloud history

Enable **Use Cloud History with Local Access** only if wanted. Configure an independently issued Owner/Fleet credential for the installed driver and the numeric ID of the same physical site. Do not transfer the setup profile's refresh token. The default cloud interval is 60 seconds and can be changed separately from the local interval. The local connection never silently falls back to cloud data or controls.

## Shared Windows and processor diagnostic inputs

`LiveTestSettings.json` has the same schema on Windows and the processor. Transfer it in either direction without field or credential conversion. The runner preserves its filename and passes its private directory as `TestDataDirectory`. A file recovered from the processor can be supplied to the Windows fixture unchanged; cloud access tokens still have their normal expiry.

For an explicitly verified provisioning profile, this command creates a private local diagnostic input:

```powershell
dotnet run --project TeslaPowerwallCrestronDriver.LocalSetup -- test-inputs garden
```

The generated file contains `enabled`, `mode: "local"`, `localProtocol: "Signed LAN"`, `host`, `password`, `localSigningKey`, and `localRefreshIntervalSeconds`. Existing cloud/fleet fields retain their meanings. Windows and processor tests use the same typed contract. These tests force read-only access and never enroll keys or issue control commands. `enabled` defaults to false; select the Live suite explicitly through the runner or use the existing NUnit enablement parameter.

The encrypted Windows profile is intentionally not a portable file. The **test input JSON** is portable and contains real local credentials; keep both copies private, and clear transferred inputs when no longer required. Installed-driver provisioning and test execution remain independent.


## Portable test key using an existing credential-helper session

Use `init-local PROFILE PRIVATE_SEED.json` to prepare a local-only profile with `mode`, exact numeric `siteId`, `host` and customer `password`. It generates an RSA-4096 key, stores it encrypted on this Windows computer and does not take ownership of a refresh token. Then explicitly run `enroll-session PROFILE LiveTestSettings.json` using a prepared Owner/Fleet input from the existing credential helper. The input site must match the local profile exactly. The helper remains the sole refresh owner. Check authorization with `status-session PROFILE LiveTestSettings.json`; registration is never repeated automatically.

After Tesla confirms physical verification, `test-inputs PROFILE` writes the portable `LiveTestSettings.json` containing the registered key. Copy that file unchanged to another test computer or supply it to either processor's manual local suite. The key and password are secrets: keep the transfer file private, outside Git and release artifacts. The encrypted provisioning profile stays on its original Windows user account; it is not the portable file. Normal local test execution has no cloud dependency. Existing Windows CNG keys are not replaced or exported.

The installer export includes explicit values for both polling intervals (15 seconds local and 60 seconds cloud), even when cloud history is disabled. Crestron's configuration SDK validates numeric fields before the driver receives them, so an omitted numeric field can be interpreted as zero. Preserve these defaults when importing the export through configuration automation.
