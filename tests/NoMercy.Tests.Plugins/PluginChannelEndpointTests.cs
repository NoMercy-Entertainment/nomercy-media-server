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

using System.Text;
using FluentAssertions;
using NoMercy.NmSystem.Information;
using NoMercy.PluginSdk.Ipc;
using Xunit;

namespace NoMercy.Tests.Plugins;

[Trait("Category", "Unit")]
public class PluginChannelEndpointTests
{
    private static readonly Ulid PluginId = Ulid.Parse("01J9ZK5V8Y0000000000000000");

    [Fact]
    public void BrokerAndHost_AreDifferentEndpointsForTheSamePlugin()
    {
        PluginChannelEndpoints
            .BrokerFor(PluginId)
            .Should()
            .NotBe(PluginChannelEndpoints.HostFor(PluginId));
    }

    [Fact]
    public void Endpoints_AreScopedToOnePlugin()
    {
        Ulid other = Ulid.NewUlid();

        PluginChannelEndpoints
            .BrokerFor(PluginId)
            .Should()
            .NotBe(PluginChannelEndpoints.BrokerFor(other));
    }

    [SkippableFact]
    public void OnWindows_TheEndpointIsAPipeNameCarryingThePluginId()
    {
        Skip.IfNot(Software.IsWindows);

        PluginChannelEndpoints.BrokerFor(PluginId).Should().Be($"NoMercy.Plugin.{PluginId}.broker");
    }

    [SkippableFact]
    public void OnUnix_TheEndpointIsASocketUnderThePluginRunDirectory()
    {
        Skip.If(Software.IsWindows);

        PluginChannelEndpoints
            .BrokerFor(PluginId)
            .Should()
            .Be(Path.Combine(PluginChannelEndpoints.RunDirectoryFor(PluginId), "broker.sock"));
    }

    /// <summary>
    /// The socket path no longer grows with the data folder. Under it, a
    /// data folder of a dozen more bytes than the test runner's broke every
    /// out-of-process plugin: .NET refuses a socket path past the limit.
    /// </summary>
    [SkippableFact]
    public void OnUnix_TheSocketPathFitsTheLimitWhateverTheDataFolder()
    {
        Skip.If(Software.IsWindows);

        foreach (
            string endpoint in new[]
            {
                PluginChannelEndpoints.BrokerFor(PluginId),
                PluginChannelEndpoints.HostFor(PluginId),
            }
        )
        {
            Encoding
                .UTF8.GetByteCount(endpoint)
                .Should()
                .BeLessThanOrEqualTo(PluginChannelEndpoints.MaxSocketPathBytes);
            endpoint.Should().NotStartWith(AppFiles.PluginsPath);
        }
    }

    /// <summary>The folder the sockets live in is this account's alone.</summary>
    [SkippableFact]
    public void OnUnix_TheSocketFolderIsPrivateToThisAccount()
    {
        Skip.If(Software.IsWindows);
        string root = Path.GetDirectoryName(PluginChannelEndpoints.RunDirectoryFor(PluginId))!;

        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(root)
                .Should()
                .Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        new DirectoryInfo(root).LinkTarget.Should().BeNull();
        Path.GetFileName(root).Should().StartWith("nomercy-");
    }

    /// <summary>
    /// A socket that a real listener binds and a real client reaches, on a
    /// machine whose data folder is longer than the old layout could hold.
    /// </summary>
    [SkippableFact]
    public async Task OnUnix_ASocketBindsAndConnectsAtTheEndpoint()
    {
        Skip.If(Software.IsWindows);
        string endpoint = PluginChannelEndpoints.HostFor(Ulid.NewUlid());
        PluginLocalTransport.ClearStaleEndpoint(endpoint);

        using System.Net.Sockets.Socket listener = new(
            System.Net.Sockets.AddressFamily.Unix,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Unspecified
        );
        listener.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(endpoint));
        listener.Listen(1);

        await using Stream client = await PluginLocalTransport.ConnectAsync(endpoint, default);

        client.CanWrite.Should().BeTrue();
        PluginLocalTransport.ClearStaleEndpoint(endpoint);
    }

    [Fact]
    public void TheUsersRuntimeFolderIsUsedWhenItExistsAndIsShortEnough()
    {
        PluginChannelEndpoints.ParentFor("/run/user/1000", _ => true).Should().Be("/run/user/1000");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/runtime")]
    public void WithoutAUsableRuntimeFolder_TheSocketsGoUnderTmp(string? runtime)
    {
        PluginChannelEndpoints.ParentFor(runtime, _ => true).Should().Be("/tmp");
    }

    [Fact]
    public void ARuntimeFolderThatDoesNotExist_FallsBackToTmp()
    {
        PluginChannelEndpoints.ParentFor("/run/user/1000", _ => false).Should().Be("/tmp");
    }

    /// <summary>
    /// A runtime folder can be long on its own. One that would push a socket
    /// past the limit is passed over for /tmp rather than used and broken.
    /// </summary>
    [Fact]
    public void ARuntimeFolderTooLongForASocket_FallsBackToTmp()
    {
        string tooLong = "/home/" + new string('a', 80);

        PluginChannelEndpoints.Fits(tooLong).Should().BeFalse();
        PluginChannelEndpoints.ParentFor(tooLong, _ => true).Should().Be("/tmp");
    }

    [Fact]
    public void Tmp_AlwaysLeavesRoomForTheLongestSocket()
    {
        PluginChannelEndpoints.Fits("/tmp").Should().BeTrue();
    }

    [Fact]
    public void ARefusalCrossesTheWireWithEveryTeachingField()
    {
        WireRefusal refusal = new(
            "PLUGIN_HOST_UNAVAILABLE",
            "Internet Radio 2.0.0",
            "The server could not reach the plugin's process.",
            "The plugin process is starting, restarting after a crash, or was disabled.",
            "Open the plugin's health page and press Restart, or check the server log for PLUGIN_HOST_CRASHED. Docs: /nomercy-plugins/handbook/runtime-and-isolation",
            "blocked"
        );

        PluginCallResponse response = PluginCallResponse.Refused(refusal);

        response.Ok.Should().BeFalse();
        response.Refusal!.Fix.Should().Contain("/nomercy-plugins/");
    }
}
