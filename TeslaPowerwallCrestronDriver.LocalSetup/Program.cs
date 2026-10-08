// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeslaPowerwall.CrestronDriver.LocalSetup;
using TeslaPowerwallLibrary;
using TeslaPowerwallLibrary.Tedapi;

if (args.Length == 0 || args[0] == "--help")
	{
	Console.WriteLine ("Independent Crestron driver LAN provisioning (Windows)");
	Console.WriteLine ("init PROFILE PRIVATE_SEED.json : create an encrypted profile and independent key; no network access");
	Console.WriteLine ("init-local PROFILE PRIVATE_SEED.json : create an encrypted portable key without taking cloud refresh ownership");
	Console.WriteLine ("enroll-session/status-session PROFILE LiveTestSettings.json : use a prepared Owner/Fleet session for this exact site");
	Console.WriteLine ("enroll PROFILE : explicitly register this key through its Owner/Fleet account");
	Console.WriteLine ("status PROFILE : read this key's authorization state; does not register or control power");
	Console.WriteLine ("test-inputs PROFILE : write portable LiveTestSettings.json for Windows or processor diagnostics");
	Console.WriteLine ("export PROFILE : write verified local settings for Crestron Home; no cloud credential exported");
	return 0;
	}
try
	{
	if (args.Length < 2 || !Regex.IsMatch (args[1], "\\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,47}\\z")
		|| args[0] is not ("init" or "init-local" or "enroll" or "status" or "enroll-session" or "status-session" or "export" or "test-inputs")
		|| args.Length != (args[0] is "init" or "init-local" or "enroll-session" or "status-session" ? 3 : 2))
		{
		throw new ArgumentException ("Invalid arguments; use --help.");
		}
	string directory = Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.LocalApplicationData), "TeslaPowerwallCrestronDriver", "Provisioning", args[1]);
	Directory.CreateDirectory (directory);
	using var profileLock = new FileStream (Path.Combine (directory, "profile.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
	string path = Path.Combine (directory, "profile.dat");
	if (args[0] is "init" or "init-local")
		{
		if (File.Exists (path))
			{
			throw new InvalidOperationException ("This profile already exists. Its key and current credential will not be replaced.");
			}
		var seed = JsonSerializer.Deserialize<ProvisioningProfile> (File.ReadAllText (args[2]), ProvisioningProfile.JsonOptions) ?? throw new InvalidDataException ();
		seed.Validate (requireRefreshCredential: args[0] == "init");
		if (args[0] == "init-local") seed.RefreshToken = "";
		using RSA generated = RSA.Create (4096);
		seed.SigningKey = generated.ToXmlString (true);
		seed.State = LocalKeyState.Unknown;
		seed.Fingerprint = "";
		seed.AuthenticationPending = false;
		seed.EnrollmentPending = false;
		seed.Save (path);
		Console.WriteLine ("Independent driver key and setup credential saved with Windows user encryption. No key was registered. Remove the private seed when no longer needed.");
		return 0;
		}
	ProvisioningProfile profile = ProvisioningProfile.Load (path);
	bool preparedSession = args[0].EndsWith ("-session", StringComparison.Ordinal);
	bool enroll = args[0] is "enroll" or "enroll-session";
	profile.Validate (requireRefreshCredential: !preparedSession && args[0] is "enroll" or "status");
	if (args[0] == "test-inputs")
		{
		_ = profile.DriverValues ();
		string output = Path.Combine (directory, "LiveTestSettings.json");
		File.WriteAllText (output, JsonSerializer.Serialize (new
			{
			enabled = false, mode = "local", localProtocol = "Signed LAN", host = profile.Host,
			password = profile.Password, localSigningKey = profile.SigningKey, localRefreshIntervalSeconds = 15
			}, ProvisioningProfile.JsonOptions));
		Console.WriteLine ("Private Windows/processor test inputs: " + output);
		return 0;
		}
	if (args[0] == "export")
		{
		string output = Path.Combine (directory, "DriverConfiguration.json");
		File.WriteAllText (output, JsonSerializer.Serialize (profile.DriverValues (), ProvisioningProfile.JsonOptions));
		Console.WriteLine ("Private installer values: " + output);
		Console.WriteLine ("Contains a private key and password. Enter these values in Configure, then remove the transfer file when no longer needed.");
		return 0;
		}
	if (profile.AuthenticationPending)
		{
		throw new InvalidOperationException ("An earlier authentication did not complete. Investigate before reusing its refresh credential; no automatic retry was made.");
		}
	if (enroll && (profile.EnrollmentPending || profile.State == LocalKeyState.Verified))
		{
			throw new InvalidOperationException ("Check the existing key status before repeating an uncertain enrollment; verified keys need no enrollment.");
			}
	using RSA key = profile.OpenKey ();
	bool fleet = profile.Mode == "fleet";
	PowerwallOptions options = preparedSession
		? (JsonSerializer.Deserialize<PreparedCloudSession> (File.ReadAllText (args[2])) ?? throw new InvalidDataException ()).Options (profile.SiteId)
		: new PowerwallOptions
		{
		CloudMode = !fleet, FleetApi = fleet, SiteId = profile.SiteId,
		RefreshToken = fleet ? null : profile.RefreshToken,
		FleetApiRefreshToken = fleet ? profile.RefreshToken : null, FleetApiClientId = profile.ClientId,
		FleetApiRegion = "auto", NoCloudTokenPersistence = true, NoFleetApiTokenPersistence = true,
		Timeout = TimeSpan.FromSeconds (25)
		};
	using var client = new Powerwall (options);
	void SaveToken (string? token)
		{
		if (!string.IsNullOrWhiteSpace (token))
			{
			profile.RefreshToken = token;
			profile.Save (path);
			}
		}
	client.CloudTokensRefreshed += (_, tokens) => SaveToken (tokens.RefreshToken);
	client.FleetApiTokensRefreshed += (_, tokens) => SaveToken (tokens.RefreshToken);
	profile.AuthenticationPending = !preparedSession;
	profile.Save (path);
	using var deadline = new CancellationTokenSource (TimeSpan.FromMinutes (2));
	if (!await client.ConnectAsync (deadline.Token) || (options.FleetApi ? client.FleetApiSiteId : client.CloudSiteId) != profile.SiteId)
		{
		throw new InvalidOperationException ("Unable to authenticate to the exact configured site.");
		}
	profile.AuthenticationPending = false;
	profile.Save (path);
	if (enroll)
		{
		profile.EnrollmentPending = true;
		profile.Save (path);
		}
	// No physical verification or power command is initiated by this tool.
	LocalKeyRegistration result = enroll
		? await client.RegisterLocalKeyAsync (key, "Crestron Home Powerwall driver", deadline.Token)
		: await client.GetLocalKeyStatusAsync (key, deadline.Token);
	profile.EnrollmentPending = false;
	profile.State = result.State;
	profile.Fingerprint = result.Fingerprint;
	profile.Save (path);
	Console.WriteLine ("Key " + result.Fingerprint + ": " + result.State);
	if (result.State == LocalKeyState.PendingVerification)
		{
		Console.WriteLine ("Tesla requires physical verification. Follow Tesla's instructions, then run status. See the local provisioning guide; the tool never switches household power.");
		}
	return 0;
	}
catch (Exception exception)
	{
	// Server responses and private paths may contain credentials. Never print exception text.
	Console.Error.WriteLine ("Provisioning did not complete (" + exception.GetType ().Name + "). Check the private profile and guide. Credentials were not printed; no automatic retry was made.");
	return 1;
	}
