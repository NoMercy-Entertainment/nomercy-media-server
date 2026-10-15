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

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// Reads what a plugin host writes to stdout and stderr into the server log,
/// one line at a time, until the process closes them.
/// <para>
/// Redirected and never read, both pipes fill up and the host blocks on its
/// next write: a plugin that logs a lot stalls, and a crash's stack trace is
/// lost. Both streams are read at once for the same reason. Reading one to
/// the end before the other leaves the other free to fill.
/// </para>
/// <para>
/// The logger is the plugin's own, so every line carries the plugin's name.
/// A <see cref="PluginHostLogLine" /> is logged at its own level, with its
/// exception. Any other line is text: on stdout the plugin talking, on
/// stderr the host or the runtime saying something went wrong, so an error.
/// </para>
/// </summary>
public sealed class PluginHostOutputPump(ILogger logger)
{
    public Task DrainAsync(TextReader stdout, TextReader stderr) =>
        Task.WhenAll(
            Task.Run(() => DrainAsync(stdout, LogLevel.Information)),
            Task.Run(() => DrainAsync(stderr, LogLevel.Error))
        );

    private async Task DrainAsync(TextReader reader, LogLevel textLevel)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (PluginHostLogLine.TryParse(line, out PluginHostLogLine? entry))
                    Log(entry);
                else
                    logger.Log(textLevel, "{Line}", [line]);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The process went away mid-line. What it wrote before that is
            // already logged, and there is nothing left to read.
        }
    }

    private void Log(PluginHostLogLine entry)
    {
        PluginHostException? exception = entry.Exception is null
            ? null
            : new PluginHostException(entry.Exception);

        logger.Log(entry.Level, exception, "{Message}", [entry.Message]);
    }
}
