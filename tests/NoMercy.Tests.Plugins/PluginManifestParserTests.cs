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
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginManifestParserTests : IDisposable
{
    private readonly string _tempDir;

    public PluginManifestParserTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "nomercy-manifest-tests-" + Ulid.NewUlid().ToString()
        );
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException) { }
    }

    private static string CreateValidManifestJson(
        Ulid? id = null,
        string name = "TestPlugin",
        string description = "A test plugin",
        string version = "1.0.0",
        string assembly = "TestPlugin.dll",
        string? targetAbi = null,
        string? author = null,
        string? projectUrl = null,
        bool autoEnabled = true
    )
    {
        Dictionary<string, object?> manifest = new()
        {
            ["id"] = (id ?? Ulid.NewUlid()).ToString(),
            ["name"] = name,
            ["description"] = description,
            ["version"] = version,
            ["assembly"] = assembly,
            ["autoEnabled"] = autoEnabled,
        };

        if (targetAbi is not null)
            manifest["targetAbi"] = targetAbi;
        if (author is not null)
            manifest["author"] = author;
        if (projectUrl is not null)
            manifest["projectUrl"] = projectUrl;

        return JsonSerializer.Serialize(manifest);
    }

    [Fact]
    public void Parse_ValidJson_ReturnsManifest()
    {
        Ulid pluginId = Ulid.NewUlid();
        string json = CreateValidManifestJson(
            id: pluginId,
            name: "MyPlugin",
            version: "2.1.0",
            assembly: "MyPlugin.dll"
        );

        PluginManifest manifest = PluginManifestParser.Parse(json);

        manifest.Id.Value.Should().Be(pluginId);
        manifest.Name.Should().Be("MyPlugin");
        manifest.Version.Should().Be("2.1.0");
        manifest.Assembly.Should().Be("MyPlugin.dll");
        manifest.AutoEnabled.Should().BeTrue();
    }

    [Fact]
    public void Parse_IdWrittenAsAGuid_LoadsAsTheEquivalentUlid()
    {
        // What every plugin published before this platform settled on Ulid
        // carries. A server update does not get to stop loading it, and the id
        // it resolves to has to be the same one on every start, or the plugin
        // loses its stored consent and grants.
        Guid preUpgradeId = Guid.Parse("395df423-3e2f-4a1c-bc5b-dbc41a9133ef");
        string json =
            $@"{{""id"":""{preUpgradeId}"",""name"":""Test"",""description"":""d"",""version"":""1.0.0"",""assembly"":""t.dll""}}";

        PluginManifest manifest = PluginManifestParser.Parse(json);

        manifest.Id.Value.Should().Be(new Ulid(preUpgradeId));
        manifest.Id.Value.ToGuid().Should().Be(preUpgradeId);
    }

    [Fact]
    public void Parse_IdThatIsNeitherUlidNorGuid_IsRejected()
    {
        string json =
            @"{""id"":""not-an-id"",""name"":""Test"",""description"":""d"",""version"":""1.0.0"",""assembly"":""t.dll""}";

        Action parse = () => PluginManifestParser.Parse(json);

        parse.Should().Throw<JsonException>();
    }

    [Fact]
    public void Parse_WithOptionalFields_PopulatesAll()
    {
        string json = CreateValidManifestJson(
            author: "Test Author",
            projectUrl: "https://example.com",
            targetAbi: "9.0.0"
        );

        PluginManifest manifest = PluginManifestParser.Parse(json);

        manifest.Author.Should().Be("Test Author");
        manifest.ProjectUrl.Should().Be("https://example.com");
        manifest.TargetAbi.Should().Be("9.0.0");
    }

    [Fact]
    public void Parse_AutoEnabledFalse_SetsCorrectly()
    {
        string json = CreateValidManifestJson(autoEnabled: false);

        PluginManifest manifest = PluginManifestParser.Parse(json);

        manifest.AutoEnabled.Should().BeFalse();
    }

    [Fact]
    public void Parse_NullJson_ThrowsArgumentException()
    {
        Action act = () => PluginManifestParser.Parse(null!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_EmptyJson_ThrowsArgumentException()
    {
        Action act = () => PluginManifestParser.Parse("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_InvalidJson_ThrowsJsonException()
    {
        Action act = () => PluginManifestParser.Parse("not json");

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Parse_JsonNullLiteral_ThrowsInvalidOperation()
    {
        // "null" is valid, non-whitespace JSON — it clears the
        // ArgumentException.ThrowIfNullOrWhiteSpace guard but deserializes to a
        // null PluginManifest, which must be rejected explicitly rather than
        // let a NullReferenceException surface from Validate().
        Action act = () => PluginManifestParser.Parse("null");

        act.Should().Throw<InvalidOperationException>().WithMessage("*deserialize*");
    }

    [Fact]
    public void Parse_EmptyGuid_ThrowsInvalidOperation()
    {
        string json = CreateValidManifestJson(id: Ulid.Empty);

        Action act = () => PluginManifestParser.Parse(json);

        act.Should().Throw<InvalidOperationException>().WithMessage("*id*");
    }

    [Fact]
    public void Parse_MissingVersion_ThrowsJsonException()
    {
        string json =
            """{"id":"0J6HB7G4HM28T14D0J6HB7H40J","name":"Test","description":"d","assembly":"t.dll"}""";

        Action act = () => PluginManifestParser.Parse(json);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Parse_InvalidVersion_ThrowsInvalidOperation()
    {
        string json = CreateValidManifestJson(version: "not-a-version");

        Action act = () => PluginManifestParser.Parse(json);

        act.Should().Throw<InvalidOperationException>().WithMessage("*version*");
    }

    [Fact]
    public void Parse_EmptyAssembly_ThrowsInvalidOperation()
    {
        Ulid id = Ulid.NewUlid();
        string json =
            $@"{{""id"":""{id}"",""name"":""Test"",""description"":""d"",""version"":""1.0.0"",""assembly"":""""}}";

        Action act = () => PluginManifestParser.Parse(json);

        act.Should().Throw<InvalidOperationException>().WithMessage("*assembly*");
    }

    [Fact]
    public void Parse_WithJsonComments_Succeeds()
    {
        Ulid id = Ulid.NewUlid();
        string json =
            $@"{{
            // This is a comment
            ""id"": ""{id}"",
            ""name"": ""Test"",
            ""description"": ""desc"",
            ""version"": ""1.0.0"",
            ""assembly"": ""Test.dll""
        }}";

        PluginManifest manifest = PluginManifestParser.Parse(json);

        manifest.Id.Value.Should().Be(id);
    }

    [Fact]
    public void Parse_WithTrailingCommas_Succeeds()
    {
        Ulid id = Ulid.NewUlid();
        string json =
            $@"{{
            ""id"": ""{id}"",
            ""name"": ""Test"",
            ""description"": ""desc"",
            ""version"": ""1.0.0"",
            ""assembly"": ""Test.dll"",
        }}";

        PluginManifest manifest = PluginManifestParser.Parse(json);

        manifest.Id.Value.Should().Be(id);
    }

    [Fact]
    public async Task ParseFileAsync_ValidFile_ReturnsManifest()
    {
        Ulid id = Ulid.NewUlid();
        string json = CreateValidManifestJson(id: id, name: "FilePlugin");
        string filePath = Path.Combine(_tempDir, "plugin.json");
        await File.WriteAllTextAsync(filePath, json);

        IStorage storage = TestStorageHelper.CreateStorage(_tempDir);
        PluginManifest manifest = await PluginManifestParser.ParseFileAsync(filePath, storage);

        manifest.Id.Value.Should().Be(id);
        manifest.Name.Should().Be("FilePlugin");
    }

    [Fact]
    public async Task ParseFileAsync_FileNotFound_ThrowsFileNotFoundException()
    {
        IStorage storage = TestStorageHelper.CreateStorage(_tempDir);
        // Missing file UNDER the scoped root. An out-of-root absolute path is
        // rejected by the storage guard before the existence check, and a leading
        // "/" path is absolute on Linux but relative on Windows — so it must stay
        // inside the allowed root to test the not-found path cross-platform.
        string missingPath = Path.Combine(_tempDir, "nonexistent", "plugin.json");
        Func<Task> act = () => PluginManifestParser.ParseFileAsync(missingPath, storage);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task ParseFileAsync_NullPath_ThrowsArgumentException()
    {
        IStorage storage = TestStorageHelper.CreateStorage(_tempDir);
        Func<Task> act = () => PluginManifestParser.ParseFileAsync(null!, storage);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void ToPluginInfo_CreatesCorrectInfo()
    {
        Ulid id = Ulid.NewUlid();
        PluginManifest manifest = new()
        {
            Id = new PluginId(id),
            Name = "TestPlugin",
            Description = "A test",
            Version = "2.0.1",
            Assembly = "TestPlugin.dll",
            Author = "Author",
            ProjectUrl = "https://test.com",
            TargetAbi = "9.0.0",
        };

        PluginInfo info = PluginManifestParser.ToPluginInfo(
            manifest,
            "/plugins/TestPlugin.dll",
            PluginStatus.Active,
            "/plugins/plugin.json"
        );

        info.Id.Should().Be(id);
        info.Name.Should().Be("TestPlugin");
        info.Description.Should().Be("A test");
        info.Version.Should().Be(new(2, 0, 1));
        info.Status.Should().Be(PluginStatus.Active);
        info.Author.Should().Be("Author");
        info.ProjectUrl.Should().Be("https://test.com");
        info.AssemblyPath.Should().Be("/plugins/TestPlugin.dll");
        info.TargetAbi.Should().Be("9.0.0");
        info.ManifestPath.Should().Be("/plugins/plugin.json");
    }

    [Fact]
    public void ToPluginInfo_NullManifest_ThrowsArgumentNullException()
    {
        Action act = () => PluginManifestParser.ToPluginInfo(null!, "/path", PluginStatus.Active);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToPluginInfo_DisabledStatus_SetsCorrectly()
    {
        PluginManifest manifest = new()
        {
            Id = new PluginId(Ulid.NewUlid()),
            Name = "Test",
            Description = "d",
            Version = "1.0.0",
            Assembly = "Test.dll",
        };

        PluginInfo info = PluginManifestParser.ToPluginInfo(
            manifest,
            "/path",
            PluginStatus.Disabled
        );

        info.Status.Should().Be(PluginStatus.Disabled);
    }
}
