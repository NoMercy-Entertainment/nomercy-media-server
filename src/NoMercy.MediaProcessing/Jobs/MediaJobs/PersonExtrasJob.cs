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

// ---------------------------------------------------------------------------------------------------------------------
// Imports
// ---------------------------------------------------------------------------------------------------------------------

using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.MediaProcessing.People;
using NoMercy.Providers.TMDB.Models.People;

namespace NoMercy.MediaProcessing.Jobs.MediaJobs;

// ---------------------------------------------------------------------------------------------------------------------
// Code
// ---------------------------------------------------------------------------------------------------------------------
[Serializable]
public class PersonExtrasJob : AbstractShowExtraDataJob<TmdbPersonAppends, string>
{
    public PersonExtrasJob() { }

    public PersonExtrasJob(ILoggerFactory loggerFactory)
        : base(loggerFactory) { }

    public override string QueueName => "extras";
    public override int Priority => 1;

    /** Note: TmdbPersonAppends is a reduced set to improve performance. */
    public override async Task Handle()
    {
        await using MediaContext context = new();
        JobDispatcher jobDispatcher = new();

        PersonRepository personRepository = new(
            context,
            LoggerFactory.CreateLogger<PersonRepository>()
        );
        PersonManager personManager = new(
            personRepository,
            jobDispatcher,
            LoggerFactory.CreateLogger<PersonManager>()
        );

        foreach (TmdbPersonAppends person in Storage)
        {
            await personManager
                .StoreTranslations(person)
                .WithTimeout(nameof(PersonManager.StoreTranslations));
            await personManager.StoreImages(person).WithTimeout(nameof(PersonManager.StoreImages));
        }

        Log.LogDebug("Show {Name}: People: Translations and Images stored", Name);
    }
}
