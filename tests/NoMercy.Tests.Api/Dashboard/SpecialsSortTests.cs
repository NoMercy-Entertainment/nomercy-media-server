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
using System.Text;
using System.Text.Json;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// PATCH /dashboard/specials/sort answered "Successfully sorted specials." without
/// reading the posted order or saving anything, so the dashboard list never moved.
/// The order a moderator posts has to be the order the list comes back in.
/// </summary>
[Trait("Category", "DashboardSpecialsSort")]
public class SpecialsSortTests : IClassFixture<NoMercyApiFactory>
{
    private readonly HttpClient _client;

    public SpecialsSortTests(NoMercyApiFactory factory)
    {
        _client = factory.CreateClient().AsAuthenticated();
    }

    private static StringContent JsonBody(object obj) =>
        new(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");

    private async Task<string> CreateSpecialAsync()
    {
        HttpResponseMessage response = await _client.PostAsync(
            "/api/v1/dashboard/specials",
            JsonBody(new { })
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetString()!;
    }

    private Task<HttpResponseMessage> SortAsync(params (string Id, int Order)[] items) =>
        _client.PatchAsync(
            "/api/v1/dashboard/specials/sort",
            JsonBody(new { libraries = items.Select(item => new { id = item.Id, order = item.Order }) })
        );

    private async Task<List<string>> ListedSpecialIdsAsync()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/v1/dashboard/specials");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc
            .RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToList();
    }

    [Fact]
    public async Task Sorting_specials_changes_the_order_the_dashboard_lists_them_in()
    {
        string first = await CreateSpecialAsync();
        string second = await CreateSpecialAsync();

        HttpResponseMessage swapped = await SortAsync((second, 1), (first, 2));
        swapped.StatusCode.Should().Be(HttpStatusCode.OK);

        List<string> ids = await ListedSpecialIdsAsync();
        ids.IndexOf(second)
            .Should()
            .BeLessThan(
                ids.IndexOf(first),
                "the sort call put the second special in front of the first"
            );

        HttpResponseMessage restored = await SortAsync((first, 1), (second, 2));
        restored.StatusCode.Should().Be(HttpStatusCode.OK);

        ids = await ListedSpecialIdsAsync();
        ids.IndexOf(first)
            .Should()
            .BeLessThan(
                ids.IndexOf(second),
                "the swapped sort call put the first special back in front"
            );
    }
}
