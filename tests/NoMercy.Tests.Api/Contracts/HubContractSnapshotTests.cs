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

using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using NoMercy.Api.Hubs;
using NoMercy.Networking;
using Xunit;

namespace NoMercy.Tests.Api.Contracts;

[Trait("Category", "Contract")]
public class HubContractSnapshotTests
{
    private static readonly string[] ConnectionHubBaseMethods =
    [
        "Devices() -> System.Collections.Generic.List<NoMercy.Database.Models.Users.Device>",
        "GetCountryFromContext() -> System.String",
        "GetLanguageFromContext() -> System.String",
        "OnConnectedAsync() -> System.Threading.Tasks.Task",
        "OnDisconnectedAsync(System.Exception) -> System.Threading.Tasks.Task",
    ];

    private static readonly string[] CastHubMethods =
    [
        "AudioTracks(NoMercy.Api.Hubs.CastHub.AudioTrack[]) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "CastPlaylist(System.String) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "CurrentAudioTrack(NoMercy.Api.Hubs.CastHub.AudioTrack) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "CurrentSubtitleTrack(NoMercy.Api.Hubs.CastHub.TextTrack) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "Disconnect() -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "Ended() -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "GetChromeCasts() -> System.String[]",
        "GetChromecastStatus() -> Sharpcaster.Models.ChromecastStatus.ChromecastStatus",
        "GetMediaStatus() -> Sharpcaster.Models.Media.MediaStatus",
        "GetPlayerState() -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "Item(NoMercy.Api.Hubs.CastHub.PlaylistItem) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "Launch() -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "Muted(System.Boolean) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "Pause() -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "Play() -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "PlayerState(NoMercy.Api.Hubs.CastHub.CastPlayerState) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "Playlist(NoMercy.Api.Hubs.CastHub.PlaylistItem[]) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "SelectChromecast(System.String) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "SetAudioTrack(System.Int32) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "SetMuted(System.Boolean) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "SetNext() -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "SetPause() -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "SetPlay() -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "SetPlaylistItem(System.Int32) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "SetPrevious() -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "SetSeek(System.Int32) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "SetStop() -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "SetSubtitleTrack(System.Int32) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "SetVolume(System.Int32) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "Stop() -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "SubtitleTracks(NoMercy.Api.Hubs.CastHub.TextTrack[]) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "Time(NoMercy.Api.Hubs.CastHub.TimeData) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "Volume(System.Int32) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
    ];

    private static readonly string[] ContentAnalysisHubMethods = [];

    private static readonly string[] DashboardHubMethods =
    [
        "StartResources() -> NoMercy.Api.Hubs.HubCommandResult",
        "StopResources() -> NoMercy.Api.Hubs.HubCommandResult",
    ];

    private static readonly string[] DeviceHubMethods =
    [
        "DeclareCapabilities(NoMercy.Encoder.Devices.DeviceCapabilities) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "GetDevices() -> System.Threading.Tasks.Task<System.Collections.Generic.List<NoMercy.Networking.Devices.DeviceListItem>>",
        "PendingNotices() -> System.Threading.Tasks.Task<System.Collections.Generic.List<NoMercy.Api.Hubs.DeviceDropNoticeDto>>",
        "WakeForMusic(System.String) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "WakeForVideo(System.String) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
    ];

    private static readonly string[] DrivesHubMethods = [];

    private static readonly string[] LiveTranscodeHubMethods =
    [
        "Heartbeat(System.String) -> NoMercy.Api.Hubs.HubCommandResult",
        "ReportBufferHealth(System.String, System.Double, System.Double) -> NoMercy.Api.Hubs.HubCommandResult",
        "ReportPlayhead(System.String, System.Double) -> NoMercy.Api.Hubs.HubCommandResult",
        "RequestPause(System.String) -> NoMercy.Api.Hubs.HubCommandResult",
        "RequestResume(System.String) -> NoMercy.Api.Hubs.HubCommandResult",
        "SubscribeToSession(System.String) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "UnsubscribeFromSession(System.String) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
    ];

    private static readonly string[] MusicHubMethods =
    [
        "ChangeDeviceCommand(System.String) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "ChangeVolumeCommand(System.Nullable<System.Int32>) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "CrossfadeCompleteCommand(System.Nullable<System.Guid>) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "CrossfadeStartCommand(System.Nullable<System.Int32>) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "CurrentTimeCommand(System.Nullable<System.Int32>) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "CurrentTimeForItemCommand(System.Nullable<System.Double>, System.String) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "GetServerTime() -> System.Int64",
        "GetStateCommand() -> NoMercy.Api.Services.Music.MusicPlayerState",
        "PlaybackCommand(System.String, System.Object) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "ReportPositionAtCommand(System.Nullable<System.Int32>, System.Nullable<System.Int64>) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "ReportPositionCommand(System.Nullable<System.Int32>) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "ReportPositionForItemCommand(System.Nullable<System.Int64>, System.String) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "SetDeviceVolumeCommand(System.String, System.Nullable<System.Int32>) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "StartPlaybackCommand(System.String, System.Nullable<System.Guid>, System.Nullable<System.Guid>) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
    ];

    private static readonly string[] RipperHubMethods =
    [
        "GetDriveState(System.String) -> System.Threading.Tasks.Task<System.Object>",
    ];

