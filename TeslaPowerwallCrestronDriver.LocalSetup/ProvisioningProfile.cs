// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeslaPowerwallLibrary;
using TeslaPowerwallLibrary.Tedapi;

namespace TeslaPowerwall.CrestronDriver.LocalSetup;

/// <summary>Private, encrypted installer authorization and the independent driver signing key.</summary>
internal sealed class ProvisioningProfile
	{
	/// <summary>Gets or sets the cloud API used only for explicit enrollment and status checks.</summary>
	public string Mode { get; set; } = "cloud";
	/// <summary>Gets or sets the Fleet application identifier.</summary>
	public string ClientId { get; set; } = "";
	/// <summary>Gets or sets the current refresh credential, owned exclusively by this setup profile.</summary>
	public string RefreshToken { get; set; } = "";
	/// <summary>Gets or sets the exact numeric energy-site identifier.</summary>
	public string SiteId { get; set; } = "";
	/// <summary>Gets or sets the local IP address or hostname.</summary>
	public string Host { get; set; } = "";
	/// <summary>Gets or sets the local customer password.</summary>
	public string Password { get; set; } = "";
	/// <summary>Gets or sets the private RSA-4096 key, never written to standard output.</summary>
	public string SigningKey { get; set; } = "";
	/// <summary>Gets or sets the last confirmed authorization state for this exact key.</summary>
	public LocalKeyState State { get; set; }
	/// <summary>Gets or sets the public-key fingerprint returned by Tesla.</summary>
	public string Fingerprint { get; set; } = "";
	/// <summary>Gets or sets whether an interrupted authentication requires investigation.</summary>
	public bool AuthenticationPending { get; set; }

	/// <summary>Gets or sets whether a registration outcome must be checked before another enrollment.</summary>
	public bool EnrollmentPending { get; set; }

	/// <summary>Gets the serializer configuration for private profile and transfer files.</summary>
	internal static JsonSerializerOptions JsonOptions { get; } = new () { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };

	/// <summary>Checks the exact cloud site and independent local endpoint before authentication.</summary>
	/// <param name="requireRefreshCredential">Whether this profile must own a cloud refresh credential.</param>
	internal void Validate (bool requireRefreshCredential = true)
		{
		if (Mode is not ("cloud" or "fleet") || (Mode == "fleet" && string.IsNullOrWhiteSpace (ClientId))
			|| (requireRefreshCredential && string.IsNullOrWhiteSpace (RefreshToken)) || string.IsNullOrEmpty (SiteId) || !SiteId.All (char.IsAsciiDigit)
			|| string.IsNullOrWhiteSpace (Password) || string.IsNullOrWhiteSpace (Host))
			{
			throw new InvalidDataException ("Specify cloud/fleet, an independently issued refresh token, numeric site ID, local host and customer password; Fleet also requires its client ID.");
			}
		}

	/// <summary>Saves the private profile atomically using current-user Windows encryption.</summary>
	/// <param name="path">Private profile path.</param>
	[SupportedOSPlatform ("windows")]
	internal void Save (string path)
		{
		byte[] clear = JsonSerializer.SerializeToUtf8Bytes (this, JsonOptions);
		try
			{
			byte[] encrypted = ProtectedData.Protect (clear, null, DataProtectionScope.CurrentUser);
			string temporary = path + ".tmp";
			File.WriteAllBytes (temporary, encrypted);
			if (File.Exists (path))
				{
				File.Replace (temporary, path, null);
				}
			else
				{
				File.Move (temporary, path);
				}
			}
		finally
			{
			CryptographicOperations.ZeroMemory (clear);
			}
		}

	/// <summary>Reads an encrypted profile for this Windows user.</summary>
	/// <param name="path">Private profile path.</param>
	/// <returns>The decrypted profile.</returns>
	[SupportedOSPlatform ("windows")]
	internal static ProvisioningProfile Load (string path)
		{
		byte[] clear = ProtectedData.Unprotect (File.ReadAllBytes (path), null, DataProtectionScope.CurrentUser);
		try
			{
			return JsonSerializer.Deserialize<ProvisioningProfile> (clear, JsonOptions) ?? throw new InvalidDataException ();
			}
		finally
			{
			CryptographicOperations.ZeroMemory (clear);
			}
		}

	/// <summary>Opens the independent signing key; never creates or replaces one.</summary>
	/// <returns>A caller-owned RSA private key.</returns>
	internal RSA OpenKey ()
		{
		RSA key = RSA.Create ();
		try
			{
			key.FromXmlString (SigningKey);
			if (key.KeySize != 4096 || key.ExportParameters (true).D is null)
				{
				throw new CryptographicException ("A private RSA-4096 key is required.");
				}
			return key;
			}
		catch
			{
			key.Dispose ();
			throw;
			}
		}

	/// <summary>Exports only local installer fields after explicit confirmation of key authorization.</summary>
	/// <returns>Private values to enter in Crestron Home; no cloud refresh credential is included.</returns>
	internal Dictionary<string, object> DriverValues ()
		{
		if (State != LocalKeyState.Verified)
			{
			throw new InvalidOperationException ("Check key status after physical verification before exporting driver settings.");
			}
		using RSA key = OpenKey ();
		return new ()
			{
			["ConnectionMode"] = "Signed LAN", ["LocalHost"] = Host, ["LocalPassword"] = Password,
			["LocalSigningKey"] = SigningKey, ["AllowLocalControl"] = "false", ["LocalRefreshIntervalSeconds"] = 15,
			["ClientId"] = "", ["RefreshToken"] = "", ["SiteId"] = "",
			["UseCloudHistory"] = "false", ["RefreshIntervalSeconds"] = 60
			};
		}
	}

