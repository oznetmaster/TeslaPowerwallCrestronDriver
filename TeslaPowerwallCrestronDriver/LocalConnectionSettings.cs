// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using Crestron.DeviceDrivers.EntityModel.Data;
using TeslaPowerwallLibrary;

namespace TeslaPowerwall.CrestronDriver;

/// <summary>Installer settings for an independent local connection.</summary>
internal sealed class LocalConnectionSettings
	{
	/// <summary>Gets the selected connection; existing installations retain cloud access.</summary>
	internal string Connection { get; private set; } = "Cloud";
	/// <summary>Gets the IP address or DNS hostname.</summary>
	internal string Host { get; private set; } = string.Empty;
	/// <summary>Gets the local customer or equipment-label password.</summary>
	internal string Password { get; private set; } = string.Empty;
	/// <summary>Gets the private key from the masked persistent configuration.</summary>
	internal string SigningKey { get; private set; } = string.Empty;
	/// <summary>Gets whether the installer explicitly permitted local setting changes.</summary>
	internal bool AllowControl { get; private set; }
	/// <summary>Gets whether cloud history was explicitly enabled for the local site.</summary>
	internal bool UseCloudHistory { get; private set; }
	/// <summary>Gets the delay between completed local polls, in seconds.</summary>
	internal int IntervalSeconds { get; private set; } = 15;
	/// <summary>Gets whether local access was selected.</summary>
	internal bool IsLocal => Connection != "Cloud";
	/// <summary>Gets the protocol corresponding to the validated selection.</summary>
	internal PowerwallLocalProtocol Protocol => Connection switch
		{
		"Gateway" => PowerwallLocalProtocol.Gateway,
		"Setup Wi-Fi" => PowerwallLocalProtocol.Tedapi,
		_ => PowerwallLocalProtocol.TedapiSigned
		};

	/// <summary>Merges an installer update without changing the active settings.</summary>
	/// <param name="values">Supplied configuration values.</param>
	/// <param name="errors">Validation errors indexed by configuration item.</param>
	/// <returns>The proposed settings, to activate only after validation succeeds.</returns>
	internal LocalConnectionSettings Merge (IDictionary<string, DriverEntityValue?> values, IDictionary<string, string> errors)
		{
		var next = (LocalConnectionSettings)MemberwiseClone ();
		next.Connection = Read (values, "ConnectionMode") ?? Connection;
		next.Host = (Read (values, "LocalHost") ?? Host).Trim ();
		next.Password = Read (values, "LocalPassword") ?? Password;
		next.SigningKey = Read (values, "LocalSigningKey") ?? SigningKey;
		string history = Read (values, "UseCloudHistory");
		if (history != null)
			{
			if (bool.TryParse (history, out bool enabled))
				{
				next.UseCloudHistory = enabled;
				}
			else
				{
				errors["UseCloudHistory"] = "Select true or false.";
				}
			}
		string controls = Read (values, "AllowLocalControl");
		if (controls != null)
			{
			if (bool.TryParse (controls, out bool allowed))
				{
				next.AllowControl = allowed;
				}
			else
				{
				errors["AllowLocalControl"] = "Select true or false.";
				}
			}
		string interval = Read (values, "LocalRefreshIntervalSeconds");
		if (interval != null)
			{
			if (int.TryParse (interval, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
				{
				next.IntervalSeconds = seconds;
				}
			else
				{
				errors["LocalRefreshIntervalSeconds"] = "Enter a whole number of seconds.";
				}
			}
		if (next.IntervalSeconds < 15 || next.IntervalSeconds > 3600)
			{
			errors["LocalRefreshIntervalSeconds"] = "Local polling must be between 15 and 3600 seconds.";
			}
		if (next.Connection != "Cloud" && next.Connection != "Gateway" && next.Connection != "Setup Wi-Fi" && next.Connection != "Signed LAN")
			{
			errors["ConnectionMode"] = "Select Cloud, Gateway, Setup Wi-Fi, or Signed LAN.";
			}
		if (next.IsLocal)
			{
			if (string.IsNullOrWhiteSpace (next.Host) || !Uri.TryCreate ("https://" + next.Host, UriKind.Absolute, out Uri endpoint)
				|| endpoint.HostNameType == UriHostNameType.Unknown || endpoint.UserInfo.Length != 0
				|| endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
				{
				errors["LocalHost"] = "Enter a hostname or IP address, optionally followed by an HTTPS port.";
				}
			if (string.IsNullOrWhiteSpace (next.Password))
				{
				errors["LocalPassword"] = "Enter the local customer password, or full equipment-label password for setup Wi-Fi.";
				}
			if (next.Connection == "Signed LAN")
				{
				try
					{
					using RSA key = next.OpenSigningKey ();
					}
				catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is CryptographicException || ex is System.Xml.XmlException
#if NETFRAMEWORK
					|| ex is System.Security.XmlSyntaxException
#endif
					)
					{
					errors["LocalSigningKey"] = "Import the registered 4096-bit private key produced by the driver's local setup tool.";
					}
				}
			}
		return next;
		}

	/// <summary>Imports a processor-compatible RSA key; the caller owns its lifetime.</summary>
	/// <returns>The imported 4096-bit RSA private key.</returns>
	/// <exception cref="CryptographicException">The key is missing, public-only, or the wrong size.</exception>
	internal RSA OpenSigningKey ()
		{
		var key = new RSACryptoServiceProvider { PersistKeyInCsp = false };
		try
			{
			if (string.IsNullOrWhiteSpace (SigningKey))
				{
				throw new CryptographicException ("A registered private key is required.");
				}
			key.FromXmlString (SigningKey);
			if (key.KeySize != 4096 || key.PublicOnly)
				{
				throw new CryptographicException ("A 4096-bit private key is required.");
				}
			return key;
			}
		catch
			{
			key.Dispose ();
			throw;
			}
		}

	private static string Read (IDictionary<string, DriverEntityValue?> values, string name) =>
		values != null && values.TryGetValue (name, out DriverEntityValue? value) && value.HasValue ? value.Value.ToString () : null;
	}
