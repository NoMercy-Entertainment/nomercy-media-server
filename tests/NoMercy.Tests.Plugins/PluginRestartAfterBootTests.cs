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
using Microsoft.Extensions.DependencyInjection;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A plugin that was present when the server booted must not be told to restart
/// it.
///
/// This is the message an owner saw after doing exactly what it asked: the
/// startup pass only recorded plugins that registered DI services, while the
/// advisor consults the same record for ROUTES. A plugin declaring `rest` and
/// contributing no services — which is most of them — was therefore never
/// recorded, and reported "needs a restart" after every boot, forever.
///
/// The advisor's own unit tests could not catch it: the advisor was right and
/// nobody told it the truth. This drives the real registration pass instead.
/// </summary>
public class PluginRestartAfterBootTests : IDisposable
{
    private static readonly Ulid PluginId = Ulid.Parse("2V5V1D07TA9MSSRRCT19DGYB8H");

    private readonly string _pluginsDir;

    public PluginRestartAfterBootTests()
    {
        _pluginsDir = Path.Combine(
            Path.GetTempPath(),
            "nomercy-restart-" + Ulid.NewUlid().ToString()
        );
        Directory.CreateDirectory(_pluginsDir);
    }

    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            if (Directory.Exists(_pluginsDir))
                Directory.Delete(_pluginsDir, recursive: true);
        }
        catch (Exception) { }

        GC.SuppressFinalize(this);
    }

    /// <summary>The Echo sample, restaged under a manifest that declares REST and no services.</summary>
    private string StageRestOnlyPlugin()
    {
        string binDir = EchoBinDir();
        string source = Path.Combine(binDir, "NoMercy.Plugin.Samples.Echo.dll");

        if (!File.Exists(source))
            throw new FileNotFoundException(
                $"Echo plugin DLL not found at '{source}'. Build NoMercy.Plugin.Samples.Echo first."
            );

        string pluginDir = Path.Combine(_pluginsDir, "RestOnly");
        Directory.CreateDirectory(pluginDir);

        foreach (string file in Directory.EnumerateFiles(binDir, "*.dll"))
            File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)), overwrite: true);

        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            $$"""
            {
              "id": "{{PluginId}}",
              "name": "Rest Only",
              "description": "Declares a REST surface and registers no services.",
              "version": "1.0.0",
              "assembly": "NoMercy.Plugin.Samples.Echo.dll",
              "autoEnabled": true,
              "capabilities": { "rest": true }
            }
            """
        );

        return pluginDir;
    }

    private static string EchoBinDir()
    {
        string testBinDir = Path.GetDirectoryName(
            typeof(PluginRestartAfterBootTests).Assembly.Location
        )!;
        string buildConfig = Path.GetFileName(Path.GetDirectoryName(testBinDir)!);
        string repoRoot = Path.GetFullPath(Path.Combine(testBinDir, "..", "..", "..", "..", ".."));

        string preferred = Path.Combine(
            repoRoot,
            "tests",
            "NoMercy.Plugin.Samples.Echo",
            "bin",
            buildConfig,
            "net10.0"
        );

        if (Directory.Exists(preferred))
            return preferred;

        return Path.Combine(
            repoRoot,
            "tests",
            "NoMercy.Plugin.Samples.Echo",
            "bin",
            string.Equals(buildConfig, "Release", StringComparison.OrdinalIgnoreCase)
                ? "Debug"
                : "Release",
            "net10.0"
        );
    }

    private static PluginInfo RestOnlyPlugin() =>
        new()
        {
            Id = PluginId,
            Name = "Rest Only",
            Description = "",
            Version = new(1, 0, 0),
            Status = PluginStatus.Active,
            ContributesServices = false,
            Capabilities = new() { Rest = true },
        };

    [Fact]
    public void APluginPresentAtBootIsNotToldToRestartAfterOne()
    {
        StageRestOnlyPlugin();

        PluginRestartAdvisor advisor = new();
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton<IPluginRestartAdvisor>(advisor);

        services.RegisterPluginServicesFromManifests(_pluginsDir);

        advisor
            .Evaluate(RestOnlyPlugin(), PluginOperation.Enable)
            .Required.Should()
            .BeFalse("the plugin's routes were collected during this very pass");
    }

    [Fact]
    public void APluginThatWasNotThereAtBootStillNeedsOne()
    {
        // The other half of the contract: arriving after the pipeline is built
        // genuinely does need a restart, and saying so has to keep meaning
        // something.
        PluginRestartAdvisor advisor = new();
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton<IPluginRestartAdvisor>(advisor);

        services.RegisterPluginServicesFromManifests(_pluginsDir);

        advisor
            .Evaluate(RestOnlyPlugin(), PluginOperation.Enable)
            .Reasons.Should()
            .HaveFlag(PluginRestartReason.OwnsRoutes);
    }
}
