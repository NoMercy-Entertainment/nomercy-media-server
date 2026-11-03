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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NoMercy.Api.DTOs.Media.Components;
using Xunit;

namespace NoMercy.Tests.Api.Contracts;

/// <summary>
/// openapi-nm-components.yaml is written by hand, so these tests hold the track
/// row schemas to the JSON the server really sends for TrackRowData.
/// </summary>
[Trait("Category", "Contract")]
public class NmComponentsTrackRowSpecTests
{
    private const string SpecFileName = "openapi-nm-components.yaml";
    private const string TrackArtistRef = "#/components/schemas/TrackArtist";

    [Fact]
    public void NMTrackRowProps_ListsExactlyTheFieldsTrackRowDataSends()
    {
        Dictionary<string, List<string>> specProperties = SchemaProperties("NMTrackRowProps");

        specProperties.Keys.Should().BeEquivalentTo(SerialisedPropertyNames(new TrackRowData()));
    }

    [Theory]
    [InlineData("artist_track")]
    [InlineData("album_track")]
    public void NMTrackRowProps_TypesTheLinkListAsTrackArtist(string property)
    {
        Dictionary<string, List<string>> specProperties = SchemaProperties("NMTrackRowProps");

        specProperties.Should().ContainKey(property);
        ItemsRef(specProperties[property]).Should().Be(TrackArtistRef);
    }

    [Fact]
    public void TrackArtistSchema_ListsExactlyTheFieldsTrackArtistSends()
    {
        Dictionary<string, List<string>> specProperties = SchemaProperties("TrackArtist");

        specProperties.Keys.Should().BeEquivalentTo(SerialisedPropertyNames(new TrackArtist()));
    }

    private static List<string> SerialisedPropertyNames(object dto)
    {
        JsonSerializer serializer = JsonSerializer.Create(
            new() { NullValueHandling = NullValueHandling.Include }
        );
        return JObject.FromObject(dto, serializer).Properties().Select(p => p.Name).ToList();
    }

    /// <summary>
    /// Reads the property names of one schema under components.schemas, with the
    /// lines that describe each. The spec keeps a fixed two-space indent: schema
    /// names at 4, their keys at 6, property names at 8.
    /// </summary>
    private static Dictionary<string, List<string>> SchemaProperties(string schema)
    {
        string[] lines = File.ReadAllLines(SpecPath());
        int start = Array.IndexOf(lines, $"    {schema}:");
        Assert.True(start >= 0, $"{schema} is missing from {SpecFileName}");

        Dictionary<string, List<string>> properties = new();
        bool inProperties = false;
        List<string>? current = null;

        for (int i = start + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0)
                continue;

            int indent = line.Length - line.TrimStart().Length;
            if (indent <= 4)
                break;

            if (indent == 6)
            {
                inProperties = line.Trim() == "properties:";
                current = null;
                continue;
            }

            if (!inProperties)
                continue;

            if (indent == 8)
            {
                current = [];
                properties[line.Trim().TrimEnd(':')] = current;
                continue;
            }

            current?.Add(line.Trim());
        }

        return properties;
    }

    private static string? ItemsRef(List<string> propertyLines)
    {
        int items = propertyLines.IndexOf("items:");
        if (items < 0 || items + 1 >= propertyLines.Count)
            return null;

        string next = propertyLines[items + 1];
        return next.StartsWith("$ref:") ? next["$ref:".Length..].Trim().Trim('\'', '"') : null;
    }

    private static string SpecPath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (
            directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Server.sln"))
        )
            directory = directory.Parent;

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, SpecFileName);
    }
}
