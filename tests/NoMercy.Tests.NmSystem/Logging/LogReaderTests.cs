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

using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Logging;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.NmSystem;

/// <summary>
/// Pins <see cref="LogReader"/> against the dashboard's actual bug: the server
/// writes logs through two independent sinks — the legacy rolling <c>log*.txt</c>
/// files (the static <see cref="NoMercy.NmSystem.SystemCalls.Logger"/> API) and
/// the per-run <c>run-*.jsonl</c> files (every <c>ILogger&lt;T&gt;</c> call site,
/// the majority of the codebase). <see cref="LogReader.GetLogsAsync"/> is the
/// data source behind both the dashboard's <c>GET /dashboard/logs</c> search and
/// the CLI's <c>manage/logs</c> — it must read and filter both formats, and must
/// not double-count an entry the legacy bridge mirrors into both.
/// </summary>
[Trait("Category", "Unit")]
public class LogReaderTests
{
    private static IStorage BuildStorage(string root)
    {
        LocalStorageDriver driver = new();
        return new LocalStorage(driver, new([], driver));
    }

    private static string WriteLegacyTextLine(string dir, string type, string level, string message)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "log20260101.txt");
        string line =
            $$"""{"@t":"2026-01-01T10:00:00.0000000Z","Type":"{{type}}","Level":"{{level}}","Message":"{{message}}","ThreadId":1}""";
        File.AppendAllText(path, line + Environment.NewLine);
        return path;
    }

    private static string WriteRunJsonlLine(
        string dir,
        string type,
        string level,
        string message,
        string timestamp = "2026-01-01T11:00:00.0000000Z"
    )
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "run-20260101-110000-1.jsonl");
        string line =
            $$"""{"@t":"{{timestamp}}","Type":"{{type}}","Category":"Cat","Group":"Grp","Level":"{{level}}","LevelValue":2,"Color":"#fff","Message":"{{message}}","Scope":null,"Source":"{{type}}","ThreadId":1,"Exception":null}""";
        File.AppendAllText(path, line + Environment.NewLine);
        return path;
    }

    [Fact]
    public async Task GetLogsAsync_ReadsEntriesFromLegacyTextFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nm-logreader-{Guid.NewGuid():N}");
        try
        {
            WriteLegacyTextLine(dir, "app", "Information", "hello from legacy log.txt");

            List<LogEntry> logs = await LogReader.GetLogsAsync(BuildStorage(dir), dir);

            logs.Should().ContainSingle(e => e.Message == "hello from legacy log.txt");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GetLogsAsync_ReadsEntriesFromRunJsonlFiles()
    {
        // This is the actual bug: before the fix, GetLogsAsync only globbed
        // "*.txt" and never saw anything written through the ILogger<T> sink.
        string dir = Path.Combine(Path.GetTempPath(), $"nm-logreader-{Guid.NewGuid():N}");
        try
        {
            WriteRunJsonlLine(dir, "access", "Information", "user fillz authenticated");

            List<LogEntry> logs = await LogReader.GetLogsAsync(BuildStorage(dir), dir);

            logs.Should().ContainSingle(e => e.Message == "user fillz authenticated");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GetLogsAsync_FilterMatchesRenderedMessage_AcrossBothFormats()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nm-logreader-{Guid.NewGuid():N}");
        try
        {
            WriteLegacyTextLine(dir, "app", "Information", "nothing interesting here");
            WriteRunJsonlLine(dir, "access", "Information", "stoney signed in from a new device");

            List<LogEntry> matches = await LogReader.GetLogsAsync(
                BuildStorage(dir),
                dir,
                entry => entry.Message.Contains("stoney", StringComparison.OrdinalIgnoreCase)
            );

            matches.Should().ContainSingle();
            matches[0].Message.Should().Be("stoney signed in from a new device");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GetLogsAsync_DedupesEntryMirroredIntoBothSinksByLegacyBridge()
    {
        // NoMercyLoggerProvider's legacy bridge writes the SAME LogEntry (same
        // type/level/message/second) to both log.txt (via Logger.Log's own
        // Serilog sink) and the current run's jsonl file. Without dedup this
        // shows up twice in the dashboard for every legacy Logger.* call made
        // while a run file is bridged.
        string dir = Path.Combine(Path.GetTempPath(), $"nm-logreader-{Guid.NewGuid():N}");
        try
        {
            WriteLegacyTextLine(dir, "app", "Information", "bridged message");
            WriteRunJsonlLine(
                dir,
                "app",
                "Information",
                "bridged message",
                timestamp: "2026-01-01T10:00:00.1234567Z"
            );

            List<LogEntry> logs = await LogReader.GetLogsAsync(BuildStorage(dir), dir);

            logs.Should().ContainSingle(e => e.Message == "bridged message");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GetLogsAsync_KeepsDistinctEntriesWithDifferentMessages()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nm-logreader-{Guid.NewGuid():N}");
        try
        {
            WriteLegacyTextLine(dir, "app", "Information", "first message");
            WriteRunJsonlLine(dir, "access", "Information", "second message");

            List<LogEntry> logs = await LogReader.GetLogsAsync(BuildStorage(dir), dir);

            logs.Select(e => e.Message).Should().BeEquivalentTo("first message", "second message");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}
