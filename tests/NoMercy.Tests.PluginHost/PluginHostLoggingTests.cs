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

using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Information;
using NoMercy.PluginHost;
using NoMercy.PluginSdk.Ipc;
using NoMercy.PluginSdk.OutOfProcess;
using Xunit;

namespace NoMercy.Tests.PluginHost;

/// <summary>
/// A plugin's log line on its way from its own process into the server log:
/// written by the host as one entry, read by the server at the same level,
/// with the same message and exception, under the plugin's name.
/// </summary>
[Trait("Category", "Unit")]
public class PluginHostLoggingTests
{
    [Fact]
    public void TheHostWritesOneReadableEntryPerLogCall()
    {
        StringWriter stdout = new();
        ILogger logger = new PluginHostLoggerProvider(stdout).CreateLogger("plugin");

        logger.LogInformation("Seeding {Count} torrents", 3);
        logger.LogError(new InvalidOperationException("no peers"), "Join failed");

        string[] lines = stdout
            .ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);

        PluginHostLogLine.TryParse(lines[0], out PluginHostLogLine? info).Should().BeTrue();
        info.Should().Be(new PluginHostLogLine(LogLevel.Information, "Seeding 3 torrents", null));

        PluginHostLogLine.TryParse(lines[1], out PluginHostLogLine? error).Should().BeTrue();
        error!.Level.Should().Be(LogLevel.Error);
        error.Message.Should().Be("Join failed");
        error.Exception.Should().StartWith("System.InvalidOperationException: no peers");
    }

    /// <summary>
    /// A plugin logs from many threads. Two entries written at once must not
    /// run into each other, or the server can read neither.
    /// </summary>
    [Fact]
    public void EntriesFromManyThreads_StayWholeLines()
    {
        StringWriter stdout = new();
        ILogger logger = new PluginHostLoggerProvider(stdout).CreateLogger("plugin");

        Parallel.For(0, 2_000, i => logger.LogInformation("entry {Index}", i));

        string[] lines = stdout
            .ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2_000);
        lines.Where(line => !PluginHostLogLine.TryParse(line, out _)).Should().BeEmpty();
    }

    /// <summary>
    /// Once the server has closed its end of the pipe there is nobody left to
    /// read a line. Logging must still never fail the plugin code that called it.
    /// </summary>
    [Fact]
    public void AClosedPipe_NeverFailsTheCaller()
    {
        StringWriter stdout = new();
        ILogger logger = new PluginHostLoggerProvider(stdout).CreateLogger("plugin");
        stdout.Dispose();

        Action log = () => logger.LogWarning("after the server left");

        log.Should().NotThrow();
    }

    [Fact]
    public async Task WhatTheHostWrites_ReachesTheServerLogAsTheSameEntry()
    {
        StringWriter stdout = new();
        ILogger hostSide = new PluginHostLoggerProvider(stdout).CreateLogger("plugin");
        hostSide.LogWarning("Tracker unreachable\nretrying in {Seconds} s", 30);
        hostSide.LogError(new InvalidOperationException("no peers"), "Join failed");

        CapturingLogger serverSide = new();
        await new PluginHostOutputPump(serverSide).DrainAsync(
            new StringReader(stdout.ToString()),
            new StringReader(string.Empty)
        );

        serverSide.Entries.Should().HaveCount(2);
        serverSide
            .Entries[0]
            .Should()
            .Be((LogLevel.Warning, "Tracker unreachable\nretrying in 30 s", null));
        serverSide.Entries[1].Level.Should().Be(LogLevel.Error);
        serverSide.Entries[1].Message.Should().Be("Join failed");
        serverSide
            .Entries[1]
            .Exception.Should()
            .StartWith("System.InvalidOperationException: no peers");
    }

    /// <summary>
    /// The real host, started the way the launcher starts it, with nothing to
    /// tell it which plugin it is. It says so on stderr and exits. Before the
    /// server read the host's output, that line went nowhere.
    /// </summary>
    [Fact]
    public async Task TheRealHostsRefusal_ReachesTheServerLogAsAnError()
    {
        ProcessStartInfo start = new()
        {
            FileName = Path.Combine(
                AppContext.BaseDirectory,
                Software.IsWindows ? "NoMercy.PluginHost.exe" : "NoMercy.PluginHost"
            ),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string name in PluginChannelVariables())
            start.Environment.Remove(name);

        using Process host = Process.Start(start)!;
        CapturingLogger serverSide = new();

        await new PluginHostOutputPump(serverSide)
            .DrainAsync(host.StandardOutput, host.StandardError)
            .WaitAsync(TimeSpan.FromSeconds(30));
        await host.WaitForExitAsync();

        host.ExitCode.Should().Be(78);
        serverSide
            .Entries.Should()
            .ContainSingle(entry =>
                entry.Level == LogLevel.Error && entry.Message.StartsWith("PLUGIN_HOST_UNAVAILABLE")
            );
    }

    private static string[] PluginChannelVariables() =>
        [
            PluginChannelEnvironment.PluginId,
            PluginChannelEnvironment.AssemblyPath,
            PluginChannelEnvironment.DataFolder,
            PluginChannelEnvironment.BrokerEndpoint,
            PluginChannelEnvironment.HostEndpoint,
            PluginChannelEnvironment.Token,
        ];

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, string? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception), exception?.ToString()));
        }
    }
}
