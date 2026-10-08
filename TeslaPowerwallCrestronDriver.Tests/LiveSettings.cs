// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Reflection;
using System.Runtime.Serialization;
using Crestron.DeviceDrivers.EntityModel.Data;
using TeslaPowerwall.CrestronDriver;

namespace TeslaPowerwallCrestronDriver.Tests;

/// <summary>Portable live-test input contract shared unchanged by Windows and the processor.</summary>
[DataContract]
internal sealed class LiveSettings
	{
	/// <summary>Gets or sets the explicit live-test enablement flag.</summary>
	[DataMember (Name = "enabled")]
	public bool Enabled { get; set; }
	/// <summary>Gets or sets cloud, fleet, or local mode.</summary>
	[DataMember (Name = "mode")]
	public string Mode { get; set; }
	/// <summary>Gets or sets the short-lived cloud access token.</summary>
	[DataMember (Name = "accessToken")]
	public string AccessToken { get; set; }
	/// <summary>Gets or sets the Fleet application identifier.</summary>
	[DataMember (Name = "clientId")]
	public string ClientId { get; set; }
	/// <summary>Gets or sets the exact cloud site identifier.</summary>
	[DataMember (Name = "siteId")]
	public string SiteId { get; set; }
	/// <summary>Gets or sets the Fleet region.</summary>
	[DataMember (Name = "region")]
	public string Region { get; set; }
	/// <summary>Gets or sets Gateway, Setup Wi-Fi, or Signed LAN.</summary>
	[DataMember (Name = "localProtocol")]
	public string LocalProtocol { get; set; }
	/// <summary>Gets or sets the local hostname or address.</summary>
	[DataMember (Name = "host")]
	public string Host { get; set; }
	/// <summary>Gets or sets the selected local protocol password.</summary>
	[DataMember (Name = "password")]
	public string Password { get; set; }
	/// <summary>Gets or sets the registered RSA private key in portable XML form.</summary>
	[DataMember (Name = "localSigningKey")]
	public string LocalSigningKey { get; set; }
	/// <summary>Gets or sets an existing Windows-only CNG key name; use portable XML on the processor.</summary>
	[DataMember (Name = "localSigningKeyName")]
	public string LocalSigningKeyName { get; set; }
	/// <summary>Gets or sets the local poll interval; zero uses the driver default.</summary>
	[DataMember (Name = "localRefreshIntervalSeconds")]
	public int LocalRefreshIntervalSeconds { get; set; }

	/// <summary>Validates a local input without contacting a device or enrolling a key.</summary>
	internal void ValidateLocal () => _ = LocalSettings ();

	/// <summary>Converts the portable local input into read-only driver settings.</summary>
	/// <returns>Validated settings using the same format on Windows and Mono.</returns>
	internal LocalConnectionSettings LocalSettings ()
		{
		var errors = new Dictionary<string, string> ();
		var values = new Dictionary<string, DriverEntityValue?>
			{
			["ConnectionMode"] = new DriverEntityValue (UsesWindowsKey ? "Gateway" : LocalProtocol ?? ""),
			["LocalHost"] = new DriverEntityValue (Host ?? ""),
			["LocalPassword"] = new DriverEntityValue (Password ?? ""),
			["LocalSigningKey"] = new DriverEntityValue (LocalSigningKey ?? ""),
			["LocalRefreshIntervalSeconds"] = new DriverEntityValue ((long)(LocalRefreshIntervalSeconds == 0 ? 15 : LocalRefreshIntervalSeconds))
			};
		LocalConnectionSettings result = new LocalConnectionSettings ().Merge (values, errors);
		if (errors.Count != 0 || !result.IsLocal)
			{
			throw new InvalidDataException ("Local live inputs require a local protocol, host, password and, for Signed LAN, a registered RSA-4096 private key. Credentials withheld.");
			}
		if (UsesWindowsKey)
			{
			using RSA key = OpenWindowsKey ();
			// Fixture-only borrowed key: production provisioning still requires portable XML.
			typeof (LocalConnectionSettings).GetProperty ("Connection", BindingFlags.Instance | BindingFlags.NonPublic).SetValue (result, "Signed LAN");
			}
		return result;
		}
	/// <summary>Gets whether this input explicitly references the existing Windows key.</summary>
	internal bool UsesWindowsKey => LocalProtocol == "Signed LAN" && !string.IsNullOrWhiteSpace (LocalSigningKeyName);

	/// <summary>Opens the existing Windows key without exporting, creating or registering it.</summary>
	/// <returns>A caller-owned signing key.</returns>
	internal RSA OpenWindowsKey ()
		{
		if (!string.IsNullOrWhiteSpace (LocalSigningKey)) throw new InvalidDataException ("Select either a Windows key name or portable XML, not both.");
		if (
#if NETFRAMEWORK
			Environment.OSVersion.Platform != PlatformID.Win32NT
#else
			!OperatingSystem.IsWindows ()
#endif
			)
			throw new InvalidDataException ("This saved signing key is Windows-only. Processor Signed LAN tests require a registered portable key in localSigningKey.");
		using CngKey stored = CngKey.Open (LocalSigningKeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
		var key = new RSACng (stored);
		if (key.KeySize == 4096) return key;
		key.Dispose ();
		throw new InvalidDataException ("Signed LAN requires an RSA-4096 key.");
		}
	}
