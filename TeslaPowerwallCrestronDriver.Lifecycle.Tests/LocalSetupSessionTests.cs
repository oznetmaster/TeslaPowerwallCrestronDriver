// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System.IO;
using NUnit.Framework;
using TeslaPowerwall.CrestronDriver.LocalSetup;

namespace TeslaPowerwallCrestronDriver.Tests;

/// <summary>Checks portable provisioning without duplicating cloud refresh-token ownership.</summary>
[TestFixture]
public sealed class LocalSetupSessionTests
	{
	/// <summary>Uses the prepared access token and never supplies a refresh token.</summary>
	/// <param name="mode">The cloud API supplying enrollment authorization.</param>
	[TestCase ("cloud"), TestCase ("fleet")]
	public void PreparedSessionDoesNotTakeRefreshOwnership (string mode)
		{
		var session = new PreparedCloudSession { Mode = mode, SiteId = "123", AccessToken = "synthetic", ClientId = "client", Region = "eu" };
		var options = session.Options ("123");
		Assert.That (options.RefreshToken, Is.Null.Or.Empty);
		Assert.That (options.FleetApiRefreshToken, Is.Null.Or.Empty);
		Assert.That (options.CloudMode, Is.EqualTo (mode == "cloud"));
		Assert.That (options.FleetApi, Is.EqualTo (mode == "fleet"));
		Assert.That (options.SiteId, Is.EqualTo ("123"));
		}

	/// <summary>Rejects a prepared authorization for a different energy site before any registration.</summary>
	[Test]
	public void PreparedSessionRejectsWrongSite ()
		{
		var session = new PreparedCloudSession { Mode = "cloud", SiteId = "123", AccessToken = "synthetic" };
		Assert.Throws<InvalidDataException> (() => session.Options ("456"));
		}

	/// <summary>Allows a local-only profile while retaining the independent refresh-profile validation.</summary>
	[Test]
	public void LocalProfileRequiresNoCloudRefreshCredential ()
		{
		var profile = new ProvisioningProfile { SiteId = "123", Host = "powerwall.example.invalid", Password = "synthetic" };
		Assert.DoesNotThrow (() => profile.Validate (requireRefreshCredential: false));
		Assert.Throws<InvalidDataException> (() => profile.Validate ());
		}
	}
