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

using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// The version=lolomo answer for a person: a persistent hero (the person, never a focused
/// card) followed by one NMLolomo of credit rows, in the order the native TV app shows them.
/// </summary>
public static class PersonPage
{
    public static IEnumerable<ComponentEnvelope> Build(PersonResponseItemDto person)
    {
        KnownForDto? heroTitle = person
            .KnownFor.Where(credit => credit.Backdrop is not null)
            .OrderByDescending(credit => credit.VoteCount)
            .FirstOrDefault();

        HomeCardData hero = new()
        {
            Id = person.Id,
            Title = person.Name,
            Overview = person.Biography,
            Link = person.Link,
            Backdrop = heroTitle?.Backdrop,
            Poster = person.Profile,
            ColorPalette = heroTitle?.ColorPalette,
            MediaType = "person",
        };

        // Only titles the user owns are listed, as on the native Person screen.
        KnownForDto[] cast = [.. person.CombinedCredits.Cast.Where(credit => credit.HasItem == true)];
        KnownForDto[] crew = [.. person.CombinedCredits.Crew.Where(credit => credit.HasItem == true)];

        List<ComponentEnvelope> rows =
        [
            Row("person-known-for", "Known for".Localize(), person.KnownFor),
            Row("person-acting", "Acting".Localize(), cast),
            Row("person-production", "Production".Localize(), crew.Where(c => HasJob(c, "Produc"))),
            Row("person-writing", "Writing".Localize(), crew.Where(c => HasJob(c, "Writ"))),
            Row("person-directing", "Directing".Localize(), crew.Where(c => HasJob(c, "Direct"))),
            Row(
                "person-crew",
                "Crew".Localize(),
                crew.Where(c => !HasJob(c, "Produc", "Writ", "Direct", "Creator"))
            ),
            Row("person-creator", "Creator".Localize(), crew.Where(c => HasJob(c, "Creator"))),
        ];

        return
        [
            Component.HomeCard(hero).Build(),
            Component.Lolomo($"person-{person.Id}").WithHero(LolomoProps.HeroNone).WithItems(rows),
        ];
    }

    private static bool HasJob(KnownForDto credit, params string[] parts) =>
        parts.Any(part => credit.Job?.Contains(part, StringComparison.OrdinalIgnoreCase) == true);

    private static ComponentEnvelope Row(string id, string title, IEnumerable<KnownForDto> credits) =>
        Component
            .Carousel()
            .WithId(id)
            .WithTitle(title)
            .WithItems(
                credits
                    .DistinctBy(credit => credit.Id)
                    .Select(credit => Component.Card().WithData(ToCard(credit)))
            )
            .Build();

    private static CardData ToCard(KnownForDto credit) =>
        new()
        {
            Id = credit.Id.ToString().OrEmpty(),
            Title = credit.Title ?? credit.Name,
            TitleSort = credit.Title ?? credit.Name,
            Overview = credit.Overview,
            Link = credit.Link,
            Year = (int?)credit.Year,
            Type = credit.MediaType ?? "movie",
            Backdrop = credit.Backdrop,
            Poster = credit.Poster,
            ColorPalette = credit.ColorPalette,
            HaveItems = credit.HaveItems,
            NumberOfItems = credit.NumberOfItems,
            Subtitle = credit.Character ?? credit.Job,
        };
}
