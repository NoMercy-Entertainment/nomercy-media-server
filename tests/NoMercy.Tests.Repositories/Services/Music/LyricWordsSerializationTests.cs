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
using NoMercy.Database.Models.Music;

namespace NoMercy.Tests.Repositories.Services.Music;

/// <summary>
/// Word timing lives inside the existing lyrics JSON. A row written before it
/// existed still reads, and a line without words writes no "words" key, so a
/// client that does not know the field never sees it (#620).
/// </summary>
[Trait("Category", "Unit")]
public class LyricWordsSerializationTests
{
    [Fact]
    public void A_line_without_words_serializes_without_a_words_key()
    {
        string json = JsonConvert.SerializeObject(new Lyric { Text = "line" });

        json.Should().NotContain("words");
    }

    [Fact]
    public void A_line_with_words_round_trips()
    {
        Lyric line = new()
        {
            Text = "two words",
            Time = new() { Total = 1.5 },
            Words =
            [
                new()
                {
                    Text = "two",
                    Start = 1.5,
                    End = 1.9,
                },
                new()
                {
                    Text = "words",
                    Start = 2.0,
                    End = 2.4,
                },
            ],
        };

        Lyric? back = JsonConvert.DeserializeObject<Lyric>(JsonConvert.SerializeObject(line));

        back!.Words.Should().HaveCount(2);
        back.Words![1].Text.Should().Be("words");
        back.Words[1].End.Should().Be(2.4);
    }

    [Fact]
    public void Old_json_without_words_still_reads()
    {
        const string old =
            """{"text":"line","time":{"total":1.5,"minutes":0,"seconds":1,"hundredths":50}}""";

        Lyric? back = JsonConvert.DeserializeObject<Lyric>(old);

        back!.Text.Should().Be("line");
        back.Words.Should().BeNull();
    }
}
