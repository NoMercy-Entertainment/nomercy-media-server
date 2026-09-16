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

using System.Text.Json;
using FluentAssertions;
using NoMercy.Plugins.Abstractions;
using NoMercy.Tests.Common;
using Xunit;

namespace NoMercy.Tests.Plugins;

[Trait("Category", "Unit")]
public class PluginTemplateTests
{
    private static readonly string TemplateRoot = FindRepoPath(
        Path.Combine("templates", "NoMercy.Plugin.Template")
    );

    // Walk up from the test assembly instead of a fixed ".." chain — the output
    // directory depth changes under a redirected BaseOutputPath.
    private static string FindRepoPath(string relativePath)
    {
        return RepoPaths.At(relativePath);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [Fact]
    public void TemplateDirectory_Exists()
    {
        Directory
            .Exists(TemplateRoot)
            .Should()
            .BeTrue($"Template directory should exist at {TemplateRoot}");
    }

    [Fact]
    public void TemplateConfig_Exists_AndIsValidJson()
    {
        string configPath = Path.Combine(TemplateRoot, ".template.config", "template.json");
        File.Exists(configPath).Should().BeTrue("template.json must exist");

        string json = File.ReadAllText(configPath);
        JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("identity", out JsonElement identity).Should().BeTrue();
        identity.GetString().Should().Be("NoMercy.Plugin.Template");

        root.TryGetProperty("shortName", out JsonElement shortName).Should().BeTrue();
        shortName.GetString().Should().Be("nomercy-plugin");

        root.TryGetProperty("sourceName", out JsonElement sourceName).Should().BeTrue();
        sourceName.GetString().Should().Be("NoMercy.Plugin.Template");

        root.TryGetProperty("symbols", out JsonElement symbols).Should().BeTrue();
        symbols
            .TryGetProperty("pluginId", out _)
            .Should()
            .BeTrue("template must generate a plugin GUID");
        symbols
            .TryGetProperty("authorName", out _)
            .Should()
            .BeTrue("template must accept an author name parameter");
        symbols
            .TryGetProperty("pluginDescription", out _)
            .Should()
            .BeTrue("template must accept a description parameter");
    }

    [Fact]
    public void PluginManifest_Exists_AndMatchesSchema()
    {
        string manifestPath = Path.Combine(TemplateRoot, "plugin.json");
        File.Exists(manifestPath).Should().BeTrue("plugin.json must exist in template");

        string json = File.ReadAllText(manifestPath);
        JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("id", out _).Should().BeTrue("manifest must have 'id'");
        root.TryGetProperty("name", out _).Should().BeTrue("manifest must have 'name'");
        root.TryGetProperty("description", out _)
            .Should()
            .BeTrue("manifest must have 'description'");
        root.TryGetProperty("version", out _).Should().BeTrue("manifest must have 'version'");
        root.TryGetProperty("assembly", out _).Should().BeTrue("manifest must have 'assembly'");

        string version = root.GetProperty("version").GetString()!;
        Version.TryParse(version, out _).Should().BeTrue("version must be a valid semver string");

        string assembly = root.GetProperty("assembly").GetString()!;
        assembly.Should().EndWith(".dll", "assembly must be a .dll filename");
    }

    [Fact]
    public void PluginManifest_AssemblyName_MatchesCsprojName()
    {
        string manifestPath = Path.Combine(TemplateRoot, "plugin.json");
        string json = File.ReadAllText(manifestPath);
        JsonDocument doc = JsonDocument.Parse(json);
        string assembly = doc.RootElement.GetProperty("assembly").GetString()!;

        string csprojPath = Path.Combine(TemplateRoot, "NoMercy.Plugin.Template.csproj");
        File.Exists(csprojPath).Should().BeTrue("csproj must exist");

        string expectedAssembly = Path.GetFileNameWithoutExtension(csprojPath) + ".dll";
        assembly.Should().Be(expectedAssembly, "plugin.json assembly must match the csproj name");
    }

    [Fact]
    public void PluginManifest_ContainsPlaceholders()
    {
        string manifestPath = Path.Combine(TemplateRoot, "plugin.json");
        string json = File.ReadAllText(manifestPath);

        json.Should()
            .Contain(
                "PLUGIN-ULID-PLACEHOLDER",
                "manifest id must use the GUID placeholder for template substitution"
            );
        json.Should()
            .Contain(
                "PLUGIN-DESCRIPTION-PLACEHOLDER",
                "manifest description must use the description placeholder"
            );
        json.Should()
            .Contain("AUTHOR-NAME-PLACEHOLDER", "manifest author must use the author placeholder");
    }

    [Fact]
    public void PluginClass_Exists_AndContainsPlaceholders()
    {
        string pluginPath = Path.Combine(TemplateRoot, "Plugin.cs");
        File.Exists(pluginPath).Should().BeTrue("Plugin.cs must exist");

        string source = File.ReadAllText(pluginPath);
        source.Should().Contain("IPlugin", "Plugin class must implement IPlugin");
        source
            .Should()
            .Contain("PLUGIN-ULID-PLACEHOLDER", "Plugin class must use the ULID placeholder");
        source.Should().Contain("Initialize", "Plugin class must implement Initialize method");
        source.Should().Contain("Dispose", "Plugin class must implement Dispose method");
    }

    [Fact]
    public void PluginClass_ImplementsIPluginInterface()
    {
        string pluginPath = Path.Combine(TemplateRoot, "Plugin.cs");
        string source = File.ReadAllText(pluginPath);

        source.Should().Contain("string Name =>", "Plugin must have Name property");
        source.Should().Contain("string Description =>", "Plugin must have Description property");
        source.Should().Contain("Ulid Id", "Plugin must have Id property");
        source.Should().Contain("Version Version", "Plugin must have Version property");
        source
            .Should()
            .Contain(
                "void Initialize(IPluginContext context)",
                "Plugin must have Initialize method"
            );
    }

    [Fact]
    public void Csproj_References_PluginAbstractions()
    {
        string csprojPath = Path.Combine(TemplateRoot, "NoMercy.Plugin.Template.csproj");
        string content = File.ReadAllText(csprojPath);

        content
            .Should()
            .Contain("NoMercy.Plugins.Abstractions", "csproj must reference plugin abstractions");
        content.Should().Contain("net10.0", "csproj must target net10.0");
    }

    [Fact]
    public void Csproj_CopiesPluginManifest()
    {
        string csprojPath = Path.Combine(TemplateRoot, "NoMercy.Plugin.Template.csproj");
        string content = File.ReadAllText(csprojPath);

        content.Should().Contain("plugin.json", "csproj must include plugin.json");
        content.Should().Contain("CopyToOutputDirectory", "plugin.json must be copied to output");
    }

    [Fact]
    public void TemplatePackageCsproj_Exists()
    {
        string packageCsprojPath = FindRepoPath(
            Path.Combine("templates", "NoMercy.Plugin.Templates.csproj")
        );
        File.Exists(packageCsprojPath).Should().BeTrue("Template package csproj must exist");

        string content = File.ReadAllText(packageCsprojPath);
        content.Should().Contain("PackageType>Template", "Must be a template package type");
        content.Should().Contain("NoMercy.Plugin.Template", "Must include the template content");
    }

    [Fact]
    public void AllRequiredTemplateFiles_Exist()
    {
        string[] requiredFiles =
        [
            ".template.config/template.json",
            "NoMercy.Plugin.Template.csproj",
            "plugin.json",
            "Plugin.cs",
        ];

        foreach (string file in requiredFiles)
        {
            string fullPath = Path.Combine(TemplateRoot, file);
            File.Exists(fullPath).Should().BeTrue($"Required template file '{file}' must exist");
        }
    }

    [Fact]
    public void PluginManifest_HasTargetAbi()
    {
        string manifestPath = Path.Combine(TemplateRoot, "plugin.json");
        string json = File.ReadAllText(manifestPath);
        JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("targetAbi", out JsonElement targetAbi)
            .Should()
            .BeTrue("manifest must have targetAbi");
        targetAbi.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void PluginManifest_TargetAbi_MatchesCurrentAbi()
    {
        string manifestPath = Path.Combine(TemplateRoot, "plugin.json");
        string json = File.ReadAllText(manifestPath);
        JsonDocument doc = JsonDocument.Parse(json);
        string targetAbi = doc.RootElement.GetProperty("targetAbi").GetString()!;

        targetAbi
            .Should()
            .Be(
                PluginAbi.Current.ToString(),
                "a scaffolded plugin must target the ABI the host actually runs, not a stale version"
            );
    }

    [Fact]
    public void PluginManifest_HasAutoEnabled()
    {
        string manifestPath = Path.Combine(TemplateRoot, "plugin.json");
        string json = File.ReadAllText(manifestPath);
        JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("autoEnabled", out JsonElement autoEnabled)
            .Should()
            .BeTrue("manifest must have autoEnabled");
        autoEnabled.ValueKind.Should().Be(JsonValueKind.True, "autoEnabled should default to true");
    }
}
