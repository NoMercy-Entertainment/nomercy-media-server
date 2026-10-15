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
using NoMercy.PluginSdk.Ipc;
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
    [Fact]
    public async Task EveryPlainStdoutLine_ReachesTheServerLog_AsInformation()
    {
        ListLogger logger = new();

        await new PluginHostOutputPump(logger).DrainAsync(
            new StringReader("first\nsecond\n"),
            new StringReader(string.Empty)
        );

        logger
            .Entries.Should()
            .Equal((LogLevel.Information, "first"), (LogLevel.Information, "second"));
    }

    /// <summary>
    /// Nothing the host writes on purpose goes to stderr. What does is the
    /// runtime reporting a crash, which is the line an owner needs to find.
    /// </summary>
    [Fact]
    public async Task APlainStderrLine_IsLoggedAsAnError()
    {
        ListLogger logger = new();

        await new PluginHostOutputPump(logger).DrainAsync(
            new StringReader(string.Empty),
            new StringReader("Unhandled exception. System.Exception: boom")
        );

        logger
            .Entries.Should()
            .Equal((LogLevel.Error, "Unhandled exception. System.Exception: boom"));
    }

    [Fact]
    public async Task AnEntry_IsLoggedAtItsOwnLevel_WithItsMessageWhole()
    {
        ListLogger logger = new();
        string stdout = new PluginHostLogLine(
            LogLevel.Warning,
            "Tracker unreachable\nretrying in 30 s",
            null
        ).Format();

        await new PluginHostOutputPump(logger).DrainAsync(
            new StringReader(stdout),
            new StringReader(string.Empty)
        );

        logger.Entries.Should().Equal((LogLevel.Warning, "Tracker unreachable\nretrying in 30 s"));
    }

    /// <summary>
    /// The exception object stayed in the plugin's process. Its text arrives
    /// as the exception of the line, stack trace and all.
    /// </summary>
    [Fact]
    public async Task AnEntryWithAnException_CarriesTheExceptionsText()
    {
        CapturingLogger logger = new();
        string remote =
            "System.InvalidOperationException: no peers\n   at Torrent.Swarm.Join() in Swarm.cs:line 12";
        string stdout = new PluginHostLogLine(LogLevel.Error, "Join failed", remote).Format();

        await new PluginHostOutputPump(logger).DrainAsync(
            new StringReader(stdout),
            new StringReader(string.Empty)
        );

        logger.Exception.Should().BeOfType<PluginHostException>();
        logger.Exception!.ToString().Should().Be(remote);
        logger.Exception.Message.Should().Be("System.InvalidOperationException: no peers");
    }

    /// <summary>A line that claims to be an entry and is not is still not dropped.</summary>
    [Fact]
    public async Task AMarkedLineThatIsNotAnEntry_IsLoggedAsText()
    {
        ListLogger logger = new();
        string broken = PluginHostLogLine.Marker + "{not json";

        await new PluginHostOutputPump(logger).DrainAsync(
            new StringReader(broken),
            new StringReader(string.Empty)
        );

        logger.Entries.Should().Equal((LogLevel.Information, broken));
    }

    [Fact]
    public async Task BlankLines_AreNotLogged()
    {
        ListLogger logger = new();

        await new PluginHostOutputPump(logger).DrainAsync(
            new StringReader("\n   \nkept\n\n"),
            new StringReader("\n")
        );

        logger.Entries.Should().Equal((LogLevel.Information, "kept"));
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

        await new PluginHostOutputPump(logger).DrainAsync(
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
            new PluginHostOutputPump(logger).DrainAsync(
                new BreakingReader("before the break"),
                new StringReader(string.Empty)
            );

        await drain.Should().NotThrowAsync();
        logger.Entries.Should().Equal((LogLevel.Information, "before the break"));
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

    private sealed class CapturingLogger : ILogger
    {
        public Exception? Exception { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Exception = exception;
    }
}
