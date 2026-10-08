// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;

using NUnit.Framework;

using TeslaPowerwall.CrestronDriver;

using TeslaPowerwallLibrary;

namespace TeslaPowerwallCrestronDriver.Tests;

// Read-only polling uses shared Windows/processor inputs. Cloud refresh credentials never enter the test host.
[TestFixture, Category ("Processor"), Category ("Live"), NonParallelizable, FixtureLifeCycle (LifeCycle.SingleInstance)]
public sealed partial class LiveSiteTests
	{
	private const BindingFlags PRIVATE = BindingFlags.Instance | BindingFlags.NonPublic;
	private DriverLogger _logger;
	private TeslaPowerwallDriver _driver;
	private Powerwall _client;
	private LiveSettings _settings;
	private RSA _borrowedKey;
	private CancellationTokenSource _deadline;

	[OneTimeSetUp]
	public async Task ConnectSite ()
		{
		string flag = TestContext.Parameters.Get ("EnableLiveTests", "");
		if (flag.Length != 0 && !bool.TryParse (flag, out _))
			throw new InvalidDataException ("EnableLiveTests must be true or false.");
		if (flag.Equals ("false", StringComparison.OrdinalIgnoreCase))
			Assert.Ignore ("Live tests are disabled for this run.");
		string directory = TestContext.Parameters.Get ("TestDataDirectory", "");
		if (string.IsNullOrWhiteSpace (directory))
			directory = Environment.GetEnvironmentVariable ("TESLA_LIVE_TEST_DATA_DIRECTORY") ?? "";
		string path = Path.Combine (string.IsNullOrWhiteSpace (directory)
			? Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.LocalApplicationData), "TeslaPowerwallCrestronDriver") : directory, "LiveTestSettings.json");
		if (File.Exists (path))
			{
			try
				{
				using var input = File.OpenRead (path);
				_settings = (LiveSettings)new DataContractJsonSerializer (typeof (LiveSettings)).ReadObject (input);
				}
			catch (SerializationException) { throw new InvalidDataException ("LiveTestSettings.json must contain a valid settings object."); }
			}
		if (!flag.Equals ("true", StringComparison.OrdinalIgnoreCase) && _settings?.Enabled != true)
			Assert.Ignore ("Live driver tests require private settings and explicit enablement.");
		if (_settings?.Mode == "local")
			{
			_settings.ValidateLocal ();
			}
		else if (string.IsNullOrWhiteSpace (_settings?.AccessToken) || string.IsNullOrWhiteSpace (_settings.SiteId)
			 || !_settings.SiteId.All (char.IsDigit) || _settings.Mode is not ("fleet" or "cloud")
			 || (_settings.Mode == "fleet" && (string.IsNullOrWhiteSpace (_settings.ClientId) || _settings.Region is not ("auto" or "na" or "eu" or "cn"))))
			throw new InvalidDataException ("Live settings require accessToken, numeric siteId, mode (fleet/cloud), and clientId/region for Fleet API.");
#if NETFRAMEWORK
		if (Type.GetType ("Mono.Runtime") == null)
			Assert.Ignore ("Run live fixtures through the desktop SDK harness or processor runtime.");
