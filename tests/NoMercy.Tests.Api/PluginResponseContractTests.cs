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
using Newtonsoft.Json;
using NoMercy.Api.DTOs.Common;
using NoMercy.PluginSdk.Mvc;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// A plugin's responses have to look like the server's own, because the clients
/// parse them with the same code.
/// <para>
/// The server's envelope DTOs live in an assembly no plugin should bind to, so
/// the plugin side has its own. Two declarations of one shape drift, and the
/// drift is silent — a client reading <c>next_page</c> simply finds nothing.
/// Serialising both and comparing is what stops that.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class PluginResponseContractTests
{
    private static string Serialize(object value) => JsonConvert.SerializeObject(value);

    [Fact]
    public void The_data_envelope_is_identical_on_both_sides()
    {
        string server = Serialize(new DataResponseDto<string> { Data = "x" });
        string plugin = Serialize(new PluginDataResponse<string> { Data = "x" });

        plugin.Should().Be(server);
    }

    [Fact]
    public void The_status_envelope_is_identical_on_both_sides()
    {
        string server = Serialize(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Data = "x",
                Message = null,
            }
        );
        string plugin = Serialize(
            new PluginStatusResponse<string>
            {
                Status = "ok",
                Data = "x",
                Message = null,
            }
        );

        plugin.Should().Be(server);
    }

    [Fact]
    public void The_paginated_envelope_is_identical_on_both_sides()
    {
        // next_page and has_more specifically: camelCase here is a client that
        // silently stops paginating.
        string server = Serialize(
            new PaginatedResponse<string>
            {
                Data = ["a"],
                NextPage = 2,
                HasMore = true,
            }
        );
        string plugin = Serialize(
            new PluginPaginatedResponse<string>
            {
                Data = ["a"],
                NextPage = 2,
                HasMore = true,
            }
        );

        plugin.Should().Be(server);
        plugin.Should().Contain("next_page").And.Contain("has_more");
    }
}
