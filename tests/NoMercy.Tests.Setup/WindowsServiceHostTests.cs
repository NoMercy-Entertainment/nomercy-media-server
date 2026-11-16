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

using CommandLine;
using NoMercy.Service;

namespace NoMercy.Tests.Setup;

public class WindowsServiceHostTests
{
    [Fact]
    public void StartupOptions_RunAsService_DefaultsToFalse()
    {
        StartupOptions options = new();
        Assert.False(options.RunAsService);
    }

    [Fact]
    public void StartupOptions_RunAsService_ParsedFromArgs()
    {
        ParserResult<StartupOptions> result = Parser.Default.ParseArguments<StartupOptions>([
            "--service",
        ]);

        StartupOptions? parsed = null;
        result.WithParsed(o => parsed = o);

        Assert.NotNull(parsed);
        Assert.True(parsed.RunAsService);
    }

    [Fact]
    public void StartupOptions_RunAsService_FalseWhenNotProvided()
    {
        ParserResult<StartupOptions> result = Parser.Default.ParseArguments<StartupOptions>([]);

        StartupOptions? parsed = null;
        result.WithParsed(o => parsed = o);

        Assert.NotNull(parsed);
        Assert.False(parsed.RunAsService);
    }

    [Fact]
    public void StartupOptions_RunAsService_CoexistsWithOtherFlags()
    {
        ParserResult<StartupOptions> result = Parser.Default.ParseArguments<StartupOptions>([
            "--service",
            "--dev",
        ]);

        StartupOptions? parsed = null;
        result.WithParsed(o => parsed = o);

        Assert.NotNull(parsed);
        Assert.True(parsed.RunAsService);
        Assert.True(parsed.Development);
    }

    [Fact]
    public void InstallerServiceBinPath_PassesServiceFlagAfterQuotedExePath()
    {
        string iss = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "NoMercy.Service", "NoMercyMediaServer.iss")
        );
        string createLine = iss.Split('\n')
            .Single(line => line.Contains("create NoMercyMediaServer", StringComparison.Ordinal));

        // sc.exe takes the binPath as one argument: "\"<exe>\" --service".
        // Inno doubles every literal quote inside a quoted parameter string.
        Assert.Contains(
            "binPath= \"\"\\\"\"{app}\\NoMercyMediaServer.exe\\\"\" --service\"\"",
            createLine,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void InstallerConfigsExistingServiceToServiceModeBeforeStart()
    {
        string[] lines = File.ReadAllLines(
            Path.Combine(RepoRoot(), "src", "NoMercy.Service", "NoMercyMediaServer.iss")
        );
        int create = Array.FindIndex(lines, l => l.Contains("create NoMercyMediaServer"));
        int config = Array.FindIndex(lines, l => l.Contains("config NoMercyMediaServer"));
        int start = Array.FindIndex(lines, l => l.Contains("start NoMercyMediaServer"));

        Assert.True(config >= 0, "an upgrade needs an sc config step for the existing service");
        Assert.True(create < config && config < start, "order must be create, config, start");
        Assert.Contains(
            "binPath= \"\"\\\"\"{app}\\NoMercyMediaServer.exe\\\"\" --service\"\"",
            lines[config],
            StringComparison.Ordinal
        );
        Assert.Contains("Tasks: installservice", lines[config], StringComparison.Ordinal);
        Assert.Contains("runhidden", lines[config], StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "NoMercy.Service")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("repository root not found");
    }
}
