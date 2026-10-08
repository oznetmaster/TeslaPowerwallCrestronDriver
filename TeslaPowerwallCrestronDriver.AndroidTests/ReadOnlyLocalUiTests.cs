// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System.Text.Json;
using System.Xml.Linq;

using CrestronHomeNUnit.Android;

using NUnit.Framework;

namespace TeslaPowerwallCrestronDriver.AndroidTests;

/// <summary>Private expectations for one workflow-selected read-only local driver.</summary>
/// <param name="TileName">The unique installed instance name.</param>
/// <param name="MainPageTitle">The title observed from the driver's published site name.</param>
/// <param name="ConnectionLabel">The exact configured protocol, endpoint and polling interval.</param>
/// <param name="OperatingSettingsVisible">Whether reserve and mode were reported.</param>
/// <param name="GridSettingsVisible">Whether the selected protocol provides grid settings.</param>
internal sealed record UiExpectations (string TileName, string MainPageTitle, string ConnectionLabel,
	bool OperatingSettingsVisible, bool GridSettingsVisible);

/// <summary>Runs navigation-only Android acceptance against a workflow-selected local driver.</summary>
[TestFixture, NonParallelizable]
public sealed class ReadOnlyLocalUiTests
	{
	private AndroidWorkflowSession? _session;
	private UiExpectations _expected = null!;
	private int _depth;
	private Action<AndroidHierarchy>? _pendingDestination;
	private AndroidWorkflowSession Session => _session ?? throw new InvalidOperationException ("No workflow session.");
	private static AndroidSelector Text (string text) => new (AndroidSelectorKind.Text, text);
	private void Home (AndroidHierarchy hierarchy) => CrestronHomePages.RequireHome (hierarchy, Session.Context.Profile.ExpectedHomeText);
	private AndroidHierarchy Page (AndroidHierarchy hierarchy, int depth) => CrestronHomeExtensionPages.RequirePage (hierarchy,
		depth == 1 ? [_expected.MainPageTitle] : [_expected.MainPageTitle, "Powerwall Settings"]);

	/// <summary>Uses the adapter's owned processor/Android session; discovery alone never connects.</summary>
	/// <returns>Completion after the selected session and private expectations are validated.</returns>
	[OneTimeSetUp]
	public async Task OpenAsync ()
		{
		if (string.IsNullOrWhiteSpace (Environment.GetEnvironmentVariable (AndroidWorkflowSession.CONTEXT_VARIABLE)))
			Assert.Ignore ("Opt in through the Crestron Home NUnit workflow or installed-driver phase.");
		string path = Environment.GetEnvironmentVariable ("TESLA_ANDROID_EXPECTATIONS") ?? throw new InvalidOperationException ("Provide private UI expectations.");
		_expected = JsonSerializer.Deserialize<UiExpectations> (File.ReadAllText (path)) ?? throw new InvalidDataException ("Missing UI expectations.");
		if (string.IsNullOrWhiteSpace (_expected.TileName) || string.IsNullOrWhiteSpace (_expected.MainPageTitle) || string.IsNullOrWhiteSpace (_expected.ConnectionLabel))
			throw new InvalidDataException ("UI identity and connection expectations are required.");
		_session = await AndroidWorkflowSession.OpenFromEnvironmentAsync ();
		}

	/// <summary>Requires a restored Home before allowing the next navigation case.</summary>
	/// <returns>The guarded initial Home verification.</returns>
	[SetUp]
	public async Task RequireRestoredHomeAsync ()
		{
		if (_pendingDestination != null || _depth != 0)
			throw new InvalidOperationException ("An earlier navigation remains unconfirmed; no further input is allowed.");
		using var deadline = new CancellationTokenSource (TimeSpan.FromSeconds (90));
		await WaitAsync (Home, deadline.Token);
		}
	/// <summary>Checks the saved processor endpoint and returns to the same Home.</summary>
	/// <returns>The navigation and endpoint-verification operation.</returns>
	[Test]
	public async Task SavedEndpointMatchesWorkflowAsync ()
		{
		var navigation = new CrestronHomeNavigation (Session);
		using var deadline = new CancellationTokenSource (TimeSpan.FromMinutes (6));
		await navigation.VerifySavedEndpointAsync ("powerwall-endpoint", Session.Context.Profile.LocalPort, deadline.Token);
		Assert.That (navigation.HomeRestored, Is.True);
		}

	/// <summary>Checks rendered live readings and hides history when no cloud history account is configured.</summary>
	/// <returns>The navigation and evidence-capture operation.</returns>
	[Test]
	public async Task LocalTileDisplaysLiveReadingsAsync ()
		{
		using var deadline = new CancellationTokenSource (TimeSpan.FromMinutes (5));
		await OpenMainAsync (deadline.Token);
		await Session.CaptureAsync ("powerwall-live-readings", h =>
			{
			var page = Page (h, 1);
			foreach (string label in new[] { "House", "Solar", "Grid" })
				Assert.That (ReadRow (page, label), Does.Match (@"^(?:(?:Importing|Exporting) )?-?\d+[.,]\d+ kW$").IgnoreCase, label);
			Assert.That (ReadRow (page, "Powerwall"), Does.Match (@"\d+(?:[.,]\d+)?%"));
			page.RequireAbsent (Text ("Energy"));
			page.RequireAbsent (Text ("Impact"));
			}, deadline.Token);
		}

	/// <summary>Checks local connection details and disabled power-setting controls without operating them.</summary>
	/// <returns>The navigation and evidence-capture operation.</returns>
	[Test]
	public async Task LocalSettingsAreReadOnlyAsync ()
		{
		using var deadline = new CancellationTokenSource (TimeSpan.FromMinutes (5));
		await OpenMainAsync (deadline.Token);
		await NavigateAsync (h => Page (h, 1).RequireUnique (Text ("Settings")), h => Page (h, 1), h => Page (h, 2), 2, deadline.Token);
		await Session.CaptureAsync ("powerwall-readonly-settings", h =>
			{
			var page = Page (h, 2);
			Assert.That (page.RequireUnique (CrestronHomePages.Resource ("customdevice_textdisplay_firstlinetext")).Text,
				Is.EqualTo (_expected.ConnectionLabel).IgnoreCase);
			page.RequireAbsent (Text ("Storm Watch"));
			RequireControl (page, "Backup Reserve", _expected.OperatingSettingsVisible);
			RequireControl (page, "Operation Mode", _expected.OperatingSettingsVisible);
			RequireControl (page, "Charge From Grid", _expected.GridSettingsVisible);
			RequireControl (page, "Grid Export", _expected.GridSettingsVisible);
			}, deadline.Token);
		}

	/// <summary>Restores navigation after each case, including assertion failures.</summary>
	/// <returns>Completion only after the original Home is observed.</returns>
	[TearDown]
	public async Task RestoreAsync ()
		{
		if (_session == null) return;
		using var deadline = new CancellationTokenSource (TimeSpan.FromMinutes (4));
		if (_pendingDestination != null) await WaitAsync (_pendingDestination, deadline.Token);
		_pendingDestination = null;
		while (_depth > 0)
			{
			int current = _depth;
			Action<AndroidHierarchy> destination = current == 1 ? Home : h => Page (h, current - 1);
			await NavigateAsync (h => Page (h, current).RequireUnique (CrestronHomePages.Resource ("customdevices_toolbarClose")),
				h => Page (h, current), destination, current - 1, deadline.Token);
			}
		Home (await Session.Device.CaptureAsync (deadline.Token));
		}

	/// <summary>Records whether navigation was restored and releases only this workflow session.</summary>
	/// <returns>The final read-only Home verification operation.</returns>
	[OneTimeTearDown]
	public async Task CompleteAsync ()
		{
		if (_session == null) return;
		bool restored = false;
		try
			{
			if (_pendingDestination != null || _depth != 0) throw new InvalidOperationException ("Navigation restoration is unconfirmed.");
			using var deadline = new CancellationTokenSource (TimeSpan.FromSeconds (90));
			await Session.CaptureAsync ("powerwall-final-home", Home, deadline.Token);
			restored = true;
			}
		finally { Session.Complete (restorationConfirmed: restored); }
		}

	private async Task OpenMainAsync (CancellationToken token)
		{
		var tile = Text (_expected.TileName) with { AncestorResourceId = CrestronHomePages.ResourcePrefix + "fragmentHomeContainer" };
		await NavigateAsync (h =>
			{
			var target = h.RequireUnique (tile);
			if (target.ResourceId != CrestronHomePages.ResourcePrefix + "titleSubtitle_title") throw new InvalidDataException ("Expected a Home tile.");
			return target;
			}, Home, h => Page (h, 1), 1, token);
		}

	private async Task NavigateAsync (Func<AndroidHierarchy, AndroidElement> selector, Action<AndroidHierarchy> guard,
		Action<AndroidHierarchy> destination, int depth, CancellationToken token)
		{
		if (_pendingDestination != null) throw new InvalidOperationException ("An earlier input remains unconfirmed; no input was repeated.");
		AndroidWorkflowSession.VerifyContext (Session.Context);
		var hierarchy = await Session.Device.CaptureAsync (token);
		guard (hierarchy);
		var target = selector (hierarchy);
		if (!target.Enabled) throw new InvalidOperationException ("The navigation target is disabled; no input was sent.");
		var profile = Session.Context.Profile;
		var transport = new AdbCommandTransport (profile.AdbExecutable, profile.DeviceSerial, TimeSpan.FromSeconds (25));
		_pendingDestination = destination;
		_depth = depth;
		await transport.ExecuteAsync (["shell", "input", "tap",
			((target.Left + target.Right) / 2).ToString (System.Globalization.CultureInfo.InvariantCulture),
			((target.Top + target.Bottom) / 2).ToString (System.Globalization.CultureInfo.InvariantCulture)], token);
		await WaitAsync (destination, token);
		_pendingDestination = null;
		}

	private async Task WaitAsync (Action<AndroidHierarchy> verify, CancellationToken token)
		{
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource (token);
		deadline.CancelAfter (TimeSpan.FromSeconds (90));
		while (true)
			{
			AndroidWorkflowSession.VerifyContext (Session.Context);
			try { verify (await Session.Device.CaptureAsync (deadline.Token)); return; }
			catch (Exception ex) when (ex is IOException or InvalidOperationException)
				{ await Task.Delay (250, deadline.Token); }
			}
		}

	private static string ReadRow (AndroidHierarchy page, string label)
		{
		var document = XDocument.Parse (page.MaskedXml);
		var title = document.Descendants ("node").Single (n => (string?)n.Attribute ("text") == label
			&& (string?)n.Attribute ("resource-id") == CrestronHomePages.ResourcePrefix + "titleSubtitle_title");
		return (string?)title.Parent!.Elements ("node").Single (n =>
			(string?)n.Attribute ("resource-id") == CrestronHomePages.ResourcePrefix + "titleSubtitle_subtitle").Attribute ("text") ?? "";
		}

	private static void RequireControl (AndroidHierarchy page, string label, bool visible)
		{
		var document = XDocument.Parse (page.MaskedXml);
		var headings = document.Descendants ("node").Where (n =>
			string.Equals ((string?)n.Attribute ("text"), label, StringComparison.OrdinalIgnoreCase)).ToArray ();
		if (!visible) { Assert.That (headings, Is.Empty, label); return; }
		Assert.That (headings, Has.Length.EqualTo (1), label);
		var heading = headings.Single ();
		string Id (XElement node) => (string?)node.Attribute ("resource-id") ?? "";
		bool Interactive (XElement node) => (string?)node.Attribute ("clickable") == "true"
			|| (string?)node.Attribute ("long-clickable") == "true" || (string?)node.Attribute ("checkable") == "true";
		if (label is "Operation Mode" or "Grid Export")
			{
			var selector = heading.Ancestors ().Single (n => Id (n) == CrestronHomePages.ResourcePrefix + "customdevice_selectorButton_root");
			// Crestron leaves selector labels enabled but removes interaction from the disabled row.
			Assert.That (selector.DescendantsAndSelf ().Any (Interactive), Is.False, label + " must not offer a selection action.");
			return;
			}
		XElement row;
		XElement[] controls;
		if (label == "Backup Reserve")
			{
			Assert.That (Id (heading), Is.EqualTo (CrestronHomePages.ResourcePrefix + "customdeviceraiselowerwithtext_label"));
			row = heading.Parent!;
			controls = row.Descendants ().Where (n => Id (n) == CrestronHomePages.ResourcePrefix + "customdeviceraiselowerwithtext_minus"
				|| Id (n) == CrestronHomePages.ResourcePrefix + "customdeviceraiselowerwithtext_plus").ToArray ();
			Assert.That (controls, Has.Length.EqualTo (2));
			}
		else
			{
			row = heading.Ancestors ().Single (n => Id (n) == CrestronHomePages.ResourcePrefix + "customdevicetoggle_layout");
			controls = row.DescendantsAndSelf ().Where (Interactive).ToArray ();
			Assert.That (controls, Is.Not.Empty, label);
			}
		Assert.That (controls.All (n => n.AncestorsAndSelf ().TakeWhile (a => a != row.Parent)
			.Any (a => (string?)a.Attribute ("enabled") == "false")), Is.True, label + " must be disabled.");
		}
	}