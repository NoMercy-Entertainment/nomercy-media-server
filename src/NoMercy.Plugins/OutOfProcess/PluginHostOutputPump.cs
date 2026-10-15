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
/// A stdout line is the plugin talking. A stderr line is the host or the
/// runtime saying something went wrong, so it is logged as an error.
/// </para>
/// </summary>
public sealed class PluginHostOutputPump(Ulid pluginId, ILogger logger)
{
    public Task DrainAsync(TextReader stdout, TextReader stderr) =>
        Task.WhenAll(
            Task.Run(() => DrainAsync(stdout, LogLevel.Information)),
            Task.Run(() => DrainAsync(stderr, LogLevel.Error))
        );

    private async Task DrainAsync(TextReader reader, LogLevel level)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                logger.Log(level, "Plugin {PluginId}: {Line}", [pluginId, line]);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The process went away mid-line. What it wrote before that is
            // already logged, and there is nothing left to read.
        }
    }
}
