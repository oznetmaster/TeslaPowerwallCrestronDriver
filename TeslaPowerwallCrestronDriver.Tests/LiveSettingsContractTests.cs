// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using NUnit.Framework;

namespace TeslaPowerwallCrestronDriver.Tests;

/// <summary>Cross-runtime checks of the shared bidirectional diagnostic input contract.</summary>
[TestFixture]
public sealed class LiveSettingsContractTests
	{
	/// <summary>Checks that a processor-written input can be loaded again with credentials and settings intact.</summary>
	/// <param name="mode">The input connection mode.</param>
	[TestCase ("cloud")]
	[TestCase ("fleet")]
	[TestCase ("local")]
	public void InputRoundTripPreservesCredentialFields (string mode)
		{
		var input = new LiveSettings
			{
			Enabled = false, Mode = mode, AccessToken = "synthetic-access", ClientId = "synthetic-client",
			SiteId = "123", Region = "eu", LocalProtocol = "Gateway", Host = "powerwall.example.invalid",
			Password = "synthetic-password", LocalSigningKey = "", LocalRefreshIntervalSeconds = 45, LocalSigningKeyName = "synthetic-key-name"
			};
		var serializer = new DataContractJsonSerializer (typeof (LiveSettings));
		using var output = new MemoryStream ();
		serializer.WriteObject (output, input);
		string json = Encoding.UTF8.GetString (output.ToArray ());
		Assert.That (json, Does.Contain ("\"localRefreshIntervalSeconds\":45"));
		output.Position = 0;
		var returned = (LiveSettings)serializer.ReadObject (output);
		Assert.That (returned.Mode, Is.EqualTo (mode));
		Assert.That (returned.AccessToken, Is.EqualTo (input.AccessToken));
		Assert.That (returned.ClientId, Is.EqualTo (input.ClientId));
		Assert.That (returned.Password, Is.EqualTo (input.Password));
		Assert.That (returned.Host, Is.EqualTo (input.Host));
		Assert.That (returned.LocalSigningKeyName, Is.EqualTo (input.LocalSigningKeyName));
		Assert.That (returned.Enabled, Is.False);
		if (mode == "local")
			{
			returned.ValidateLocal ();
			Assert.That (returned.LocalSettings ().AllowControl, Is.False);
			}
		}
	}