    private static readonly string[] VideoHubMethods =
    [
        "ChangeDeviceCommand(System.String) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "GetStateCommand() -> NoMercy.Api.Services.Video.VideoPlayerState",
        "PlaybackCommand(System.String, System.Object) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "RemoveWatched(NoMercy.Api.Services.Video.VideoProgressRequest) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "SetTime(NoMercy.Api.Services.Video.VideoProgressRequest) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "StartPlaybackCommand(System.String, System.Object, System.Nullable<System.Int32>) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
    ];

    /// <summary>
    /// One hub for every plugin, multiplexed by group. <see cref="PluginHubMethods"/>
    /// is the surface a client talks to; what a plugin does behind
    /// <c>Send</c> is the plugin's own contract and not part of this one.
    /// </summary>
    private static readonly string[] PluginHubMethods =
    [
        "Send(System.String, System.String, System.Text.Json.Nodes.JsonNode) -> System.Threading.Tasks.Task<System.Boolean>",
        "Subscribe(System.String) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
        "Unsubscribe(System.String) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.HubCommandResult>",
    ];

    private static readonly string[] KnownHubTypeNames =
    [
        "CastHub",
        "ContentAnalysisHub",
        "DashboardHub",
        "DeviceHub",
        "DrivesHub",
        "LiveTranscodeHub",
        "MusicHub",
        "PluginHub",
        "RipperHub",
        "VideoHub",
    ];

    private static string FormatType(Type type)
    {
        if (type.IsArray)
            return FormatType(type.GetElementType()!) + "[]";

        if (type.IsGenericType)
        {
            Type definition = type.GetGenericTypeDefinition();
            string rawName = definition.FullName ?? definition.Name;
            string baseName = rawName[..rawName.IndexOf('`')].Replace('+', '.');
            string args = string.Join(", ", type.GetGenericArguments().Select(FormatType));
            return $"{baseName}<{args}>";
        }

        return (type.FullName ?? type.Name).Replace('+', '.');
    }

    private static string[] ActualHubMethods(Type hubType)
    {
        return hubType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.DeclaringType != typeof(object))
            .Where(m => m.DeclaringType != typeof(Hub))
            .Where(m => !m.IsSpecialName)
            .Select(m =>
            {
                string parameters = string.Join(
                    ", ",
                    m.GetParameters().Select(p => FormatType(p.ParameterType))
                );
                return $"{m.Name}({parameters}) -> {FormatType(m.ReturnType)}";
            })
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AssertHubContract(Type hubType, string[] hubSpecificMethods)
    {
        string[] expected = ConnectionHubBaseMethods
            .Concat(hubSpecificMethods)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        string[] actual = ActualHubMethods(hubType);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CastHub_MatchesLockedContract() =>
        AssertHubContract(typeof(CastHub), CastHubMethods);

    [Fact]
    public void ContentAnalysisHub_MatchesLockedContract() =>
        AssertHubContract(typeof(ContentAnalysisHub), ContentAnalysisHubMethods);

    [Fact]
    public void DashboardHub_MatchesLockedContract() =>
        AssertHubContract(typeof(DashboardHub), DashboardHubMethods);

    [Fact]
    public void DeviceHub_MatchesLockedContract() =>
        AssertHubContract(typeof(DeviceHub), DeviceHubMethods);

    [Fact]
    public void DrivesHub_MatchesLockedContract() =>
        AssertHubContract(typeof(DrivesHub), DrivesHubMethods);

    [Fact]
    public void LiveTranscodeHub_MatchesLockedContract() =>
        AssertHubContract(typeof(LiveTranscodeHub), LiveTranscodeHubMethods);

    [Fact]
    public void MusicHub_MatchesLockedContract() =>
        AssertHubContract(typeof(MusicHub), MusicHubMethods);

    [Fact]
    public void PluginHub_MatchesLockedContract() =>
        AssertHubContract(typeof(PluginHub), PluginHubMethods);

    [Fact]
    public void RipperHub_MatchesLockedContract() =>
        AssertHubContract(typeof(RipperHub), RipperHubMethods);

    [Fact]
    public void VideoHub_MatchesLockedContract() =>
        AssertHubContract(typeof(VideoHub), VideoHubMethods);

    [Fact]
    public void AllConnectionHubSubclasses_MatchTheKnownHubSet()
    {
        Assembly apiAssembly = typeof(CastHub).Assembly;

        string[] actualHubTypeNames = apiAssembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsPublic: true, IsAbstract: false })
            .Where(t => typeof(ConnectionHub).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        string[] expectedHubTypeNames = KnownHubTypeNames
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedHubTypeNames, actualHubTypeNames);
    }

    [Fact]
    public async Task SignalRContracts_MatchCommittedSnapshot()
    {
        Type[] hubTypes = typeof(CastHub)
            .Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsPublic: true, IsAbstract: false })
            .Where(t => typeof(ConnectionHub).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();

        SortedDictionary<string, string[]> contracts = new(StringComparer.Ordinal);
        foreach (Type hubType in hubTypes)
            contracts.Add(hubType.Name, ActualHubMethods(hubType));

        string json =
            JsonSerializer.Serialize(
                new { hubs = contracts },
                // LF like the committed file; the default is the OS newline, which fails on Windows.
                new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }
            ) + "\n";

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (
            directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Server.sln"))
        )
            directory = directory.Parent;

        Assert.NotNull(directory);
        string path = Path.Combine(directory.FullName, "signalr-contracts.json");
        if (Environment.GetEnvironmentVariable("NOMERCY_UPDATE_SIGNALR_CONTRACTS") == "1")
        {
            await File.WriteAllTextAsync(path, json);
            return;
        }

        Assert.Equal(await File.ReadAllTextAsync(path), json);
    }
}