#endif
		_deadline = new CancellationTokenSource (TimeSpan.FromMinutes (3));
		_logger = new DriverLogger ("powerwall-live-test") { AppLogger = new QuietLogger () };
		_driver = new TeslaPowerwallDriver (new DriverControllerCreationArgs ("powerwall-live-test", TestSupport.DataDirectory, _logger.AppLogger, null), TestSupport.Resources (_logger));
		bool fleet = _settings.Mode == "fleet";
		if (_settings.Mode == "local")
			{
			var local = _settings.LocalSettings ();
			Set ("_local", local);
			typeof (TeslaPowerwallDriver).GetMethod ("UpdateConnectionPresentation", PRIVATE).Invoke (_driver, null);
			// Use the production factory so local live coverage includes driver connection settings.
			if (_settings.UsesWindowsKey)
				{
				_borrowedKey = _settings.OpenWindowsKey ();
				_client = new Powerwall (new PowerwallOptions
					{
					Host = local.Host, Password = local.Password, LocalProtocol = local.Protocol, LocalSigningKey = _borrowedKey,
					AllowLocalControl = false, NoLocalSessionPersistence = true, CacheExpireSeconds = local.IntervalSeconds, Timeout = TimeSpan.FromSeconds (20)
					});
				}
			else
				_client = (Powerwall)typeof (TeslaPowerwallDriver).GetMethod ("GetOrCreateClient", PRIVATE).Invoke (_driver, null);
			}
		else
			{
		_client = new Powerwall (new PowerwallOptions
			{
			FleetApi = fleet,
			CloudMode = !fleet,
			SiteId = _settings.SiteId,
			FleetApiClientId = fleet ? _settings.ClientId : null,
			FleetApiRegion = _settings.Region ?? "na",
			FleetApiAccessToken = fleet ? _settings.AccessToken : null,
			AccessToken = fleet ? null : _settings.AccessToken,
			NoFleetApiTokenPersistence = true,
			NoCloudTokenPersistence = true,
			Timeout = TimeSpan.FromSeconds (20)
			});
			}
		// Exercise the driver's real polling and publication with a non-refreshing API connection.
		Set ("_client", _client);
		Set ("_clientId", fleet ? _settings.ClientId : string.Empty);
		Set ("_siteId", _settings.SiteId);
		await Poll ();
		}

	[OneTimeTearDown]
	public void DisposeSite ()
		{
		_driver?.Dispose ();
		_client?.Dispose ();
		_logger?.Dispose ();
		_borrowedKey?.Dispose ();
		_borrowedKey = null;
		_deadline?.Dispose ();
		_driver = null;
		_client = null;
		_logger = null;
		_deadline = null;
		_settings = null;
		}

	private void Set (string field, object value) => typeof (TeslaPowerwallDriver).GetField (field, PRIVATE).SetValue (_driver, value);
	private async Task Poll ()
		{
		await (Task)typeof (TeslaPowerwallDriver).GetMethod ("RunPollAsync", PRIVATE).Invoke (_driver, new object[] { _deadline.Token });
		Assert.That (_driver.OnlineIndicatorIsOnline && _driver.ReadyIndicatorIsReady, Is.True,
			"Live Tesla polling must become ready. If authentication has expired, stop the run and prepare another session using the dedicated test credential profile.");
		}

	[Test]
	public async Task RealSite_PublishesReadyStateAndSelectedSite ()
		{
		if (_settings.Mode != "local")
			{
			Assert.That (_settings.Mode == "fleet" ? _client.FleetApiSiteId : _client.CloudSiteId, Is.EqualTo (_settings.SiteId));
			}
		if (_settings.Mode == "local")
			{
			string name = await _client.SiteNameAsync (_deadline.Token);
			Assert.That (_driver.SiteNameDisplay, Is.EqualTo (string.IsNullOrWhiteSpace (name) ? "--" : name),
				"A missing local site name must remain unavailable, not be invented from cloud data.");
			Assert.That (_client.LocalHost, Is.EqualTo (_settings.Host));
			}
		else
			Assert.That (_driver.SiteNameDisplay, Is.Not.Null.And.Not.Empty.And.Not.EqualTo ("--"));
		Assert.That (_driver.OnlineIndicatorIsOnline && _driver.ReadyIndicatorIsReady, Is.True);
		}

	[Test]
	public async Task RealSite_PublishesBatteryAndOperatingState ()
		{
		Assert.That (_driver.BatteryLevelDisplay, Does.Contain ("%"));
		double? reserve = await _client.GetReserveAsync (cancellationToken: _deadline.Token);
		string mode = await _client.GetModeAsync (cancellationToken: _deadline.Token);
		if (_settings.Mode != "local" || _settings.LocalProtocol != "Gateway")
			{
			Assert.That (reserve, Is.Not.Null.And.InRange (0d, 100d));
			Assert.That (mode, Is.Not.Null.And.Not.Empty);
			}
		Assert.That (_driver.OperatingSettingsVisible, Is.EqualTo (reserve.HasValue && !string.IsNullOrWhiteSpace (mode)));
		if (reserve.HasValue) Assert.That (_driver.BackupReservePercent, Is.EqualTo ((int)Math.Round (reserve.Value, MidpointRounding.AwayFromZero)));
		if (!string.IsNullOrWhiteSpace (mode)) Assert.That (_driver.OperationMode, Is.EqualTo (mode));
		}

	[Test]
	public async Task RealSite_RefreshRetainsConnectionAndSite ()
		{
		if (_settings.Mode == "local")
			await Task.Delay (TimeSpan.FromSeconds (_settings.LocalSettings ().IntervalSeconds + 1), _deadline.Token);
		await Poll ();
		Assert.That (typeof (TeslaPowerwallDriver).GetField ("_client", PRIVATE).GetValue (_driver), Is.SameAs (_client));
		if (_settings.Mode != "local")
			{
			Assert.That (_settings.Mode == "fleet" ? _client.FleetApiSiteId : _client.CloudSiteId, Is.EqualTo (_settings.SiteId));
			}
		}

	private sealed class QuietLogger : DriverControllerLogger
		{
		public override bool IsEnabled (string id, LogEntryLevel level) => false;
		public override LogEntryLevel GetCurrentLevel (string id) => LogEntryLevel.Error;
		public override void Exception (string id, Exception exception, string message, params object[] args)
			{
			}
		public override void Log (string id, LogEntryLevel level, string message)
			{
			}
		public override void Log (string id, LogEntryLevel level, string message, params object[] args)
			{
			}
		public override void Log<T> (string id, LogEntryLevel level, string message, T arg)
			{
			}
		public override void Log<T1, T2> (string id, LogEntryLevel level, string message, T1 arg1, T2 arg2)
			{
			}
		public override void Log<T1, T2, T3> (string id, LogEntryLevel level, string message, T1 arg1, T2 arg2, T3 arg3)
			{
			}
		}
	}