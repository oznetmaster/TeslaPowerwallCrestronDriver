// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TeslaPowerwall.CrestronDriver;

public sealed partial class TeslaPowerwallDriver
	{
	// Independent of the installer's data/cache cadence and the full API/history poll.
	// A complete local transport loss is noticed in at most one probe plus one delay
	// (nominally five seconds, excluding runtime scheduling/UI propagation).
	internal static readonly TimeSpan LocalAvailabilityDelay = TimeSpan.FromSeconds (2);
	internal static readonly TimeSpan LocalAvailabilityTimeout = TimeSpan.FromSeconds (3);

	private async Task LocalAvailabilityLoopAsync (Uri endpoint, CancellationToken token)
		{
		try
			{
			while (!token.IsCancellationRequested)
				{
				bool reachable = await LocalTransportProbeAsync (endpoint, LocalAvailabilityTimeout, token).ConfigureAwait (false);
				ApplyLocalAvailability (reachable, token);
				await Task.Delay (LocalAvailabilityDelay, token).ConfigureAwait (false);
				}
			}
		catch (OperationCanceledException) when (token.IsCancellationRequested) { }
		catch (Exception ex)
			{
			ApplyLocalAvailability (false, token);
			LogError ("Local availability monitor stopped: " + ex.Message);
			}
		}

	// No credentials, TLS bypass, device API request, or control operation. Socket
	// success is transport-only and must never publish online/readiness or readings.
	internal static async Task<bool> LocalTransportProbeAsync (Uri endpoint, TimeSpan timeout, CancellationToken token)
		{
		token.ThrowIfCancellationRequested ();
		using var socket = new TcpClient ();
		Task connect = socket.ConnectAsync (endpoint.DnsSafeHost, endpoint.Port);
		return await AwaitLocalConnectionAsync (connect, () => socket.Dispose (), timeout, token).ConfigureAwait (false);
		}

	internal static async Task<bool> AwaitLocalConnectionAsync (Task connect, Action close, TimeSpan timeout, CancellationToken token)
		{
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource (token);
		_ = connect.ContinueWith (task => { _ = task.Exception; }, CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		using var cancelSocket = token.Register (close);
		try
			{
			Task expired = Task.Delay (timeout, deadline.Token);
			Task completed = await Task.WhenAny (connect, expired).ConfigureAwait (false);
			token.ThrowIfCancellationRequested ();
			if (completed != connect) return false;
			await connect.ConfigureAwait (false);
			token.ThrowIfCancellationRequested ();
			return true;
			}
		catch (SocketException) { token.ThrowIfCancellationRequested (); return false; }
		catch (ObjectDisposedException) { token.ThrowIfCancellationRequested (); return false; }
		finally { deadline.Cancel (); close (); }
		}

	private void ApplyLocalAvailability (bool reachable, CancellationToken token)
		{
		lock (_stateLock)
			{
			if (token.IsCancellationRequested || token != _localAvailabilityToken || !IsLocal) return;
			bool returned = reachable && _localTransportReachable == false;
			_localTransportReachable = reachable;
			if (!reachable)
				{
				// Fence every read started before this failure, including cached successes.
				_localAvailabilityRevision++;
				_localTransportUnavailable = true;
				SetOfflineState ("Local Powerwall connection unavailable");
				}
			else if (returned && _localTransportUnavailable && _refreshWake.CurrentCount == 0)
				{
				// Wake the normal serialized data poll. Only its uncached API observation
				// and successful data reads may restore the tile and online/readiness flags.
				_refreshWake.Release ();
				}
			}
		}
	}
