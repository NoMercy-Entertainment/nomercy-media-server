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
        "AudioTracks(NoMercy.Api.Hubs.CastHub.AudioTrack[]) -> System.Threading.Tasks.Task",
        "CastPlaylist(System.String) -> System.Threading.Tasks.Task",
        "CurrentAudioTrack(NoMercy.Api.Hubs.CastHub.AudioTrack) -> System.Threading.Tasks.Task",
        "CurrentSubtitleTrack(NoMercy.Api.Hubs.CastHub.TextTrack) -> System.Threading.Tasks.Task",
        "Disconnect() -> System.Threading.Tasks.Task",
        "Ended() -> System.Threading.Tasks.Task",
        "GetChromeCasts() -> System.String[]",
        "GetChromecastStatus() -> Sharpcaster.Models.ChromecastStatus.ChromecastStatus",
        "GetMediaStatus() -> Sharpcaster.Models.Media.MediaStatus",
        "GetPlayerState() -> System.Threading.Tasks.Task",
        "Item(NoMercy.Api.Hubs.CastHub.PlaylistItem) -> System.Threading.Tasks.Task",
        "Launch() -> System.Threading.Tasks.Task",
        "Muted(System.Boolean) -> System.Threading.Tasks.Task",
        "Pause() -> System.Threading.Tasks.Task",
        "Play() -> System.Threading.Tasks.Task",
        "PlayerState(NoMercy.Api.Hubs.CastHub.CastPlayerState) -> System.Threading.Tasks.Task",
        "Playlist(NoMercy.Api.Hubs.CastHub.PlaylistItem[]) -> System.Threading.Tasks.Task",
        "SelectChromecast(System.String) -> System.Threading.Tasks.Task",
        "SetAudioTrack(System.Int32) -> System.Threading.Tasks.Task",
        "SetMuted(System.Boolean) -> System.Threading.Tasks.Task",
        "SetNext() -> System.Threading.Tasks.Task",
        "SetPause() -> System.Threading.Tasks.Task",
        "SetPlay() -> System.Threading.Tasks.Task",
        "SetPlaylistItem(System.Int32) -> System.Threading.Tasks.Task",
        "SetPrevious() -> System.Threading.Tasks.Task",
        "SetSeek(System.Int32) -> System.Threading.Tasks.Task",
        "SetStop() -> System.Threading.Tasks.Task",
        "SetSubtitleTrack(System.Int32) -> System.Threading.Tasks.Task",
        "SetVolume(System.Int32) -> System.Threading.Tasks.Task",
        "Stop() -> System.Threading.Tasks.Task",
        "SubtitleTracks(NoMercy.Api.Hubs.CastHub.TextTrack[]) -> System.Threading.Tasks.Task",
        "Time(NoMercy.Api.Hubs.CastHub.TimeData) -> System.Threading.Tasks.Task",
        "Volume(System.Int32) -> System.Threading.Tasks.Task",
    ];

    private static readonly string[] ContentAnalysisHubMethods = [];

    private static readonly string[] DashboardHubMethods =
    [
        "StartResources() -> System.Void",
        "StopResources() -> System.Void",
    ];

    private static readonly string[] DeviceHubMethods =
    [
        "DeclareCapabilities(NoMercy.Encoder.Devices.DeviceCapabilities) -> System.Threading.Tasks.Task",
        "GetDevices() -> System.Threading.Tasks.Task<System.Collections.Generic.List<NoMercy.Networking.Devices.DeviceListItem>>",
        "PendingNotices() -> System.Threading.Tasks.Task<System.Collections.Generic.List<NoMercy.Api.Hubs.DeviceDropNoticeDto>>",
        "WakeForMusic(System.String) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.WakeResult>",
        "WakeForVideo(System.String) -> System.Threading.Tasks.Task<NoMercy.Api.Hubs.WakeResult>",
    ];

    private static readonly string[] DrivesHubMethods = [];

    private static readonly string[] LiveTranscodeHubMethods =
    [
        "Heartbeat(System.String) -> System.Void",
        "ReportBufferHealth(System.String, System.Double, System.Double) -> System.Void",
        "ReportPlayhead(System.String, System.Double) -> System.Void",
        "RequestPause(System.String) -> System.Void",
        "RequestResume(System.String) -> System.Void",
        "SubscribeToSession(System.String) -> System.Threading.Tasks.Task",
        "UnsubscribeFromSession(System.String) -> System.Threading.Tasks.Task",
    ];

    private static readonly string[] MusicHubMethods =
    [
        "ChangeDeviceCommand(System.String) -> System.Threading.Tasks.Task",
        "ChangeVolumeCommand(System.Nullable<System.Int32>) -> System.Threading.Tasks.Task",
        "CrossfadeCompleteCommand(System.Nullable<System.Guid>) -> System.Threading.Tasks.Task",
        "CrossfadeStartCommand(System.Nullable<System.Int32>) -> System.Threading.Tasks.Task",
        "CurrentTimeCommand(System.Nullable<System.Int32>) -> System.Threading.Tasks.Task",
        "CurrentTimeForItemCommand(System.Nullable<System.Double>, System.String) -> System.Threading.Tasks.Task",
        "GetServerTime() -> System.Int64",
        "GetStateCommand() -> NoMercy.Api.Services.Music.MusicPlayerState",
        "PlaybackCommand(System.String, System.Object) -> System.Threading.Tasks.Task",
        "ReportPositionAtCommand(System.Nullable<System.Int32>, System.Nullable<System.Int64>) -> System.Threading.Tasks.Task",
        "ReportPositionCommand(System.Nullable<System.Int32>) -> System.Threading.Tasks.Task",
        "ReportPositionForItemCommand(System.Nullable<System.Int64>, System.String) -> System.Threading.Tasks.Task",
        "SetDeviceVolumeCommand(System.String, System.Nullable<System.Int32>) -> System.Threading.Tasks.Task",
        "StartPlaybackCommand(System.String, System.Nullable<System.Guid>, System.Nullable<System.Guid>) -> System.Threading.Tasks.Task",
    ];

    private static readonly string[] RipperHubMethods =
    [
        "GetDriveState(System.String) -> System.Threading.Tasks.Task<System.Object>",
    ];

    private static readonly string[] VideoHubMethods =
    [
        "ChangeDeviceCommand(System.String) -> System.Threading.Tasks.Task",
        "GetStateCommand() -> NoMercy.Api.Services.Video.VideoPlayerState",
        "PlaybackCommand(System.String, System.Object) -> System.Threading.Tasks.Task",
        "RemoveWatched(NoMercy.Api.Services.Video.VideoProgressRequest) -> System.Threading.Tasks.Task",
        "SetTime(NoMercy.Api.Services.Video.VideoProgressRequest) -> System.Threading.Tasks.Task",
        "StartPlaybackCommand(System.String, System.Object, System.Nullable<System.Int32>) -> System.Threading.Tasks.Task",
    ];

    /// <summary>
    /// One hub for every plugin, multiplexed by group. <see cref="PluginHubMethods"/>
    /// is the surface a client talks to; what a plugin does behind
    /// <c>Send</c> is the plugin's own contract and not part of this one.
    /// </summary>
    private static readonly string[] PluginHubMethods =
    [
        "Send(System.String, System.String, System.Text.Json.Nodes.JsonNode) -> System.Threading.Tasks.Task<System.Boolean>",
        "Subscribe(System.String) -> System.Threading.Tasks.Task",
        "Unsubscribe(System.String) -> System.Threading.Tasks.Task",
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
                new JsonSerializerOptions { WriteIndented = true }
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
