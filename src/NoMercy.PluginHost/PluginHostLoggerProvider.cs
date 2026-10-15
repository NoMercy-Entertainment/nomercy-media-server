// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk.Ipc;

namespace NoMercy.PluginHost;

/// <summary>
/// Writes every log entry of this process to stdout as one
/// <see cref="PluginHostLogLine" />.
/// <para>
/// The server reads this process's stdout into its own log, under the
/// plugin's name. That covers the plugin's lines and the host's own, which
/// are about the same plugin. Writing to stdout rather than calling the
/// server means a log line still arrives when the channel to the server is
/// the thing that broke.
/// </para>
/// </summary>
public sealed class PluginHostLoggerProvider(TextWriter output) : ILoggerProvider
{
    private readonly Lock _gate = new();

    public ILogger CreateLogger(string categoryName) => new PluginHostLogger(this);

    public void Dispose() { }

    private void Write(PluginHostLogLine line)
    {
        // One entry is one line. Two threads writing at once must not
        // interleave inside a line, or the server cannot read either.
        lock (_gate)
        {
            try
            {
                output.WriteLine(line.Format());
                output.Flush();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // The server closed its end. Nobody is left to read the line,
                // and a log call must never fail the plugin code that made it.
            }
        }
    }

    private sealed class PluginHostLogger(PluginHostLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (!IsEnabled(logLevel))
                return;

            string message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null)
                return;

            provider.Write(new(logLevel, message, exception?.ToString()));
        }
    }
}
