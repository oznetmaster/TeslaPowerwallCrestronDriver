// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE.
using System;
using System.Collections.Generic;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using TeslaPowerwall.CrestronDriver;

namespace TeslaPowerwallCrestronDriver.Tests;

[TestFixture]
public sealed class LibraryLoggerTests
    {
    [TestCase (LogLevel.Trace, LogEntryLevel.Trace)]
    [TestCase (LogLevel.Debug, LogEntryLevel.Debug)]
    [TestCase (LogLevel.Information, LogEntryLevel.Info)]
    [TestCase (LogLevel.Warning, LogEntryLevel.Warning)]
    [TestCase (LogLevel.Error, LogEntryLevel.Error)]
    [TestCase (LogLevel.Critical, LogEntryLevel.Error)]
    public void LibraryLevels_UseCrestronFilteringAndSeverity (LogLevel level, LogEntryLevel expected)
        {
        LogEntryLevel observed = LogEntryLevel.Off;
        string text = null;
        var logger = new LibraryLogger (entry => entry == expected, (entry, value) => { observed = entry; text = value; });
        logger.Log (level, new EventId (7), "message", null, (state, _) => state);
        Assert.That (observed, Is.EqualTo (expected));
        Assert.That (text, Is.EqualTo ("Powerwall[7]: message"));
        }

    [Test]
    public void DisabledLevels_DoNotFormatOrWrite ()
        {
        var logger = new LibraryLogger (_ => false, (_, _) => Assert.Fail ("Unexpected log write."));
        logger.Log (LogLevel.Error, new EventId (1), "secret", null, (_, _) => throw new InvalidOperationException ("Must not format."));
        Assert.That (logger.IsEnabled (LogLevel.None), Is.False);
        }

    [Test]
    public void Scopes_RemainPerDriverAndAreRemovedOnDispose ()
        {
        var first = new List<string>();
        var second = new List<string>();
        var a = new LibraryLogger (_ => true, (_, message) => first.Add (message));
        var b = new LibraryLogger (_ => true, (_, message) => second.Add (message));
        using (a.BeginScope ("device-a"))
            {
            a.LogInformation ("one");
            b.LogInformation ("two");
            }
        a.LogInformation ("three");
        Assert.That (first[0], Does.Contain ("[device-a]"));
        Assert.That (first[1], Does.Not.Contain ("device-a"));
        Assert.That (second[0], Does.Not.Contain ("device-a"));
        }
    }