/// <summary>Short-lived cloud authorization supplied by the sole credential-helper owner.</summary>
internal sealed class PreparedCloudSession
	{
	/// <summary>Gets or sets the cloud or fleet API selection.</summary>
	[JsonPropertyName ("mode")] public string Mode { get; set; } = "";
	/// <summary>Gets or sets the exact authorized energy site.</summary>
	[JsonPropertyName ("siteId")] public string SiteId { get; set; } = "";
	/// <summary>Gets or sets the short-lived token; refresh credentials are never consumed.</summary>
	[JsonPropertyName ("accessToken")] public string AccessToken { get; set; } = "";
	/// <summary>Gets or sets the Fleet application identifier.</summary>
	[JsonPropertyName ("clientId")] public string ClientId { get; set; } = "";
	/// <summary>Gets or sets the Fleet region.</summary>
	[JsonPropertyName ("region")] public string Region { get; set; } = "auto";

	/// <summary>Builds a session restricted to the profile's exact site without taking refresh ownership.</summary>
	/// <param name="siteId">The profile's already selected site.</param>
	/// <returns>Non-persisting cloud options with no refresh token.</returns>
	internal PowerwallOptions Options (string siteId)
		{
		if (Mode is not ("cloud" or "fleet") || string.IsNullOrWhiteSpace (AccessToken)
			|| SiteId != siteId || string.IsNullOrEmpty (SiteId) || !SiteId.All (char.IsAsciiDigit)
			|| (Mode == "fleet" && (string.IsNullOrWhiteSpace (ClientId) || Region is not ("auto" or "na" or "eu" or "cn"))))
			throw new InvalidDataException ("The prepared cloud session must select the profile's exact site.");
		bool fleet = Mode == "fleet";
		return new PowerwallOptions
			{
			CloudMode = !fleet, FleetApi = fleet, SiteId = SiteId,
			AccessToken = fleet ? null : AccessToken, FleetApiAccessToken = fleet ? AccessToken : null,
			FleetApiClientId = fleet ? ClientId : null, FleetApiRegion = Region,
			NoCloudTokenPersistence = true, NoFleetApiTokenPersistence = true, Timeout = TimeSpan.FromSeconds (25)
			};
		}
	}
