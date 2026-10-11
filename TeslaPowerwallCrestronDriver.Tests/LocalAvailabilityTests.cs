// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TeslaPowerwall.CrestronDriver;
namespace TeslaPowerwallCrestronDriver.Tests;
[TestFixture, Category ("Offline")]
public sealed class LocalAvailabilityTests
	{
	[Test]
	public async Task SuccessfulProbeClosesOwnedSocketAndCancelsDeadline ()
		{
		int closed = 0;
		Assert.That (await TeslaPowerwallDriver.AwaitLocalConnectionAsync (Task.CompletedTask, () => closed++, TimeSpan.FromSeconds (3), CancellationToken.None), Is.True);
		Assert.That (closed, Is.EqualTo (1));
		}
	[Test]
	public async Task TimeoutIsBoundedAndClosesEvenAnUnfinishedConnect ()
		{
		var pending = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		int closed = 0;
		var time = Stopwatch.StartNew ();
		Assert.That (await TeslaPowerwallDriver.AwaitLocalConnectionAsync (pending.Task, () => closed++, TimeSpan.FromMilliseconds (40), CancellationToken.None), Is.False);
		Assert.That (time.Elapsed, Is.LessThan (TimeSpan.FromSeconds (2)));
		Assert.That (closed, Is.EqualTo (1));
		pending.SetException (new SocketException ());
		}
	[Test]
	public async Task CancellationClosesPendingSocketWithoutPublishingAvailability ()
		{
		var pending = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		using var cts = new CancellationTokenSource ();
		int closed = 0;
		var probe = TeslaPowerwallDriver.AwaitLocalConnectionAsync (pending.Task, () => closed++, TimeSpan.FromSeconds (3), cts.Token);
		cts.Cancel ();
		await Assert.ThrowsAsync<OperationCanceledException> (async () => await probe);
		Assert.That (closed, Is.GreaterThanOrEqualTo (1));
		pending.SetException (new ObjectDisposedException ("synthetic socket"));
		await Task.CompletedTask;
		}
	[Test]
	public async Task RefusedTransportIsNotSuccess ()
		{
		int closed = 0;
		Assert.That (await TeslaPowerwallDriver.AwaitLocalConnectionAsync (Task.FromException (new SocketException (10061)), () => closed++, TimeSpan.FromSeconds (3), CancellationToken.None), Is.False);
		Assert.That (closed, Is.EqualTo (1));
		}
	[Test]
	public async Task ProbeUsesConfiguredPortAgainstLoopbackOnly ()
		{
		var listener = new TcpListener (IPAddress.Loopback, 0);
		listener.Start ();
		try
			{
			int port = ((IPEndPoint)listener.LocalEndpoint).Port;
			var accept = listener.AcceptTcpClientAsync ();
			Assert.That (await TeslaPowerwallDriver.LocalTransportProbeAsync (new Uri ("https://127.0.0.1:" + port), TimeSpan.FromSeconds (3), CancellationToken.None), Is.True);
			using var accepted = await accept;
			}
		finally { listener.Stop (); }
		}
	}
