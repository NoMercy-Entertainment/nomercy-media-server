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
using NoMercy.NmSystem.Logging;

namespace NoMercy.Tests.NmSystem;

/// <summary>
/// Pins the per-run JSONL file sink and the record callback: each run writes its own
/// file with a rich, query-friendly line; old runs are pruned to MaxRunFiles; the
/// callback receives a structured record and a throwing callback never breaks logging.
/// </summary>
[Trait("Category", "Unit")]
public class NoMercyLoggerSinkTests
{
    [Fact]
    public void PerRunFile_WritesRichQueryableJsonLine()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nm-logdir-{Guid.NewGuid():N}");
        try
        {
            NoMercyLoggerOptions options = new() { Color = false, LogDirectory = dir };
            using (NoMercyLoggerProvider provider = new(options, new StringWriter()))
            {
                ILogger logger = provider.CreateLogger(
                    "NoMercy.Providers.TMDB.Client.TmdbBaseClient"
                );
                logger.LogInformation("Fetching {Id}", 27205);
            }

            string[] runs = Directory.GetFiles(dir, "run-*.jsonl");
            runs.Should().ContainSingle();

            string[] lines = File.ReadAllLines(runs[0]);
            lines.Should().ContainSingle();

            string line = lines[0];
            line.Should().Contain("\"@t\":");
            line.Should().Contain("\"Type\":\"moviedb\"");
            line.Should().Contain("\"Group\":\"Providers\"");
            line.Should().Contain("\"Level\":\"Information\"");
            line.Should().Contain("Fetching 27205");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// A plugin logs through a logger named after its category key. Its lines
    /// carry its name on the console and its key and group in the run file,
    /// which is what the dashboard filters on.
    /// </summary>
    [Fact]
    public void APluginLogger_WritesThePluginsNameKeyAndGroup()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nm-logdir-{Guid.NewGuid():N}");
        try
        {
            Ulid plugin = Ulid.NewUlid();
            LogCategories.RegisterPlugin(plugin, "Echo");
            StringWriter console = new();

            NoMercyLoggerOptions options = new() { Color = false, LogDirectory = dir };
            using (NoMercyLoggerProvider provider = new(options, console))
            {
                ILogger logger = provider.CreateLogger(LogCategories.PluginKey(plugin));
                logger.LogInformation("Echoed {Count} lines", 3);
            }

            console.ToString().Should().Contain("Echo   Echoed 3 lines");

            string line = File.ReadAllLines(Directory.GetFiles(dir, "run-*.jsonl").Single())
                .Single();
            line.Should().Contain($"\"Type\":\"plugin:{plugin}\"");
            line.Should().Contain("\"Category\":\"Echo\"");
            line.Should().Contain("\"Group\":\"Plugins\"");
            line.Should().Contain($"\"Color\":\"{LogCategories.PluginDarkHex}\"");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Write_AfterDispose_DegradesToConsoleInsteadOfThrowing()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nm-logdir-{Guid.NewGuid():N}");
        try
        {
            NoMercyLoggerOptions options = new() { Color = false, LogDirectory = dir };
            StringWriter console = new();
            NoMercyLoggerProvider provider = new(options, console);
            ILogger logger = provider.CreateLogger("NoMercy.Service.Hosting.ServerRunner");

            provider.Dispose();

            // A logger resolved from a disposed DI container (the HTTP host after
            // the first-boot HTTPS restart swap) still logs. This exact call used
            // to throw ObjectDisposedException from the closed per-run StreamWriter
            // and crash the whole process at the last step of setup.
            Exception? thrown = Record.Exception(() =>
                logger.LogInformation("HTTPS server starting...")
            );

            thrown.Should().BeNull();
            console.ToString().Should().Contain("HTTPS server starting...");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Retention_KeepsOnlyMaxRunFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nm-logdir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (
                string old in new[]
                {
                    "run-20200101-000001-1.jsonl",
                    "run-20200102-000001-1.jsonl",
                    "run-20200103-000001-1.jsonl",
                }
            )
                File.WriteAllText(Path.Combine(dir, old), string.Empty);

            NoMercyLoggerOptions options = new()
            {
                Color = false,
                LogDirectory = dir,
                MaxRunFiles = 2,
            };
            using (NoMercyLoggerProvider provider = new(options, new StringWriter())) { }

            Directory.GetFiles(dir, "run-*.jsonl").Length.Should().Be(2);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WriteEntry_AppendsLegacyLineToRunFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nm-logdir-{Guid.NewGuid():N}");
        try
        {
            NoMercyLoggerOptions options = new() { Color = false, LogDirectory = dir };
            using (NoMercyLoggerProvider provider = new(options, new StringWriter()))
            {
                provider.WriteEntry(
                    new()
                    {
                        Type = "queue",
                        Message = "legacy line",
                        Level = "Information",
                        ThreadId = 7,
                        Time = DateTime.UtcNow,
                    }
                );
            }

            string[] runs = Directory.GetFiles(dir, "run-*.jsonl");
            runs.Should().ContainSingle();
            string line = File.ReadAllLines(runs[0]).Single();
            line.Should().Contain("\"Type\":\"queue\"");
            line.Should().Contain("legacy line");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void OnRecord_ReceivesStructuredRecord()
    {
        List<NoMercyLogRecord> received = [];
        NoMercyLoggerOptions options = new() { Color = false, OnRecord = received.Add };
        using NoMercyLoggerProvider provider = new(options, new StringWriter());
        ILogger logger = provider.CreateLogger("NoMercy.Providers.TVDB.Client.TvdbBaseClient");

        logger.LogWarning("rate limited");

        received.Should().ContainSingle();
        received[0].CategoryKey.Should().Be("tvdb");
        received[0].Level.Should().Be(LogLevel.Warning);
        received[0].Message.Should().Be("rate limited");
    }

    [Fact]
    public void OnRecord_ThatThrows_DoesNotBreakLogging()
    {
        StringWriter sink = new();
        NoMercyLoggerOptions options = new()
        {
            Color = false,
            OnRecord = _ => throw new InvalidOperationException("boom"),
        };
        using NoMercyLoggerProvider provider = new(options, sink);
        ILogger logger = provider.CreateLogger("NoMercy.Service.X");

        Action act = () => logger.LogInformation("still logged");

        act.Should().NotThrow();
        sink.ToString().Should().Contain("still logged");
    }
}
