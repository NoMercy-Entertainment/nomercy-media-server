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
using NoMercy.Database;
using NoMercy.Database.Models.People;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.Providers.TMDB.Models.People;
using TmdbGender = NoMercy.Providers.TMDB.Models.People.TmdbGender;
using TmdbPersonExternalIds = NoMercy.Database.Models.People.TmdbPersonExternalIds;

namespace NoMercy.Api.DTOs.Media;

public record PersonResponseItemDto
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("adult")]
    public bool Adult { get; set; }

    [JsonProperty("also_known_as")]
    public string[]? AlsoKnownAs { get; set; }

    [JsonProperty("biography")]
    public string? Biography { get; set; }

    [JsonProperty("birthday")]
    public DateTime? Birthday { get; set; }

    [JsonProperty("deathday")]
    public DateTime? DeathDay { get; set; }

    [JsonProperty("gender")]
    public string Gender { get; set; } = nameof(TmdbGender.Unknown);

    [JsonProperty("homepage")]
    public string? Homepage { get; set; }

    [JsonProperty("imdb_id")]
    public string? ImdbId { get; set; }

    [JsonProperty("known_for_department")]
    public string? KnownForDepartment { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("place_of_birth")]
    public string? PlaceOfBirth { get; set; }

    [JsonProperty("popularity")]
    public double Popularity { get; set; }

    [JsonProperty("profile")]
    public string? Profile { get; set; }

    [JsonProperty("titleSort")]
    public string TitleSort { get; set; } = string.Empty;

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonProperty("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; } = null!;

    [JsonProperty("combined_credits")]
    public Credits CombinedCredits { get; set; } = new();

    [JsonProperty("external_ids")]
    public TmdbPersonExternalIds? ExternalIds { get; set; }

    [JsonProperty("translations")]
    public TranslationsDto TranslationsDto { get; set; } = new();

    [JsonProperty("known_for")]
    public KnownForDto[] KnownFor { get; set; } = [];

    [JsonProperty("images")]
    public ImagesDto ImagesDto { get; set; } = new();

    public PersonResponseItemDto(Person person)
    {
        string? biography = person.Translations.FirstOrDefault()?.Biography;

        Id = person.Id;
        Name = person.Name;
        Biography = biography.OrWhenEmpty(person.Biography);
        Adult = person.Adult;
        AlsoKnownAs = person.AlsoKnownAs.FromJson<string[]>() ?? [];
        Birthday = person.BirthDay;
        DeathDay = person.DeathDay;
        Homepage = person.Homepage;
        ImdbId = person.ImdbId;
        KnownForDepartment = person.KnownForDepartment;
        PlaceOfBirth = person.PlaceOfBirth;
        Popularity = person.Popularity;
        Profile = person.Profile;
        ColorPalette = person.ColorPalette;
        CreatedAt = person.CreatedAt;
        ExternalIds = person.ExternalIds;
        Gender = person.Gender;
        Link = new($"/person/{Id}", UriKind.Relative);

        ImagesDto = new() { Profiles = [.. person.Images.Select(image => new ImageDto(image))] };

        CombinedCredits = new()
        {
            Cast =
            [
                .. person
                    .Casts.Select(cast => new KnownForDto(cast))
                    .OrderByDescending(knownFor => knownFor.Year),
            ],

            Crew =
            [
                .. person
                    .Crews.Select(crew => new KnownForDto(crew))
                    .OrderByDescending(knownFor => knownFor.Year),
            ],
        };

        KnownFor =
        [
            .. person
                .Casts.Select(crew => new KnownForDto(crew))
                .Concat(person.Crews.Select(crew => new KnownForDto(crew)))
                .OrderByDescending(knownFor => knownFor.Popularity),
        ];
    }

    public PersonResponseItemDto(
        TmdbPersonAppends tmdbPersonAppends,
        string? country,
        Person? person
    )
    {
        string? biography = tmdbPersonAppends
            .Translations.Translations.FirstOrDefault(translation =>
                translation.Iso31661 == country
            )
            ?.TmdbPersonTranslationData.Overview;

        Id = tmdbPersonAppends.Id;
        Name = tmdbPersonAppends.Name;
        Biography = biography.OrWhenEmpty(tmdbPersonAppends.Biography);
        Adult = tmdbPersonAppends.Adult;
        AlsoKnownAs = tmdbPersonAppends.AlsoKnownAs;
        Birthday = tmdbPersonAppends.BirthDay;
        DeathDay = tmdbPersonAppends.DeathDay;
        Homepage = tmdbPersonAppends.Homepage?.ToString();
        ImdbId = tmdbPersonAppends.ImdbId;
        KnownForDepartment = tmdbPersonAppends.KnownForDepartment;
        PlaceOfBirth = tmdbPersonAppends.PlaceOfBirth;
        Popularity = tmdbPersonAppends.Popularity;
        Profile = tmdbPersonAppends.ProfilePath;
        ColorPalette = new();
        ExternalIds = tmdbPersonAppends.ExternalIds;
        Gender = Enum.Parse<TmdbGender>(tmdbPersonAppends.TmdbGender.ToString(), true).ToString();
        Link = new($"/person/{Id}", UriKind.Relative);

        ImagesDto = new()
        {
            Profiles = [.. tmdbPersonAppends.Images.Profiles.Select(image => new ImageDto(image))],
        };

        CombinedCredits = new()
        {
            Cast =
            [
                .. tmdbPersonAppends
                    .CombinedCredits.Cast.Select(cast => new KnownForDto(cast, person))
                    .Where(knownFor =>
                        RuntimeServerSettings.Current.ShowAdultContent || !knownFor.Adult
                    )
                    .OrderByDescending(knownFor => knownFor.Year),
            ],

            Crew =
            [
                .. tmdbPersonAppends
                    .CombinedCredits.Crew.Select(crew => new KnownForDto(crew, person))
                    .Where(knownFor =>
                        RuntimeServerSettings.Current.ShowAdultContent || !knownFor.Adult
                    )
                    .OrderByDescending(knownFor => knownFor.Year),
            ],
        };

        KnownForDto[] cast =
        [
            .. tmdbPersonAppends
                .CombinedCredits.Cast.Select(cast => new KnownForDto(cast, person))
                .Where(knownFor =>
                    RuntimeServerSettings.Current.ShowAdultContent || !knownFor.Adult
                )
                .DistinctBy(knownFor => knownFor.Id),
        ];

        KnownForDto[] crew =
        [
            .. tmdbPersonAppends
                .CombinedCredits.Crew.Select(crew => new KnownForDto(crew, person))
                .Where(knownFor =>
                    RuntimeServerSettings.Current.ShowAdultContent || !knownFor.Adult
                )
                .DistinctBy(knownFor => knownFor.Id),
        ];

        KnownFor = [.. cast.Concat(crew).OrderByDescending(knownFor => knownFor.VoteCount)];
    }
}
