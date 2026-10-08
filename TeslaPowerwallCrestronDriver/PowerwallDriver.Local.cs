// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Threading;
using System.Threading.Tasks;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;
using TeslaPowerwallLibrary;

namespace TeslaPowerwall.CrestronDriver;

/// <content>Connection capabilities and optional cloud history for local monitoring.</content>
public sealed partial class TeslaPowerwallDriver
	{
	/// <summary>Gets whether both reserve and operating mode were reported by the device.</summary>
	[EntityProperty (Id = "operatingSettingsVisible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool OperatingSettingsVisible
		{
		get;
		private set => SetAndNotify ("operatingSettingsVisible", value, ref field);
		}

	/// <summary>Gets whether the selected connection permits setting changes.</summary>
	[EntityProperty (Id = "controlsEnabled")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool ControlsEnabled
		{
		get;
		private set => SetAndNotify ("controlsEnabled", value, ref field);
		}

	/// <summary>Gets whether grid charge and export settings are available.</summary>
	[EntityProperty (Id = "gridSettingsVisible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool GridSettingsVisible
		{
		get;
		private set => SetAndNotify ("gridSettingsVisible", value, ref field);
		}

	/// <summary>Gets whether an explicit cloud connection is available for historical data.</summary>
	[EntityProperty (Id = "historyVisible")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool HistoryVisible
		{
		get;
		private set => SetAndNotify ("historyVisible", value, ref field);
		}

	/// <summary>Gets the configured connection endpoint and polling delay.</summary>
	[EntityProperty (Id = "connectionDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ConnectionDisplay
		{
		get;
		private set => SetAndNotify ("connectionDisplay", value, ref field);
		}

	private string HistorySelection () => string.Join ("|", EnergyPeriod, EnergyType, ImpactPeriod,
		(_energyPeriodIsCurrent ? DateTimeOffset.Now : _energyPeriodAnchorDate).Date.ToString ("yyyy-MM-dd"),
		(_impactPeriodIsCurrent ? DateTimeOffset.Now : _impactPeriodAnchorDate).Date.ToString ("yyyy-MM-dd"));

	private async Task RefreshHistoryIfDueAsync (Powerwall client, CancellationToken cancellationToken)
		{
		if (!HasHistory)
			{
			EnergyContentEnabled = false;
			ImpactContentEnabled = false;
			return;
			}
		string selection = HistorySelection ();
		if (selection == _lastHistorySelection && DateTimeOffset.UtcNow < _nextHistoryRefresh)
			{
			return;
			}
		try
			{
			Powerwall history = client;
			if (IsLocal)
				{
				lock (_stateLock)
					{
					EnsureCurrentClient (client, cancellationToken);
					_historyClient ??= CreateCloudClient (_siteId);
					history = _historyClient;
					}
				if (!history.IsClientConnected)
					{
					if (!await history.ConnectAsync (cancellationToken).ConfigureAwait (false))
						{
						throw new PowerwallException ("Cloud history connection is unavailable.");
						}
					EnsureCurrentClient (history, cancellationToken);
					}
				}
			await RefreshEnergyAndImpactAsync (history, cancellationToken).ConfigureAwait (false);
			EnsureCurrentClient (client, cancellationToken);
			}
		catch (PowerwallException)
			{
			lock (_stateLock)
				{
				EnsureCurrentClient (client, cancellationToken);
				EnergyValueDisplay = "--";
				EnergyDetailDisplay = "--";
				EnergySummaryDisplay = "Cloud history unavailable";
				EnergyBreakdownVisible = false;
				ImpactSummaryDisplay = "Cloud history unavailable";
				ImpactHomeUsageDisplay = "--";
				ImpactGridUsageDisplay = "--";
				EnergyContentEnabled = true;
				ImpactContentEnabled = false;
				LogWarning ("Cloud history unavailable; local monitoring continues.");
				}
			}
		lock (_stateLock)
			{
			EnsureCurrentClient (client, cancellationToken);
			_lastHistorySelection = selection;
			_nextHistoryRefresh = DateTimeOffset.UtcNow.AddSeconds (_refreshIntervalSeconds);
			}
		}
	}
