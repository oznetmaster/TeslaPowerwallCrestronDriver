// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Collections.Generic;

using NUnit.Framework;

using TeslaPowerwall.CrestronDriver;
namespace TeslaPowerwallCrestronDriver.Tests;

[TestFixture, FixtureLifeCycle (LifeCycle.InstancePerTestCase)]
public sealed class HistoryTests
	{
	private static T Call<T> (string name, params object[] args) => TestSupport.Call<T> (typeof (TeslaPowerwallDriver), name, args);
	private static object Period (string name) => Call<object> ("ParseHistoryPeriod", name);
	[TestCase (null, "Day")]
	[TestCase ("unknown", "Day")]
	[TestCase ("day", "Day")]
	[TestCase ("week", "Week")]
	[TestCase ("month", "Month")]
	[TestCase ("year", "Year")]
	[TestCase ("lifetime", "Lifetime")]
	public void PeriodSelection_DefaultsToDay (string text, string expected) => Assert.That (Period (text).ToString (), Is.EqualTo (expected));
	[TestCase ("day", 24)]
	[TestCase ("week", 7)]
	[TestCase ("month", 4)]
	[TestCase ("year", 12)]
	[TestCase ("lifetime", 0)]
	public void HistoryRows_CoverTheSelectedPeriodWithoutGaps (string period, int count)
		{
		var anchor = new DateTimeOffset (2026, 2, 12, 12, 0, 0, TimeSpan.Zero);
		var rows = Call<IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End, string Label)>> ("BuildEnergyRowSlots", Period (period), anchor);
		Assert.That (rows.Count, Is.EqualTo (count));
		for (int i = 0; i < rows.Count; i++)
			{
			Assert.That (rows[i].End, Is.GreaterThan (rows[i].Start));
			Assert.That (rows[i].Label, Is.Not.Empty);
			if (i > 0)
				Assert.That (rows[i].Start, Is.EqualTo (rows[i - 1].End));
			}
		}
	[TestCase ("day", 1)]
	[TestCase ("week", 7)]
	[TestCase ("month", 28)]
	[TestCase ("year", 365)]
	[TestCase ("lifetime", 0)]
	public void Navigation_MovesOneWholePeriod (string period, int days)
		{
		var anchor = new DateTimeOffset (2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
		var moved = Call<DateTimeOffset> ("StepPeriod", Period (period), anchor, 1);
		Assert.That (moved, Is.EqualTo (anchor.AddDays (days)));
		Assert.That (Call<DateTimeOffset> ("StepPeriod", Period (period), moved, -1), Is.EqualTo (anchor));
		}
	[TestCase ("day", false)]
	[TestCase ("week", false)]
	[TestCase ("month", false)]
	[TestCase ("year", false)]
	[TestCase ("lifetime", true)]
	public void PeriodBuckets_DoNotConfuseDifferentYears (string period, bool expected) => Assert.That (Call<bool> ("IsSamePeriodBucket", Period (period), new DateTimeOffset (2025, 2, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset (2026, 2, 1, 0, 0, 0, TimeSpan.Zero)), Is.EqualTo (expected));
	}