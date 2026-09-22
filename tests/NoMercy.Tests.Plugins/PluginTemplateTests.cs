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
using NoMercy.PluginSdk.Abstractions;
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
            .Contain("NoMercy.PluginSdk.Abstractions", "csproj must reference plugin abstractions");
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

    [Fact]
    public void The_template_targets_the_current_abi()
    {
        JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TemplateRoot, "plugin.json"))
        );

        manifest
            .RootElement.GetProperty("targetAbi")
            .GetString()
            .Should()
            .Be(
                PluginAbi.Current.ToString(),
                "a template that scaffolds against a contract the server no longer speaks is a first run that fails"
            );
    }

    [Fact]
    public void The_template_declares_only_capabilities_it_uses()
    {
        JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TemplateRoot, "plugin.json"))
        );
        JsonElement capabilities = manifest.RootElement.GetProperty("capabilities");

        capabilities
            .ValueKind.Should()
            .Be(
                JsonValueKind.Object,
                "capabilities are an object on the manifest, not a list of names"
            );
        capabilities.GetProperty("rest").GetBoolean().Should().BeTrue();
        capabilities
            .TryGetProperty("network", out _)
            .Should()
            .BeFalse("a template that asks for the network teaches every author to ask for it");
    }

    [Fact]
    public void Every_ui_mount_names_a_kind_the_server_knows()
    {
        JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TemplateRoot, "plugin.json"))
        );

        IEnumerable<string> sections = manifest
            .RootElement.GetProperty("capabilities")
            .GetProperty("ui")
            .GetProperty("mounts")
            .EnumerateArray()
            .Select(mount => mount.GetProperty("section").GetString()!);

        sections.Should().OnlyContain(section => PluginKind.IsKnown(section));
    }

    [Fact]
    public void Every_label_in_the_manifest_is_a_key_that_both_locales_carry()
    {
        JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TemplateRoot, "plugin.json"))
        );
        Dictionary<string, string> english = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(TemplateRoot, "lang", "en.json"))
        )!;
        Dictionary<string, string> dutch = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(TemplateRoot, "lang", "nl.json"))
        )!;

        IEnumerable<string> labels = manifest
            .RootElement.GetProperty("capabilities")
            .GetProperty("ui")
            .GetProperty("mounts")
            .EnumerateArray()
            .Select(mount => mount.GetProperty("label").GetString()!);

        foreach (string label in labels)
        {
            english.Should().ContainKey(label);
            dutch
                .Should()
                .ContainKey(
                    label,
                    "a second locale missing a key is a page that falls back silently"
                );
        }
    }

    [Fact]
    public void The_two_locales_carry_the_same_keys()
    {
        Dictionary<string, string> english = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(TemplateRoot, "lang", "en.json"))
        )!;
        Dictionary<string, string> dutch = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(TemplateRoot, "lang", "nl.json"))
        )!;

        dutch.Keys.Should().BeEquivalentTo(english.Keys);
    }

    [Fact]
    public void The_settings_schema_reads_against_the_generated_schema()
    {
        JsonDocument settings = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TemplateRoot, "settings.schema.json"))
        );

        JsonElement field = settings.RootElement.GetProperty("fields").EnumerateArray().First();

        field.GetProperty("type").GetString().Should().BeOneOf(PluginFormFieldType.All);
        field.GetProperty("scope").GetString().Should().BeOneOf("server", "user");
        PluginSettingsSchema.Json.Should().Contain("\"fields\"");
    }

    [Fact]
    public void The_package_reference_is_pinned_to_a_major_rather_than_floating()
    {
        string csproj = File.ReadAllText(
            Path.Combine(TemplateRoot, "NoMercy.Plugin.Template.csproj")
        );

        csproj
            .Should()
            .NotContain(
                "Version=\"*\"",
                "a floating reference rebuilds against a contract the plugin was never tested with"
            );
        csproj.Should().Contain("NoMercy.PluginSdk.Analyzers");
    }

    [Fact]
    public void The_template_ships_for_both_forges()
    {
        File.Exists(Path.Combine(TemplateRoot, ".github", "workflows", "build.yml"))
            .Should()
            .BeTrue();
        File.Exists(Path.Combine(TemplateRoot, ".forgejo", "workflows", "build.yml"))
            .Should()
            .BeTrue("Fillz publishes from Forgejo, so a GitHub-only template is one he rewrites");
    }

    [Fact]
    public void Nothing_user_facing_is_written_in_the_template_source()
    {
        IEnumerable<string> sources = Directory.EnumerateFiles(
            TemplateRoot,
            "*.cs",
            SearchOption.AllDirectories
        );

        foreach (string source in sources)
        {
            File.ReadAllText(source)
                .Should()
                .NotContain(
                    "\"It works\"",
                    $"{Path.GetFileName(source)} writes a sentence no translator can reach"
                );
        }
    }
}
