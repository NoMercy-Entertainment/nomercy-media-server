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

using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Drivers.WebDav;
using NoMercy.Storage.Factory;
using NoMercy.Storage.Remote;
using NoMercy.Tests.Storage.Container;
using WebDav;

namespace NoMercy.Tests.Storage;

// ============================================================================
// Unit tests — no Docker needed
// ============================================================================

public class WebDavDriverConfigParsingTests
{
    [Fact]
    public void Parse_missing_url_throws()
    {
        Action act = () =>
            WebDavDriverConfig.Parse("""{"ignoreCertErrors":false}""", Ulid.NewUlid());
        act.Should().Throw<ArgumentException>().WithMessage("*url*");
    }

    [Fact]
    public void Parse_empty_url_throws()
    {
        Action act = () => WebDavDriverConfig.Parse("""{"url":"  "}""", Ulid.NewUlid());
        act.Should().Throw<ArgumentException>().WithMessage("*url*");
    }

    [Fact]
    public void Parse_null_config_throws()
    {
        Action act = () => WebDavDriverConfig.Parse(null!, Ulid.NewUlid());
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Parse_malformed_json_throws_ArgumentException()
    {
        Action act = () => WebDavDriverConfig.Parse("{bad json{{", Ulid.NewUlid());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_invalid_timeout_throws()
    {
        string json = """{"url":"http://dav.example.com/","timeoutSeconds":0}""";
        Action act = () => WebDavDriverConfig.Parse(json, Ulid.NewUlid());
        act.Should().Throw<ArgumentException>().WithMessage("*timeoutSeconds*");
    }

    [Fact]
    public void Parse_minimal_config_uses_defaults()
    {
        WebDavDriverConfig config = WebDavDriverConfig.Parse(
            """{"url":"http://dav.example.com/files/"}""",
            Ulid.NewUlid()
        );

        config.Url.Should().Be("http://dav.example.com/files/");
        config.Username.Should().BeNull();
        config.Password.Should().BeNull();
        config.IgnoreCertErrors.Should().BeFalse();
        config.TimeoutSeconds.Should().Be(30);
    }

    [Fact]
    public void Parse_url_without_trailing_slash_is_normalized()
    {
        WebDavDriverConfig config = WebDavDriverConfig.Parse(
            """{"url":"http://dav.example.com/files"}""",
            Ulid.NewUlid()
        );

        config.Url.Should().EndWith("/");
    }

    [Fact]
    public void Parse_ignoreCertErrors_defaults_to_false()
    {
        WebDavDriverConfig config = WebDavDriverConfig.Parse(
            """{"url":"https://self-signed.example.com/"}""",
            Ulid.NewUlid()
        );

        config.IgnoreCertErrors.Should().BeFalse();
    }

    [Fact]
    public void Parse_ignoreCertErrors_true_accepted()
    {
        WebDavDriverConfig config = WebDavDriverConfig.Parse(
            """{"url":"https://self-signed.example.com/","ignoreCertErrors":true}""",
            Ulid.NewUlid()
        );

        config.IgnoreCertErrors.Should().BeTrue();
    }

    [Fact]
    public void Parse_custom_timeout_accepted()
    {
        WebDavDriverConfig config = WebDavDriverConfig.Parse(
            """{"url":"http://dav.example.com/","timeoutSeconds":60}""",
            Ulid.NewUlid()
        );

        config.TimeoutSeconds.Should().Be(60);
    }

    [Fact]
    public void Parse_legacy_username_field_emits_warning_and_succeeds()
    {
        Mock<ILogger> logger = new();
        string json =
            """{"url":"http://dav.example.com/","username":"alice","passwordRef":"vault/alice"}""";

        WebDavDriverConfig config = WebDavDriverConfig.Parse(json, Ulid.NewUlid(), logger.Object);

        // Legacy fields are ignored — credentials not on the config.
        config.Username.Should().BeNull();
        config.Password.Should().BeNull();

        // Logger should have received a warning.
        logger.Verify(
            l =>
                l.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("deprecated")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.Once
        );
    }

    [Fact]
    public void Parse_legacy_bearerTokenRef_emits_warning_and_succeeds()
    {
        Mock<ILogger> logger = new();
        string json = """{"url":"http://dav.example.com/","bearerTokenRef":"tokens/mytoken"}""";

        WebDavDriverConfig config = WebDavDriverConfig.Parse(json, Ulid.NewUlid(), logger.Object);

        config.Username.Should().BeNull();
        config.Password.Should().BeNull();

        logger.Verify(
            l =>
                l.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("deprecated")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.Once
        );
    }

    [Fact]
    public void For_helper_sets_username_and_password()
    {
        WebDavDriverConfig config = WebDavDriverConfig.For(
            "http://dav.example.com/",
            username: "alice",
            password: "s3cr3t"
        );

        config.Username.Should().Be("alice");
        config.Password.Should().Be("s3cr3t");
    }
}

public class WebDavStorageDriverFactoryTests
{
    private static StorageFactory FactoryWithConfig(string type, string? config)
    {
        Mock<IDriverConfigResolver> resolver = new();
        resolver.Setup(r => r.Resolve(It.IsAny<Ulid>())).Returns((type, config));
        return new(new LocalStorageDriver(), NullLogger<StorageFactory>.Instance, resolver.Object);
    }

    [Fact]
    public void For_webdav_without_config_throws_ArgumentException()
    {
        StorageFactory factory = FactoryWithConfig("webdav", null);
        Ulid driverId = Ulid.NewUlid();
        Action act = () => factory.For(Ulid.NewUlid(), driverId, "/irrelevant");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void For_webdav_missing_url_throws_ArgumentException()
    {
        StorageFactory factory = FactoryWithConfig("webdav", """{"ignoreCertErrors":false}""");
        Ulid driverId = Ulid.NewUlid();
        Action act = () => factory.For(Ulid.NewUlid(), driverId, "/irrelevant");
        act.Should().Throw<ArgumentException>().WithMessage("*url*");
    }

    [Fact]
    public void For_webdav_valid_config_returns_RemoteStorage()
    {
        string json = """{"url":"http://dav.example.com/files/"}""";
        StorageFactory factory = FactoryWithConfig("webdav", json);
        Ulid driverId = Ulid.NewUlid();

        IStorage storage = factory.For(Ulid.NewUlid(), driverId, "/irrelevant");

        storage.Should().NotBeNull().And.BeOfType<RemoteStorage>();
    }

    [Fact]
    public void For_webdav_malformed_config_throws_ArgumentException()
    {
        StorageFactory factory = FactoryWithConfig("webdav", "{{{bad json");
        Ulid driverId = Ulid.NewUlid();
        Action act = () => factory.For(Ulid.NewUlid(), driverId, "/irrelevant");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void For_webdav_credential_resolver_injects_username_and_password()
    {
        string json = """{"url":"http://dav.example.com/files/"}""";

        Mock<IDriverConfigResolver> driverResolver = new();
        driverResolver.Setup(r => r.Resolve(It.IsAny<Ulid>())).Returns(("webdav", json));

        Mock<ICredentialResolver> credResolver = new();
        credResolver.Setup(r => r.Resolve(It.IsAny<string>())).Returns(("alice", "s3cr3t"));

        StorageFactory factory = new(
            new LocalStorageDriver(),
            NullLogger<StorageFactory>.Instance,
            driverResolver.Object,
            credResolver.Object
        );

        // Construction must not throw.
        IStorage storage = factory.For(Ulid.NewUlid(), Ulid.NewUlid(), string.Empty);
        storage.Should().NotBeNull().And.BeOfType<RemoteStorage>();
    }
}

// ============================================================================
// Integration tests — run against the shared all-in-one storage container
// (WebDAV). The container is started once for the whole assembly by the
// StorageBackends collection fixture and torn down after the last test.
// ============================================================================

[Collection("StorageBackends")]
public class WebDavStorageDriverIntegrationTests(StorageBackendsFixture fix)
{
    private string SkipReason => fix.StartupError ?? "storage container not available";

    [SkippableFact]
    public async Task RoundTrip_write_read_delete()
    {
        Skip.If(!fix.Available, SkipReason);

        WebDavStorageDriver driver = fix.BuildWebDavDriver();
        string path = $"roundtrip-{Ulid.NewUlid()}.txt";
        byte[] data = [.. "hello webdav"u8];

        await using (Stream w = driver.OpenWrite(path, overwrite: true))
            await w.WriteAsync(data);

        await using Stream r = driver.OpenRead(path);
        using MemoryStream ms = new();
        await r.CopyToAsync(ms);
        ms.ToArray().Should().Equal(data);

        driver.DeleteFile(path);
        driver.FileExists(path).Should().BeFalse();
    }

    [SkippableFact]
    public async Task LargeFile_write_read()
    {
        Skip.If(!fix.Available, SkipReason);

        WebDavStorageDriver driver = fix.BuildWebDavDriver();
        string path = $"large-{Ulid.NewUlid()}.bin";

        byte[] data = new byte[12 * 1024 * 1024];
        new Random(99).NextBytes(data);

        await using (Stream w = driver.OpenWrite(path, overwrite: true))
            await w.WriteAsync(data);

        long size = driver.GetFileSize(path);
        size.Should().Be(data.Length);

        driver.DeleteFile(path);
    }

    [SkippableFact]
    public async Task Mkcol_recursive_creation()
    {
        Skip.If(!fix.Available, SkipReason);

        WebDavStorageDriver driver = fix.BuildWebDavDriver();
        string dir = $"a-{Ulid.NewUlid()}/b/c";

        driver.CreateDirectory(dir);
        driver.DirectoryExists(dir).Should().BeTrue();

        driver.DeleteDirectory($"a-{dir.Split('/')[0]}", recursive: true);
    }

    [SkippableFact]
    public async Task Propfind_enumerate_with_pattern()
    {
        Skip.If(!fix.Available, SkipReason);

        WebDavStorageDriver driver = fix.BuildWebDavDriver();
        string dirName = $"enum-{Ulid.NewUlid()}";
        driver.CreateDirectory(dirName);

        string fileA = $"{dirName}/a.txt";
        string fileB = $"{dirName}/b.txt";
        string fileC = $"{dirName}/c.bin";
        byte[] bytes = [.. "x"u8];

        await using (Stream w = driver.OpenWrite(fileA, overwrite: true))
            await w.WriteAsync(bytes);
        await using (Stream w2 = driver.OpenWrite(fileB, overwrite: true))
            await w2.WriteAsync(bytes);
        await using (Stream w3 = driver.OpenWrite(fileC, overwrite: true))
            await w3.WriteAsync(bytes);

        IEnumerable<string> entries = driver.EnumerateFileSystemEntries(
            dirName,
            "*.txt",
            SearchOption.TopDirectoryOnly
        );

        entries.Should().HaveCount(2);

        driver.DeleteDirectory(dirName, recursive: true);
    }

    [SkippableFact]
    public async Task MoveFile_renames_resource()
    {
        Skip.If(!fix.Available, SkipReason);

        WebDavStorageDriver driver = fix.BuildWebDavDriver();
        string src = $"move-src-{Ulid.NewUlid()}.txt";
        string dst = $"move-dst-{Ulid.NewUlid()}.txt";
        byte[] data = [.. "move me"u8];

        await using (Stream w = driver.OpenWrite(src, overwrite: true))
            await w.WriteAsync(data);

        driver.MoveFile(src, dst);

        driver.FileExists(src).Should().BeFalse();
        driver.FileExists(dst).Should().BeTrue();

        driver.DeleteFile(dst);
    }

    [SkippableFact]
    public async Task CopyFile_duplicates_resource()
    {
        Skip.If(!fix.Available, SkipReason);

        WebDavStorageDriver driver = fix.BuildWebDavDriver();
        string src = $"copy-src-{Ulid.NewUlid()}.txt";
        string dst = $"copy-dst-{Ulid.NewUlid()}.txt";
        byte[] data = [.. "copy me"u8];

        await using (Stream w = driver.OpenWrite(src, overwrite: true))
            await w.WriteAsync(data);

        driver.CopyFile(src, dst, overwrite: true);

        driver.FileExists(src).Should().BeTrue();
        driver.FileExists(dst).Should().BeTrue();

        driver.DeleteFile(src);
        driver.DeleteFile(dst);
    }

    [SkippableFact]
    public async Task BasicAuth_wrong_password_fails()
    {
        Skip.If(!fix.Available, SkipReason);

        WebDavClient badClient = new(
            new WebDavClientParams
            {
                BaseAddress = new(fix.WebDavBaseUrl),
                Credentials = new NetworkCredential("testuser", "wrongpassword"),
            }
        );
        WebDavStorageDriver driver = new(badClient, fix.WebDavBaseUrl);

        // Propfind on root with wrong creds should return an HTTP error (401/403).
        // Some servers return 401 as non-successful; the driver returns false (not exception)
        // because FileExists/DirectoryExists swallow non-success responses.
        bool result = driver.DirectoryExists("/");
        result.Should().BeFalse("401/403 responses should be treated as 'not found'");
    }
}

// ============================================================================
// Unit tests — EnumerateFileSystemEntries contract (no Docker)
// ============================================================================

public class WebDavEnumerateContractTests
{
    private const string BaseUrl = "https://nas.local/dav/";

    private static WebDavStorageDriver BuildDriver(Mock<IWebDavClient> mockClient)
    {
        return new(mockClient.Object, BaseUrl);
    }

    private static WebDavResource MakeResource(string absoluteUri, bool isCollection)
    {
        WebDavResource.Builder builder = new WebDavResource.Builder().WithUri(absoluteUri);
        if (isCollection)
            builder.IsCollection();
        else
            builder.IsNotCollection();
        return builder.Build();
    }

    private static PropfindResponse MakePropfindResponse(
        int statusCode,
        IEnumerable<WebDavResource> resources
    )
    {
        return new(statusCode, resources);
    }

    [Theory]
    [InlineData("a #b.mkv", "a%20%23b.mkv")]
    [InlineData("a ?b%.mkv", "a%20%3Fb%25.mkv")]
    public void FileExists_escapes_file_name_in_client_uri(string path, string encoded)
    {
        Mock<IWebDavClient> mock = new();
        string expectedUri = BaseUrl + encoded;
        mock.Setup(c => c.Propfind(expectedUri, It.IsAny<PropfindParameters>()))
            .ReturnsAsync(MakePropfindResponse(207, [MakeResource(expectedUri, false)]));

        WebDavStorageDriver driver = BuildDriver(mock);

        driver.GetFullPath(path).Should().Be(expectedUri);
        driver.FileExists(path).Should().BeTrue();
        mock.Verify(c => c.Propfind(expectedUri, It.IsAny<PropfindParameters>()), Times.Once);
    }

    [Fact]
    public void DirectoryExists_escapes_collection_name_in_client_uri()
    {
        Mock<IWebDavClient> mock = new();
        string expectedUri = BaseUrl + "A%20%23%3F%25/";
        mock.Setup(c => c.Propfind(expectedUri, It.IsAny<PropfindParameters>()))
            .ReturnsAsync(MakePropfindResponse(207, [MakeResource(expectedUri, true)]));

        BuildDriver(mock).DirectoryExists("A #?%").Should().BeTrue();
        mock.Verify(c => c.Propfind(expectedUri, It.IsAny<PropfindParameters>()), Times.Once);
    }

    [Fact]
    public void Listed_encoded_name_round_trips_to_same_client_uri()
    {
        Mock<IWebDavClient> mock = new();
        string expectedUri = BaseUrl + "folder/a%20%23b%25.mkv";
        mock.Setup(c => c.Propfind(BaseUrl, It.IsAny<PropfindParameters>()))
            .ReturnsAsync(
                MakePropfindResponse(
                    207,
                    [MakeResource(BaseUrl, true), MakeResource(expectedUri, false)]
                )
            );
        mock.Setup(c => c.Propfind(expectedUri, It.IsAny<PropfindParameters>()))
            .ReturnsAsync(MakePropfindResponse(207, [MakeResource(expectedUri, false)]));

        WebDavStorageDriver driver = BuildDriver(mock);
        string path = driver
            .EnumerateFileSystemEntries("", "*", SearchOption.TopDirectoryOnly)
            .Single();

        path.Should().Be("folder/a #b%.mkv");
        driver.FileExists(path).Should().BeTrue();
        mock.Verify(c => c.Propfind(expectedUri, It.IsAny<PropfindParameters>()), Times.Once);
    }

    [Fact]
    public void EnumerateFileSystemEntries_returns_relative_paths_not_absolute_uris()
    {
        Mock<IWebDavClient> mock = new();

        PropfindResponse enumResponse = MakePropfindResponse(
            207,
            [
                MakeResource("https://nas.local/dav/", isCollection: true), // dir itself — skipped
                MakeResource("https://nas.local/dav/folder/file.mp3", isCollection: false),
                MakeResource("https://nas.local/dav/folder/", isCollection: true),
            ]
        );

        mock.Setup(c =>
                c.Propfind(
                    It.Is<string>(u => u == BaseUrl),
                    It.Is<PropfindParameters>(p =>
                        p.ApplyTo == ApplyTo.Propfind.ResourceAndChildren
                    )
                )
            )
            .ReturnsAsync(enumResponse);

        WebDavStorageDriver driver = BuildDriver(mock);

        List<string> entries =
        [
            .. driver.EnumerateFileSystemEntries(string.Empty, "*", SearchOption.TopDirectoryOnly),
        ];

        entries.Should().Contain("folder/file.mp3");
        entries.Should().Contain("folder");
        entries.Should().NotContain(e => e.StartsWith("https://"));
    }

    [Fact]
    public void EnumerateFileSystemEntries_DirectoryExists_roundtrip()
    {
        Mock<IWebDavClient> mock = new();

        PropfindResponse enumResponse = MakePropfindResponse(
            207,
            [
                MakeResource("https://nas.local/dav/", isCollection: true),
                MakeResource("https://nas.local/dav/music/", isCollection: true),
            ]
        );

        mock.Setup(c =>
                c.Propfind(
                    It.Is<string>(u => u == BaseUrl),
                    It.Is<PropfindParameters>(p =>
                        p.ApplyTo == ApplyTo.Propfind.ResourceAndChildren
                    )
                )
            )
            .ReturnsAsync(enumResponse);

        // DirectoryExists calls Propfind on "https://nas.local/dav/music/" with ResourceOnly
        PropfindResponse existsResponse = MakePropfindResponse(
            207,
            [MakeResource("https://nas.local/dav/music/", isCollection: true)]
        );

        mock.Setup(c =>
                c.Propfind(
                    It.Is<string>(u => u == "https://nas.local/dav/music/"),
                    It.Is<PropfindParameters>(p => p.ApplyTo == ApplyTo.Propfind.ResourceOnly)
                )
            )
            .ReturnsAsync(existsResponse);

        WebDavStorageDriver driver = BuildDriver(mock);

        List<string> entries =
        [
            .. driver.EnumerateFileSystemEntries(string.Empty, "*", SearchOption.TopDirectoryOnly),
        ];

        string dirEntry = entries.Single(e => e == "music");
        bool exists = driver.DirectoryExists(dirEntry);

        exists
            .Should()
            .BeTrue("round-trip from EnumerateFileSystemEntries to DirectoryExists must work");
    }
}
