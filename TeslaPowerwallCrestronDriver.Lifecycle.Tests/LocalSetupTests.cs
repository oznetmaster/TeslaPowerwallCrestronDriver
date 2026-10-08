// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Serialization.Json;
using NUnit.Framework;
using TeslaPowerwall.CrestronDriver.LocalSetup;
using TeslaPowerwallCrestronDriver.Tests;
using TeslaPowerwallLibrary.Tedapi;

namespace TeslaPowerwallCrestronDriver.Lifecycle.Tests;

/// <summary>Offline checks for independent provisioning and portable diagnostic credentials.</summary>
[TestFixture]
public sealed class LocalSetupTests
	{
	private static readonly Lazy<string> _privateKey = new (() =>
		{
		using RSA key = RSA.Create (4096);
		return key.ToXmlString (true);
		});

	/// <summary>Checks that provisioning cannot export a merely pending or unknown authorization.</summary>
	/// <param name="state">A nonverified state.</param>
	[TestCase (LocalKeyState.Unknown)]
	[TestCase (LocalKeyState.PendingVerification)]
	[TestCase (LocalKeyState.VerificationTimedOut)]
	[TestCase (LocalKeyState.Removed)]
	public void ExportRequiresVerifiedKey (LocalKeyState state)
		{
		Assert.Throws<InvalidOperationException> (() => new ProvisioningProfile { State = state }.DriverValues ());
		}

	/// <summary>Checks that installer exports contain independent local credentials and no cloud refresh token.</summary>
	[Test]
	public void ExportImportsIntoProcessorCompatibleKeyProvider ()
		{
		var profile = Profile ();
		var values = profile.DriverValues ();
		Assert.That (values["RefreshToken"], Is.EqualTo (""));
		Assert.That (values["LocalSigningKey"], Is.EqualTo (_privateKey.Value));
		Assert.That (values["AllowLocalControl"], Is.EqualTo ("false"));
		Assert.That (values["UseCloudHistory"], Is.EqualTo ("false"));
		Assert.That (values["LocalRefreshIntervalSeconds"], Is.EqualTo (15));
		Assert.That (values["RefreshIntervalSeconds"], Is.EqualTo (60), "The installer SDK validates every numeric field before driver configuration.");
		using var processorKey = new RSACryptoServiceProvider { PersistKeyInCsp = false };
		processorKey.FromXmlString ((string)values["LocalSigningKey"]);
		using RSA windowsKey = profile.OpenKey ();
		byte[] message = Encoding.UTF8.GetBytes ("offline round-trip verification");
		byte[] signature = processorKey.SignData (message, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1);
		Assert.That (windowsKey.VerifyData (message, signature, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1), Is.True);
		}

	/// <summary>Checks private profile encryption and durable preservation of interrupted enrollment state.</summary>
	[Test]
	public void EncryptedProfilePreservesKeyAndRecoveryState ()
		{
		if (!OperatingSystem.IsWindows ())
			{
			return;
			}
		string directory = Path.Combine (TestContext.CurrentContext.WorkDirectory, "setup-test-" + Guid.NewGuid ().ToString ("N"));
		Directory.CreateDirectory (directory);
		try
			{
			string path = Path.Combine (directory, "profile.dat");
			var profile = Profile ();
			profile.EnrollmentPending = true;
			profile.Save (path);
			var loaded = ProvisioningProfile.Load (path);
			Assert.That (loaded.SigningKey, Is.EqualTo (profile.SigningKey));
			Assert.That (loaded.EnrollmentPending, Is.True);
			Assert.That (Encoding.UTF8.GetString (File.ReadAllBytes (path)), Does.Not.Contain ("synthetic"));
			}
		finally
			{
			Directory.Delete (directory, true);
			}
		}

	/// <summary>Checks Windows JSON to processor contract to Windows JSON round-trip compatibility.</summary>
	[Test]
	public void LocalLiveInputsRoundTripWithoutRenamingFieldsOrLosingCredentials ()
		{
		string json = JsonSerializer.Serialize (new
			{
			enabled = false, mode = "local", localProtocol = "Signed LAN", host = "powerwall.example.invalid",
			password = "synthetic-local", localSigningKey = _privateKey.Value, localRefreshIntervalSeconds = 30
			});
		var serializer = new DataContractJsonSerializer (typeof (LiveSettings));
		using var input = new MemoryStream (Encoding.UTF8.GetBytes (json));
		var processor = (LiveSettings)serializer.ReadObject (input)!;
		processor.ValidateLocal ();
		using var output = new MemoryStream ();
		serializer.WriteObject (output, processor);
		using var returned = JsonDocument.Parse (output.ToArray ());
		Assert.That (returned.RootElement.GetProperty ("localSigningKey").GetString (), Is.EqualTo (_privateKey.Value));
		Assert.That (returned.RootElement.GetProperty ("host").GetString (), Is.EqualTo ("powerwall.example.invalid"));
		Assert.That (returned.RootElement.GetProperty ("localRefreshIntervalSeconds").GetInt32 (), Is.EqualTo (30));
		Assert.That (returned.RootElement.GetProperty ("enabled").GetBoolean (), Is.False);
		}

	private static ProvisioningProfile Profile () => new ()
		{
		Mode = "cloud", RefreshToken = "synthetic-setup-only-token", SiteId = "123", Host = "powerwall.example.invalid",
		Password = "synthetic-local", SigningKey = _privateKey.Value, State = LocalKeyState.Verified
		};
	}
