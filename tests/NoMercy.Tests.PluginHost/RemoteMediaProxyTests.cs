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
using NoMercy.PluginHost;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Ipc;
using Xunit;

namespace NoMercy.Tests.PluginHost;

/// <summary>
/// A plugin in its own process asks the server for a link to a file in its
/// folder. Only the path crosses: the server checks and serves the file.
/// </summary>
[Trait("Category", "Unit")]
public class RemoteMediaProxyTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    [Fact]
    public async Task A_file_link_is_asked_for_by_its_path_and_comes_back_minted()
    {
        RecordingBroker broker = new(
            PluginCallResponse.Value(
                """{"url":"/api/v1/plugins/x/media/ticket","expiresAt":"2030-01-01T00:00:00+00:00","media":"01JBQ0Y1ZQ8W4T7N2M6K5R3H9A"}"""
            )
        );
        RemoteMediaProxy proxy = new(new RemoteCall(PluginId, broker));

        PluginMediaUrl url = await proxy.MintFileAsync("recordings/news.ts");

        url.Url.ToString().Should().Be("/api/v1/plugins/x/media/ticket");
        broker.Last!.Facade.Should().Be("media.proxy");
        broker.Last.Member.Should().Be(nameof(IPluginMediaProxy.MintFileAsync));
        JsonDocument
            .Parse(broker.Last.PayloadJson)
            .RootElement.GetProperty("path")
            .GetString()
            .Should()
            .Be("recordings/news.ts");
    }
}
