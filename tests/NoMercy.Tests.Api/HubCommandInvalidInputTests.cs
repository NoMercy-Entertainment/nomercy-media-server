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

using System.Runtime.CompilerServices;
using NoMercy.Api.Hubs;
using Xunit;

namespace NoMercy.Tests.Api;

[Trait("Category", "Unit")]
public class HubCommandInvalidInputTests
{
    [Fact]
    public async Task CastHub_RejectsNullTimeBeforeRouting()
    {
        CastHub hub = NewHub<CastHub>();
        HubCommandResult result = await hub.Time(null!);
        AssertInvalid(result);
    }

    [Fact]
    public async Task DeviceHub_RejectsNullCapabilitiesBeforeLookingUpDevice()
    {
        DeviceHub hub = NewHub<DeviceHub>();
        HubCommandResult result = await hub.DeclareCapabilities(null!);
        AssertInvalid(result);
    }

    [Fact]
    public void LiveTranscodeHub_RejectsEmptySessionBeforeLookingUpRuntime()
    {
        LiveTranscodeHub hub = NewHub<LiveTranscodeHub>();
        HubCommandResult result = hub.ReportPlayhead("", 5);
        AssertInvalid(result);
    }

    [Fact]
    public async Task MusicHub_RejectsMissingPlaybackIdsBeforeLookingUpUser()
    {
        MusicHub hub = NewHub<MusicHub>();
        HubCommandResult result = await hub.StartPlaybackCommand(null, null, null);
        AssertInvalid(result);
    }

    [Fact]
    public async Task PluginHub_RejectsMalformedPluginIdBeforeJoiningGroup()
    {
        PluginHub hub = NewHub<PluginHub>();
        HubCommandResult result = await hub.Subscribe("not-a-ulid");
        AssertInvalid(result);
    }

    [Fact]
    public async Task VideoHub_RejectsNullProgressBeforeLookingUpUser()
    {
        VideoHub hub = NewHub<VideoHub>();
        HubCommandResult result = await hub.SetTime(null!);
        AssertInvalid(result);
    }

    [Theory]
    [InlineData("volume", 101)]
    [InlineData("seek", -1)]
    [InlineData("item", -1)]
    public async Task VideoHub_RejectsOutOfRangePlaybackData(string command, int data)
    {
        VideoHub hub = NewHub<VideoHub>();
        HubCommandResult result = await hub.PlaybackCommand(command, data);
        AssertInvalid(result);
    }

    private static T NewHub<T>()
        where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static void AssertInvalid(HubCommandResult result)
    {
        Assert.False(result.Ok);
        Assert.Equal("invalid_input", result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }
}
