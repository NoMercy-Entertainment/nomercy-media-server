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

using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.Providers.AcoustId.Client;
using NoMercy.Providers.AcoustId.Models;

namespace NoMercy.Tests.Providers.AcoustId.Models;

/// <summary>
/// The music filelist identifies an album by fingerprinting its tracks and following
/// <c>recordings[].releases[].id</c> into MusicBrainz. Both halves of that hop broke:
/// AcoustID reports <c>duration</c> as a float, and the global Newtonsoft error handler
/// swallows the resulting bind failure, so the field vanished without a stack trace.
/// <para>
/// The payload below is a trimmed capture of a live AcoustID response taken on
/// 2026-08-02 (<c>meta=recordings+releases</c>), values unedited.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class AcoustIdFingerprintDeserializationTests
{
    private const string LiveResponse = """
        {
          "status": "ok",
          "results": [
            {
              "id": "32f60013-38f7-45fb-a6e7-d2d61f481357",
              "score": 0.9831553,
              "recordings": [
                {
                  "artists": [
                    { "id": "66c662b6-6e2f-4930-8610-912e24c63ed1", "name": "AC/DC" }
                  ],
                  "duration": 205.0,
                  "id": "b4fdd569-b85c-4ef5-b2cb-3007f3303f64",
                  "releases": [
                    {
                      "artists": [
                        { "id": "66c662b6-6e2f-4930-8610-912e24c63ed1", "name": "AC/DC" }
                      ],
                      "country": "US",
                      "date": { "year": 1983 },
                      "id": "7047d8d5-e91c-4d48-90cc-eba5d6dc96ea",
                      "medium_count": 1,
                      "releaseevents": [
                        { "country": "US", "date": { "year": 1983 } }
                      ],
                      "title": "Flick of the Switch / Guns for Hire",
                      "track_count": 2
                    }
                  ],
                  "title": "Guns for Hire"
                }
              ]
            }
          ]
        }
        """;

    private const string FractionalDurationResponse = """
        {
          "status": "ok",
          "results": [
            {
              "id": "32f60013-38f7-45fb-a6e7-d2d61f481357",
              "score": 0.9831553,
              "recordings": [
                {
                  "duration": 205.291,
                  "id": "b4fdd569-b85c-4ef5-b2cb-3007f3303f64",
                  "releases": [
                    { "id": "7047d8d5-e91c-4d48-90cc-eba5d6dc96ea", "title": "Flick of the Switch" }
                  ],
                  "title": "Guns for Hire"
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Float_duration_still_yields_the_release_ids_the_lookup_needs()
    {
        AcoustIdFingerprint? fingerprint = LiveResponse.FromJson<AcoustIdFingerprint>();

        AcoustIdFingerprintRecording recording = fingerprint!
            .Results.Single()
            .Recordings!.Single()!;

        recording
            .Releases!.Select(release => release.Id)
            .Should()
            .Equal(Guid.Parse("7047d8d5-e91c-4d48-90cc-eba5d6dc96ea"));
    }

    [Fact]
    public void Whole_second_duration_binds_instead_of_being_dropped()
    {
        AcoustIdFingerprint? fingerprint = LiveResponse.FromJson<AcoustIdFingerprint>();

        fingerprint!.Results.Single().Recordings!.Single()!.Duration.Should().Be(205.0);
    }

    /// <summary>
    /// The same live lookup requested with <c>meta=recordings+releases+releasegroups</c>.
    /// Captured 2026-08-02, values unedited.
    /// </summary>
    private const string NestedResponse = """
        {
          "status": "ok",
          "results": [
            {
              "id": "32f60013-38f7-45fb-a6e7-d2d61f481357",
              "score": 0.9831553,
              "recordings": [
                {
                  "duration": 205.0,
                  "id": "b4fdd569-b85c-4ef5-b2cb-3007f3303f64",
                  "releasegroups": [
                    {
                      "id": "7b1217ed-1d54-420c-bcd5-5f36346c4f3b",
                      "title": "Flick of the Switch",
                      "type": "Single",
                      "releases": [
                        {
                          "id": "7047d8d5-e91c-4d48-90cc-eba5d6dc96ea",
                          "title": "Flick of the Switch / Guns for Hire",
                          "track_count": 2
                        }
                      ]
                    }
                  ],
                  "title": "Guns for Hire"
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Lookup_does_not_ask_for_releasegroups_which_would_nest_the_releases()
    {
        AcoustIdFingerprintClient.LookupMeta.Should().Equal("recordings", "releases");
    }

    /// <summary>
    /// Pins the reason for the assertion above: the nested shape carries the same release
    /// id, but not where anything reads it. Asking for <c>releasegroups</c> cost every
    /// music lookup its result while the request still logged as a success.
    /// </summary>
    [Fact]
    public void Releasegroups_shape_binds_no_release_ids_at_all()
    {
        AcoustIdFingerprint? fingerprint = NestedResponse.FromJson<AcoustIdFingerprint>();

        fingerprint!.Results.Single().Recordings!.Single()!.Releases.Should().BeEmpty();
    }

    [Fact]
    public void Fractional_duration_binds_instead_of_being_dropped()
    {
        AcoustIdFingerprint? fingerprint =
            FractionalDurationResponse.FromJson<AcoustIdFingerprint>();

        AcoustIdFingerprintRecording recording = fingerprint!
            .Results.Single()
            .Recordings!.Single()!;

        recording.Duration.Should().BeApproximately(205.291, 0.0005);
        recording.Releases!.Should().ContainSingle();
    }
}
