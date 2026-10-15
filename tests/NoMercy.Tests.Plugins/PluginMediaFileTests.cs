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
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Media;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A plugin hands the player a file it wrote into its own folder, a recording
/// for instance. The server serves it, so the folder's edge is the guard: the
/// file has to be inside, by a path that stays inside, with no link on the way.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginMediaFileTests : IDisposable
{
    private static readonly Ulid LiveTv = Ulid.Parse("01J9ZK5V8Y0000000000000017");
    private static readonly Guid Viewer = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly string _pluginsRoot = Path.Combine(
        Path.GetTempPath(),
        $"nm-plugin-files-{Ulid.NewUlid()}"
    );

    private readonly PluginMediaTicketMinter _minter = new(
        TimeProvider.System,
        "a-server-key-that-is-long-enough-for-hmac"u8.ToArray()
    );

    public PluginMediaFileTests() => Directory.CreateDirectory(DataFolder);

    private string DataFolder => Path.Combine(_pluginsRoot, "data", LiveTv.ToString());

    public void Dispose()
    {
        if (Directory.Exists(_pluginsRoot))
            Directory.Delete(_pluginsRoot, recursive: true);
    }

    [Fact]
    public async Task A_recording_in_the_plugins_folder_gets_a_link_on_this_server()
    {
        Write("recordings/news.ts", [0x47, 1, 2, 3]);

        PluginMediaUrl url = await Proxy().MintFileAsync("recordings/news.ts");

        url.Url.ToString().Should().StartWith($"/api/v1/plugins/{LiveTv}/media/");
        url.Url.ToString()
            .Should()
            .NotContain("news.ts", "a link a viewer can read is a link a viewer can share");

        PluginMediaTicket ticket = _minter.Read(url.Url.ToString().Split('/')[^1])!;
        ticket.FilePath.Should().Be("recordings/news.ts");
        ticket.UserId.Should().Be(Viewer);
    }

    [Fact]
    public async Task The_file_behind_the_link_reads_back_through_the_folder()
    {
        Write("recordings/news.ts", [0x47, 1, 2, 3]);

        await using Stream stream = Proxy().OpenFile("recordings/news.ts")!;
        using MemoryStream copy = new();
        await stream.CopyToAsync(copy);

        copy.ToArray().Should().Equal(0x47, 1, 2, 3);
        stream.CanSeek.Should().BeTrue("a recording is watched with seeking");
    }

    /// <summary>A recording still being written can already be watched.</summary>
    [Fact]
    public async Task A_file_the_plugin_is_still_writing_can_be_opened()
    {
        Write("recordings/live.ts", []);
        await using FileStream writer = new(
            Path.Combine(DataFolder, "recordings", "live.ts"),
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read
        );

        await using Stream? reader = Proxy().OpenFile("recordings/live.ts");

        reader.Should().NotBeNull();
    }

    [Theory]
    [InlineData("../other-plugin/secret.json")]
    [InlineData("recordings/../../escape.ts")]
    public async Task A_path_that_climbs_out_is_refused(string path)
    {
        Func<Task> mint = () => Proxy().MintFileAsync(path);

        (await mint.Should().ThrowAsync<PluginRefusedException>())
            .Which.Refusal.Code.Should()
            .Be(PluginRefusalCodes.FileOutsideGrant);
    }

    [Fact]
    public async Task An_absolute_path_is_refused()
    {
        Write("recordings/news.ts", [1]);

        Func<Task> mint = () =>
            Proxy().MintFileAsync(Path.Combine(DataFolder, "recordings", "news.ts"));

        (await mint.Should().ThrowAsync<PluginRefusedException>())
            .Which.Refusal.Code.Should()
            .Be(PluginRefusalCodes.FileOutsideGrant);
    }

    /// <summary>
    /// A plugin in its own process can make a link. One that led out of the
    /// folder would have the server read a file the plugin may not.
    /// </summary>
    [SkippableFact]
    public async Task A_link_inside_the_folder_is_refused()
    {
        string outside = Path.Combine(_pluginsRoot, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "secret.ts"), [1]);

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(DataFolder, "linked"), outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Skip.If(true, "This account may not create symbolic links.");
        }

        Func<Task> mint = () => Proxy().MintFileAsync("linked/secret.ts");

        (await mint.Should().ThrowAsync<PluginRefusedException>())
            .Which.Refusal.Code.Should()
            .Be(PluginRefusalCodes.FileOutsideGrant);
    }

    [Fact]
    public async Task A_file_that_is_not_there_gets_no_link()
    {
        Func<Task> mint = () => Proxy().MintFileAsync("recordings/missing.ts");

        (await mint.Should().ThrowAsync<PluginRefusedException>())
            .Which.Refusal.Why.Should()
            .Contain("recordings/missing.ts");
    }

    [Fact]
    public void A_file_removed_after_minting_opens_as_nothing()
    {
        Proxy().OpenFile("recordings/gone.ts").Should().BeNull();
    }

    /// <summary>The same declaration a proxied link needs.</summary>
    [Fact]
    public async Task A_plugin_without_the_media_proxy_capability_is_refused()
    {
        Write("recordings/news.ts", [1]);
        PluginRefusal refused = new(
            PluginRefusalCodes.CapabilityNotDeclared,
            LiveTv.ToString(),
            "The plugin used media.proxy.",
            "It did not declare it.",
            "Declare it.",
            PluginRefusalSeverity.Blocked
        );

        Func<Task> mint = () => Proxy(refused).MintFileAsync("recordings/news.ts");

        (await mint.Should().ThrowAsync<PluginRefusedException>())
            .Which.Refusal.Code.Should()
            .Be(PluginRefusalCodes.CapabilityNotDeclared);
    }

    [Fact]
    public async Task A_host_that_wired_no_folder_says_so_by_name()
    {
        PluginMediaProxy proxy = new(
            LiveTv,
            new Caller(Viewer),
            new Broker(null),
            _minter,
            new HttpClient(),
            NullLogger.Instance
        );

        Func<Task> mint = () => proxy.MintFileAsync("recordings/news.ts");

        (await mint.Should().ThrowAsync<PluginRefusedException>())
            .Which.Refusal.What.Should()
            .Contain("MintFileAsync");
    }

    /// <summary>
    /// Only MintFile makes a file ticket. A link a plugin hands to MintAsync,
    /// a file address included, never makes the server serve a file.
    /// </summary>
    [Fact]
    public void A_link_minted_from_a_file_address_is_not_a_file_ticket()
    {
        string ticket = _minter.Mint(
            LiveTv,
            Viewer,
            new PluginProxyRequest { Links = [new() { Url = new("file:///etc/passwd") }] },
            TimeSpan.FromMinutes(15)
        );

        _minter.Read(ticket)!.FilePath.Should().BeNull();
    }

    private PluginMediaProxy Proxy(PluginRefusal? refusal = null)
    {
        LocalStorageDriver driver = new();
        IStorage files = new LocalStorage(driver, new StoragePathGuard([_pluginsRoot], driver));

        return new(
            LiveTv,
            new Caller(Viewer),
            new Broker(refusal),
            _minter,
            new HttpClient(),
            NullLogger.Instance,
            dataFolder: DataFolder,
            files: files
        );
    }

    private void Write(string relative, byte[] bytes)
    {
        string full = Path.Combine(DataFolder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private sealed class Caller(Guid userId) : IPluginCallerAccessor
    {
        public Guid CurrentUserId => userId;
    }

    private sealed class Broker(PluginRefusal? refusal) : IPluginCapabilityBroker
    {
        public PluginRefusal? Check(Ulid pluginId, string capability, string? scopeValue = null) =>
            refusal;
    }
}
