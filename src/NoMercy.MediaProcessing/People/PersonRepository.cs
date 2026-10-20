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

using FlexLabs.EntityFrameworkCore.Upsert;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.People;
using NoMercy.Database.Models.TvShows;

namespace NoMercy.MediaProcessing.People;

public class PersonRepository(MediaContext context, ILogger<PersonRepository> logger)
    : IPersonRepository
{
    public Task Store(IEnumerable<Person> people)
    {
        return context
            .People.UpsertRange(people.ToArray())
            .On(p => new { p.Id })
            .WhenMatched(
                (ps, pi) =>
                    new()
                    {
                        Id = pi.Id,
                        Adult = pi.Adult,
                        AlsoKnownAs = pi.AlsoKnownAs,
                        Biography = pi.Biography,
                        BirthDay = pi.BirthDay,
                        DeathDay = pi.DeathDay,
                        _externalIds = pi._externalIds,
                        TmdbGender = pi.TmdbGender,
                        Homepage = pi.Homepage,
                        ImdbId = pi.ImdbId,
                        KnownForDepartment = pi.KnownForDepartment,
                        Name = pi.Name,
                        PlaceOfBirth = pi.PlaceOfBirth,
                        Popularity = pi.Popularity,
                        Profile = pi.Profile,
                        TitleSort = pi.Name,
                    }
            )
            .RunAsync();
    }

    public Task StoreTranslationsAsync(IEnumerable<Translation> translations)
    {
        return context
            .Translations.UpsertRange(translations.ToArray())
            .On(t => new
            {
                t.Iso31661,
                t.Iso6391,
                t.PersonId,
            })
            .WhenMatched(
                (ts, ti) =>
                    new()
                    {
                        Iso31661 = ti.Iso31661,
                        Iso6391 = ti.Iso6391,
                        Name = ti.Name,
                        EnglishName = ti.EnglishName,
                        Title = ti.Title,
                        Overview = ti.Overview,
                        Homepage = ti.Homepage,
                        Biography = ti.Biography,
                        TvId = ti.TvId,
                        SeasonId = ti.SeasonId,
                        EpisodeId = ti.EpisodeId,
                        MovieId = ti.MovieId,
                        CollectionId = ti.CollectionId,
                        PersonId = ti.PersonId,
                    }
            )
            .RunAsync();
    }

    public Task StoreImagesAsync(IEnumerable<Image> images)
    {
        return context
            .Images.UpsertRange(images.ToArray())
            .On(v => new { v.FilePath, v.PersonId })
            .WhenMatched(
                (ts, ti) =>
                    new()
                    {
                        AspectRatio = ti.AspectRatio,
                        FilePath = ti.FilePath,
                        Height = ti.Height,
                        Iso6391 = ti.Iso6391,
                        Site = ti.Site,
                        VoteAverage = ti.VoteAverage,
                        VoteCount = ti.VoteCount,
                        Width = ti.Width,
                        Type = ti.Type,
                        PersonId = ti.PersonId,
                    }
            )
            .RunAsync();
    }

    public Task StoreCast(IEnumerable<Cast> cast, Type type)
    {
        Cast[] castArray = cast.ToArray();

        if (type == Type.Episode)
        {
            int[] episodeIds = context
                .Episodes.Select(e => e.Id)
                .ToArray()
                .Where(e => castArray.Any(c => c.EpisodeId == e))
                .ToArray();

            castArray = castArray
                .Where(c => c.EpisodeId is not null && episodeIds.Contains(c.EpisodeId.Value))
                .ToArray();

            if (castArray.Length == 0)
                return Task.CompletedTask;
        }

        Role[] roles = context
            .Roles.Where(role => castArray.Select(r => r.CreditId).Contains(role.CreditId))
            .ToArray();

        castArray = castArray
            .Select(c =>
            {
                c.RoleId = roles.First(r => r.CreditId == c.CreditId).Id;
                return c;
            })
            .ToArray();

        UpsertCommandBuilder<Cast> query = type switch
        {
            Type.Movie => context
                .Casts.UpsertRange(castArray)
                .On(c2 => new
                {
                    c2.CreditId,
                    c2.MovieId,
                    c2.RoleId,
                }),
            Type.TvShow => context
                .Casts.UpsertRange(castArray)
                .On(c2 => new
                {
                    c2.CreditId,
                    c2.TvId,
                    c2.RoleId,
                }),
            Type.Season => context
                .Casts.UpsertRange(castArray)
                .On(c2 => new
                {
                    c2.CreditId,
                    c2.SeasonId,
                    c2.RoleId,
                }),
            Type.Episode => context
                .Casts.UpsertRange(castArray)
                .On(c2 => new
                {
                    c2.CreditId,
                    c2.EpisodeId,
                    c2.RoleId,
                }),
            _ => throw new ArgumentOutOfRangeException(),
        };

        return query
            .WhenMatched(
                (cs, ci) =>
                    new()
                    {
                        CreditId = ci.CreditId,
                        MovieId = ci.MovieId,
                        TvId = ci.TvId,
                        SeasonId = ci.SeasonId,
                        EpisodeId = ci.EpisodeId,
                        PersonId = ci.PersonId,
                        RoleId = ci.RoleId,
                    }
            )
            .RunAsync();
    }

    public async Task StoreCrew(IEnumerable<Crew> crew, Type type)
    {
        Crew[] crewArray = crew.ToArray();

        if (type == Type.Episode)
        {
            int[] episodeIds = context
                .Episodes.Select(e => e.Id)
                .ToArray()
                .Where(e => crewArray.Any(c => c.EpisodeId == e))
                .ToArray();

            crewArray = crewArray
                .Where(c => c.EpisodeId is not null && episodeIds.Contains(c.EpisodeId.Value))
                .ToArray();

            if (crewArray.Length == 0)
                return;
        }

        Job[] jobs = context
            .Jobs.Where(job => crewArray.Select(c => c.CreditId).Contains(job.CreditId))
            .ToArray();

        // A credit without a credit_id has no key: its job sits under the empty
        // string, and a crew row keyed on null never meets the upsert's conflict
        // columns, so each refresh would insert it again. It is dropped and the
        // rest of the title's crew is still stored.
        List<Crew> keyedCrew = [];
        foreach (Crew member in crewArray)
        {
            Job? job = jobs.FirstOrDefault(j => j.CreditId == member.CreditId);
            if (job is null)
                continue;

            member.JobId = job.Id;
            keyedCrew.Add(member);
        }

        if (keyedCrew.Count < crewArray.Length)
            logger.LogWarning(
                "{Count} crew credits had no stored job and were skipped",
                crewArray.Length - keyedCrew.Count
            );

        crewArray = keyedCrew.ToArray();
        if (crewArray.Length == 0)
            return;

        try
        {
            UpsertCommandBuilder<Crew> query = type switch
            {
                Type.Movie => context
                    .Crews.UpsertRange(crewArray)
                    .On(c2 => new
                    {
                        c2.CreditId,
                        c2.MovieId,
                        c2.JobId,
                    }),
                Type.TvShow => context
                    .Crews.UpsertRange(crewArray)
                    .On(c2 => new
                    {
                        c2.CreditId,
                        c2.TvId,
                        c2.JobId,
                    }),
                Type.Season => context
                    .Crews.UpsertRange(crewArray)
                    .On(c2 => new
                    {
                        c2.CreditId,
                        c2.SeasonId,
                        c2.JobId,
                    }),
                Type.Episode => context
                    .Crews.UpsertRange(crewArray)
                    .On(c2 => new
                    {
                        c2.CreditId,
                        c2.EpisodeId,
                        c2.JobId,
                    }),
                _ => throw new ArgumentOutOfRangeException(),
            };

            await query
                .WhenMatched(
                    (cs, ci) =>
                        new()
                        {
                            CreditId = ci.CreditId,
                            MovieId = ci.MovieId,
                            TvId = ci.TvId,
                            SeasonId = ci.SeasonId,
                            EpisodeId = ci.EpisodeId,
                            PersonId = ci.PersonId,
                            JobId = ci.JobId,
                        }
                )
                .RunAsync();
        }
        catch (Exception e)
        {
            logger.LogError(e.Message);
        }
    }

    public Task StoreCreatorAsync(Creator creator)
    {
        return context
            .Creators.Upsert(creator)
            .On(c => new { c.TvId, c.PersonId })
            .WhenMatched((cs, ci) => new() { TvId = ci.TvId, PersonId = ci.PersonId })
            .RunAsync();
    }

    public Task StoreGuestStarsAsync(IEnumerable<GuestStar> guestStars)
    {
        return context
            .GuestStars.UpsertRange(guestStars.ToArray())
            .On(c => new { c.CreditId, c.EpisodeId })
            .WhenMatched(
                (cs, ci) =>
                    new()
                    {
                        Id = ci.Id,
                        CreditId = ci.CreditId,
                        PersonId = ci.PersonId,
                        EpisodeId = ci.EpisodeId,
                    }
            )
            .RunAsync();
    }

    public Task StoreRoles(IEnumerable<Role> roles)
    {
        return context
            .Roles.UpsertRange(roles.ToArray())
            .On(p => new { p.CreditId })
            .WhenMatched(
                (rs, ri) =>
                    new()
                    {
                        EpisodeCount = ri.EpisodeCount,
                        Character = ri.Character,
                        Order = ri.Order,
                        CreditId = ri.CreditId,
                    }
            )
            .RunAsync();
    }

    public Task StoreJobs(IEnumerable<Job> jobs)
    {
        return context
            .Jobs.UpsertRange(jobs.ToArray())
            .On(p => new { p.CreditId })
            .WhenMatched(
                (js, ji) =>
                    new()
                    {
                        Task = ji.Task,
                        CreditId = ji.CreditId,
                        EpisodeCount = ji.EpisodeCount,
                        Order = ji.Order,
                    }
            )
            .RunAsync();
    }

    public async Task StoreAggregateCreditsAsync(
        IEnumerable<Cast> cast,
        IEnumerable<Crew> crew,
        Type type
    )
    {
        await StoreAggregateCastAsync(cast, type);
        await StoreAggregateCrewAsync(crew, type);
    }

    public Task StoreAggregateCastAsync(IEnumerable<Cast> cast, Type type)
    {
        return StoreCast(cast, type);
    }

    public Task StoreAggregateCrewAsync(IEnumerable<Crew> crew, Type type)
    {
        return StoreCrew(crew, type);
    }

    public async Task RemoveAggregateCreditsAsync(int tvId)
    {
        // Remove only this show's cast/crew association rows; shared Person
        // records are preserved for other titles that reference them.
        await context.Casts.Where(cast => cast.TvId == tvId).ExecuteDeleteAsync();
        await context.Crews.Where(crew => crew.TvId == tvId).ExecuteDeleteAsync();
    }

    public List<int> GetIds()
    {
        return context.People.Select(p => p.Id).ToList();
    }
}
