// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE.

using System;
using System.Text;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Microsoft.Extensions.Logging;

namespace TeslaPowerwall.CrestronDriver;

// The owning driver supplies its controller identity and existing Crestron filtering.
internal sealed class LibraryLogger : ILogger
    {
    private readonly Func<LogEntryLevel, bool> _enabled;
    private readonly Action<LogEntryLevel, string> _write;
    private readonly LoggerExternalScopeProvider _scopes = new ();

    internal LibraryLogger (Func<LogEntryLevel, bool> enabled, Action<LogEntryLevel, string> write)
        {
        _enabled = enabled ?? throw new ArgumentNullException (nameof (enabled));
        _write = write ?? throw new ArgumentNullException (nameof (write));
        }

    public IDisposable BeginScope<TState> (TState state) where TState : notnull => _scopes.Push (state);
    public bool IsEnabled (LogLevel logLevel) => logLevel >= LogLevel.Trace && logLevel <= LogLevel.Critical && _enabled (Map (logLevel));

    public void Log<TState> (LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
        if (!IsEnabled (logLevel)) return;
        if (formatter == null) throw new ArgumentNullException (nameof (formatter));
        var message = new StringBuilder ("Powerwall[").Append (eventId.Id).Append ("]: ");
        _scopes.ForEachScope ((scope, text) => text.Append ('[').Append (scope).Append ("] "), message);
        message.Append (formatter (state, exception));
        _write (Map (logLevel), message.ToString ());
        }

    private static LogEntryLevel Map (LogLevel level) => level switch
        {
        LogLevel.Trace => LogEntryLevel.Trace,
        LogLevel.Debug => LogEntryLevel.Debug,
        LogLevel.Information => LogEntryLevel.Info,
        LogLevel.Warning => LogEntryLevel.Warning,
        _ => LogEntryLevel.Error
        };
    }
