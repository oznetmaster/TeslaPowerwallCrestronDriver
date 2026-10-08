// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;
using Crestron.DeviceDrivers.EntityModel.Data;
using NUnit.Framework;
using TeslaPowerwall.CrestronDriver;
using TeslaPowerwallLibrary;

namespace TeslaPowerwallCrestronDriver.Tests;

/// <summary>Offline installer validation independent of the Crestron runtime.</summary>
[TestFixture]
public sealed class LocalConnectionTests
	{
	/// <summary>Checks safe upgrade defaults.</summary>
	[Test]
	public void DefaultsPreserveCloudAndLocalReadOnly ()
		{
		var settings = new LocalConnectionSettings ();
		Assert.That (settings.IsLocal, Is.False);
		Assert.That (settings.IntervalSeconds, Is.EqualTo (15));
		Assert.That (settings.AllowControl, Is.False);
		}

	/// <summary>Checks interval boundaries and malformed values without network activity.</summary>
	/// <param name="value">Installer input.</param>
	/// <param name="invalid">Whether validation must reject the input.</param>
	[TestCase ("14", true)]
	[TestCase ("15", false)]
	[TestCase ("60", false)]
	[TestCase ("3600", false)]
	[TestCase ("3601", true)]
	[TestCase ("15.5", true)]
	[TestCase ("invalid", true)]
	public void PollingIntervalIsValidated (string value, bool invalid)
		{
		var errors = new Dictionary<string, string> ();
		var current = new LocalConnectionSettings ();
		current.Merge (Values ("LocalRefreshIntervalSeconds", value), errors);
		Assert.That (errors.ContainsKey ("LocalRefreshIntervalSeconds"), Is.EqualTo (invalid));
		Assert.That (current.IntervalSeconds, Is.EqualTo (15), "Pending edits cannot modify active configuration.");
		}

	/// <summary>Checks hostname, address, and endpoint validation.</summary>
	/// <param name="host">Installer input.</param>
	/// <param name="invalid">Whether validation must reject the input.</param>
	[TestCase ("powerwall.example.invalid", false)]
	[TestCase ("192.0.2.1", false)]
	[TestCase ("192.0.2.1:8443", false)]
	[TestCase ("[2001:db8::1]", false)]
	[TestCase ("", true)]
	[TestCase ("https://192.0.2.1", true)]
	[TestCase ("user:secret@192.0.2.1", true)]
	[TestCase ("192.0.2.1/api", true)]
	public void LocalHostValidation (string host, bool invalid)
		{
		var errors = new Dictionary<string, string> ();
		var values = Values ("ConnectionMode", "Gateway");
		values["LocalHost"] = new DriverEntityValue (host);
		values["LocalPassword"] = new DriverEntityValue ("synthetic");
		var settings = new LocalConnectionSettings ().Merge (values, errors);
		Assert.That (errors.ContainsKey ("LocalHost"), Is.EqualTo (invalid));
		Assert.That (settings.Protocol, Is.EqualTo (PowerwallLocalProtocol.Gateway));
		}

	/// <summary>Rejects unsupported modes without changing the active configuration.</summary>
	/// <param name="connection">The unsupported connection mode.</param>
	[TestCase ("Setup Wi-Fi")]
	[TestCase ("unknown")]
	public void UnsupportedConnectionIsRejected (string connection)
		{
		var errors = new Dictionary<string, string> ();
		var values = Values ("ConnectionMode", connection);
		values["LocalHost"] = new DriverEntityValue ("192.0.2.1");
		values["LocalPassword"] = new DriverEntityValue ("synthetic");
		var current = new LocalConnectionSettings ();
		var proposed = current.Merge (values, errors);
		Assert.That (errors.ContainsKey ("ConnectionMode"), Is.True);
		Assert.That (current.Connection, Is.EqualTo ("Cloud"));
		Assert.Throws<InvalidOperationException> (() => _ = proposed.Protocol);
		}

	/// <summary>Checks missing or malformed signed-access credentials without disclosing them.</summary>
	/// <param name="key">An invalid key representation.</param>
	[TestCase ("")]
	[TestCase ("not-a-key")]
	public void SignedAccessRequiresARegisteredPrivateKey (string key)
		{
		var errors = new Dictionary<string, string> ();
		var values = Values ("ConnectionMode", "Signed LAN");
		values["LocalSigningKey"] = new DriverEntityValue (key);
		new LocalConnectionSettings ().Merge (values, errors);
		Assert.That (errors.ContainsKey ("LocalSigningKey"), Is.True);
		Assert.That (errors.ContainsKey ("LocalPassword"), Is.True);
		}

	/// <summary>Checks that a normal RSA key with insufficient size is rejected.</summary>
	[Test]
	public void SigningRequires4096Bits ()
		{
		using var key = new RSACryptoServiceProvider (2048) { PersistKeyInCsp = false };
		var errors = new Dictionary<string, string> ();
		var values = Values ("ConnectionMode", "Signed LAN");
		values["LocalSigningKey"] = new DriverEntityValue (key.ToXmlString (true));
		new LocalConnectionSettings ().Merge (values, errors);
		Assert.That (errors.ContainsKey ("LocalSigningKey"), Is.True);
		}

	/// <summary>Checks the installer choices in the actual driver manifest, including packaged test data.</summary>
	[Test]
	public void InstallerOffersOnlyCloudAndHomeNetworkConnections ()
		{
		using JsonDocument manifest = JsonDocument.Parse (File.ReadAllText (Path.Combine (TestSupport.DataDirectory, "DriverDefinition.json")));
		var connection = manifest.RootElement.GetProperty ("ConfigurationSteps").GetProperty ("Items")
			.EnumerateArray ().Single (item => item.GetProperty ("Id").GetString () == "ConnectionMode");
		Assert.That (connection.GetProperty ("AvailableValues").EnumerateArray ().Select (value => value.GetString ()),
			Is.EqualTo (new[] { "Cloud", "Gateway", "Signed LAN" }));
		}

	private static Dictionary<string, DriverEntityValue?> Values (string key, string value) => new () { [key] = new DriverEntityValue (value) };
	}
