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
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Newtonsoft.Json;
using NoMercy.Api.Controllers.V1.Dashboard.Media;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Database.Models.Libraries;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// Reflection-only tests for <see cref="InboxController"/>.
///
/// Verifies:
///   1. Every expected route + verb exists on InboxController.
///   2. InboxAssignRequest binds from a representative JSON payload (contract test).
/// </summary>
[Trait("Category", "Routes")]
public partial class InboxController_Routes_Test
{
    // -------------------------------------------------------------------------
    // Helpers — mirror EncoderProfilesController_Routes_Test
    // -------------------------------------------------------------------------

    private static IEnumerable<MethodInfo> PublicActions(Type controller) =>
        controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any());

    private static MethodInfo? FindAction(
        Type controller,
        Type httpVerbAttribute,
        string routeSuffix
    )
    {
        foreach (MethodInfo method in PublicActions(controller))
        {
            HttpMethodAttribute? attr = method
                .GetCustomAttributes(httpVerbAttribute, inherit: false)
                .Cast<HttpMethodAttribute>()
                .FirstOrDefault();

            if (attr is null)
                continue;

            string template = attr.Template ?? string.Empty;

            if (
                string.Equals(
                    StripConstraints(template),
                    StripConstraints(routeSuffix),
                    StringComparison.OrdinalIgnoreCase
                )
            )
                return method;
        }

        return null;
    }

    private static string StripConstraints(string template) =>
        RouteConstraint().Replace(template, "{$1}");

    [GeneratedRegex(@"\{(\w+):[^}]+\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RouteConstraint();

    private static void AssertEndpointExists(
        Type controller,
        Type httpVerb,
        string routeSuffix,
        string description
    )
    {
        MethodInfo? method = FindAction(controller, httpVerb, routeSuffix);
        Assert.True(
            method is not null,
            $"Missing endpoint on {controller.Name}: {description} (route suffix: \"{routeSuffix}\")"
        );
    }

    // -------------------------------------------------------------------------
    // Route surface assertions
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(typeof(HttpGetAttribute), "", "GET / (list)")]
    [InlineData(typeof(HttpGetAttribute), "{id}", "GET /{id}")]
    [InlineData(typeof(HttpGetAttribute), "{id}/matches", "GET /{id}/matches")]
    [InlineData(typeof(HttpPostAttribute), "{id}/assign", "POST /{id}/assign")]
    [InlineData(typeof(HttpPostAttribute), "{id}/dismiss", "POST /{id}/dismiss")]
    [InlineData(typeof(HttpDeleteAttribute), "{id}", "DELETE /{id}")]
    public void InboxController_HasExpectedEndpoint(
        Type httpVerb,
        string routeSuffix,
        string description
    )
    {
        AssertEndpointExists(typeof(InboxController), httpVerb, routeSuffix, description);
    }

    // -------------------------------------------------------------------------
    // InboxAssignRequest contract test
    // -------------------------------------------------------------------------

    [Fact]
    public void InboxAssignRequest_DeserializesFromClientJson()
    {
        string json = """
            {
              "type": "movie",
              "match": {
                "provider": "tmdb",
                "external_id": "603",
                "title": "The Matrix",
                "year": 1999,
                "poster_path": "/f89U3ADr1oiB1s9GkdPOEpXUk5H.jpg",
                "score": 0.97
              },
              "target_library_id": "01HZXY7ABCDEF0123456789012",
              "target_folder_id":  "01HZXY7ABCDEF0123456789013",
              "target_profile_id": "01HZXY7ABCDEF0123456789014"
            }
            """;

        InboxAssignRequest? request = JsonConvert.DeserializeObject<InboxAssignRequest>(json);

        Assert.NotNull(request);
        Assert.Equal("movie", request!.Type);
        Assert.NotNull(request.Match);
        Assert.Equal("tmdb", request.Match.Provider);
        Assert.Equal("603", request.Match.ExternalId);
        Assert.Equal("The Matrix", request.Match.Title);
        Assert.Equal(1999, request.Match.Year);
        Assert.Equal(0.97, request.Match.Score, precision: 5);
        Assert.Equal(Ulid.Parse("01HZXY7ABCDEF0123456789012"), request.TargetLibraryId);
        Assert.Equal(Ulid.Parse("01HZXY7ABCDEF0123456789013"), request.TargetFolderId);
        Assert.Equal(Ulid.Parse("01HZXY7ABCDEF0123456789014"), request.TargetProfileId);
    }

    [Fact]
    public void InboxItemDto_ProjectsFromInboxItem()
    {
        Ulid itemId = Ulid.NewUlid();

        InboxItem item = new()
        {
            Id = itemId,
            SourcePath = "inbox/The Matrix (1999).mkv",
            DriverId = Ulid.NewUlid(),
            DetectedType = "movie",
            Confidence = "high",
            Status = "NeedsReview",
            Candidates =
            [
                new()
                {
                    Provider = "tmdb",
                    ExternalId = "603",
                    Title = "The Matrix",
                    Year = 1999,
                    Score = 0.98,
                },
            ],
        };

        InboxItemDto dto = new(item);

        Assert.Equal(itemId.ToString(), dto.Id);
        Assert.Equal("inbox/The Matrix (1999).mkv", dto.SourcePath);
        Assert.Equal("movie", dto.DetectedType);
        Assert.Equal("high", dto.Confidence);
        Assert.Equal("NeedsReview", dto.Status);
        Assert.Single(dto.Candidates);
        Assert.Equal("The Matrix", dto.Candidates[0].Title);
        Assert.Null(dto.SelectedMatch);
        Assert.Null(dto.TargetLibraryId);
    }
}
