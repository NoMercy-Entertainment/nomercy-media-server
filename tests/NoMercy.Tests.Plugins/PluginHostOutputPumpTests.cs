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

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk.OutOfProcess;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A plugin host's output is redirected. Unless the server reads it, the
/// plugin's log lines are lost and the host blocks once the pipe is full.
/// </summary>
[Trait("Category", "Unit")]
public class PluginHostOutputPumpTests
{
    private static readonly Ulid Plugin = Ulid.NewUlid();

    [Fact]
    public async Task EveryStdoutLine_ReachesTheServerLog_AsInformation()
    {
        ListLogger logger = new();

        await new PluginHostOutputPump(Plugin, logger).DrainAsync(
            new StringReader("first\nsecond\n"),
            new StringReader(string.Empty)
        );

        logger
            .Entries.Should()
            .Equal(
                (LogLevel.Information, $"Plugin {Plugin}: first"),
                (LogLevel.Information, $"Plugin {Plugin}: second")
            );
    }

    /// <summary>
    /// Nothing the host writes on purpose goes to stderr. What does is the
    /// runtime reporting a crash, which is the line an owner needs to find.
    /// </summary>
    [Fact]
    public async Task AStderrLine_IsLoggedAsAnError()
    {
        ListLogger logger = new();

        await new PluginHostOutputPump(Plugin, logger).DrainAsync(
            new StringReader(string.Empty),
            new StringReader("Unhandled exception. System.Exception: boom")
        );

        logger
            .Entries.Should()
            .Equal(
                (LogLevel.Error, $"Plugin {Plugin}: Unhandled exception. System.Exception: boom")
            );
    }

    [Fact]
    public async Task BlankLines_AreNotLogged()
    {
        ListLogger logger = new();

        await new PluginHostOutputPump(Plugin, logger).DrainAsync(
            new StringReader("\n   \nkept\n\n"),
            new StringReader("\n")
        );

        logger.Entries.Should().Equal((LogLevel.Information, $"Plugin {Plugin}: kept"));
    }

    /// <summary>
    /// The pipe is read to its end, however much the plugin wrote. Stopping
    /// early is the same as not reading at all once the buffer fills.
    /// </summary>
    [Fact]
    public async Task BothStreams_AreReadToTheEnd()
    {
        ListLogger logger = new();
        string stdout = string.Join('\n', Enumerable.Range(0, 5_000).Select(i => $"out {i}"));
        string stderr = string.Join('\n', Enumerable.Range(0, 5_000).Select(i => $"err {i}"));

        await new PluginHostOutputPump(Plugin, logger).DrainAsync(
            new StringReader(stdout),
            new StringReader(stderr)
        );

        logger.Entries.Count(entry => entry.Level == LogLevel.Information).Should().Be(5_000);
        logger.Entries.Count(entry => entry.Level == LogLevel.Error).Should().Be(5_000);
    }

    /// <summary>
    /// A process that is killed closes its pipes under the reader. That ends
    /// the read, and must not surface as a failure of whoever stopped it.
    /// </summary>
    [Fact]
    public async Task AStreamThatBreaksMidRead_EndsTheDrainQuietly_KeepingWhatWasRead()
    {
        ListLogger logger = new();

        Func<Task> drain = () =>
            new PluginHostOutputPump(Plugin, logger).DrainAsync(
                new BreakingReader("before the break"),
                new StringReader(string.Empty)
            );

        await drain.Should().NotThrowAsync();
        logger.Entries.Should().Equal((LogLevel.Information, $"Plugin {Plugin}: before the break"));
    }

    private sealed class BreakingReader(string firstLine) : TextReader
    {
        private bool _served;

        public override Task<string?> ReadLineAsync()
        {
            if (_served)
                throw new IOException("The pipe has been ended.");

            _served = true;
            return Task.FromResult<string?>(firstLine);
        }
    }
}
