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
using Newtonsoft.Json.Serialization;
using NoMercy.MediaProcessing.Files;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// Pins the wire shape of the `dashboard/server/filelist` payload. Every dashboard
/// client picks files by these key names, and an unmatched file is told apart from a
/// matched one by the value of match.id — so a rename here silently empties an
/// operator's add-content list rather than failing loudly.
/// </summary>
public class FileItemSerializationTests
{
    // Mirrors the MVC pipeline: AddNewtonsoftJson sets no ContractResolver, so
    // property names fall through to Newtonsoft's defaults.
    private static readonly JsonSerializerSettings MvcSettings = new()
    {
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        DateFormatHandling = DateFormatHandling.IsoDateFormat,
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        ContractResolver = new DefaultContractResolver(),
    };

    private static JObject Serialize(FileItem item) =>
        JObject.Parse(JsonConvert.SerializeObject(item, MvcSettings));

    [Fact]
    public void FileItem_UsesAttributeNames_ForItsOwnProperties()
    {
        FileItem item = new()
        {
            Name = "Frieren S02E01",
            Path = "/downloads/Frieren/S02E01.mkv",
            Parent = "/downloads/Frieren",
            Size = 1234,
        };

        JObject json = Serialize(item);

        json.Should().ContainKey("name");
        json.Should().ContainKey("path");
        json.Should().ContainKey("parent");
        json.Should().ContainKey("size");
        json.Should().ContainKey("parsed");
        json.Should().ContainKey("match");
    }

    [Fact]
    public void Match_UsesSnakeCaseEpisodeKeys()
    {
        FileItem item = new()
        {
            Match = new()
            {
                Id = 1234,
                Title = "The Chosen One",
                SeasonNumber = 2,
                EpisodeNumber = 1,
            },
        };

        JObject match = (JObject)Serialize(item)["match"]!;

        match.Should().ContainKey("id");
        match.Should().ContainKey("title");
        match.Should().ContainKey("season_number");
        match.Should().ContainKey("episode_number");
    }

    /// <summary>
    /// MovieFile is third-party and carries no serialization attributes, so it is
    /// projected before it reaches the wire. Without that projection its CLR names
    /// land in a payload that is snake_case everywhere else, and every client
    /// reading `parsed.title` gets undefined.
    /// </summary>
    [Fact]
    public void Parsed_IsProjected_SoItsKeysMatchTheRestOfThePayload()
    {
        FileItem item = new()
        {
            Parsed = new("/downloads/Frieren/S02E01.mkv")
            {
                Title = "Frieren",
                Year = "2026",
                Season = 2,
                Episode = 1,
            },
        };

        JObject parsed = (JObject)Serialize(item)["parsed"]!;

        parsed["title"]!.Value<string>().Should().Be("Frieren");
        parsed["year"]!.Value<string>().Should().Be("2026");
        parsed["season"]!.Value<int>().Should().Be(2);
        parsed["episode"]!.Value<int>().Should().Be(1);
        parsed.Should().NotContainKey("Title");
    }

    /// <summary>
    /// An unidentified file still ships a match object, so a client cannot treat
    /// "match is present" as "this file can be added" — the id is the only signal.
    /// </summary>
    [Fact]
    public void UnmatchedFile_StillEmitsMatchObject_WithEmptyId()
    {
        FileItem item = new() { Name = "Some Unknown File" };

        JObject json = Serialize(item);

        json["match"].Should().NotBeNull();
        json["match"]!["id"]!.Value<string>().Should().BeEmpty();
    }
}
