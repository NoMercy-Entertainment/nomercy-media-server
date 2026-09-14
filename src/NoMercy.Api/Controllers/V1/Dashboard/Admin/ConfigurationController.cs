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

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Api.Controllers.V1.Music;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Activity;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Libraries;
using NoMercy.NmSystem.Configuration;
using NoMercyQueue;
using Configuration = NoMercy.Database.Models.Common.Configuration;

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

[ApiController]
[Tags("Dashboard Configuration")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/dashboard/configuration", Order = 10)]
public class ConfigurationController(
    IServerConfigurationRepository serverConfiguration,
    QueueRunner queueRunner,
    IActivityLogger activityLogger,
    ILanguageRepository languageRepository,
    RuntimeServerSettings runtimeSettings,
    ILogger<ConfigurationController> logger
) : BaseController
{
    [HttpGet]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Index()
    {
        return Ok(
            new ConfigDto
            {
                Data = new()
                {
                    InternalServerPort = runtimeSettings.InternalServerPort,
                    ExternalServerPort = runtimeSettings.ExternalServerPort,
                    LibraryWorkers = runtimeSettings.LibraryWorkers.Value,
                    ImportWorkers = runtimeSettings.ImportWorkers.Value,
                    ExtrasWorkers = runtimeSettings.ExtrasWorkers.Value,
                    EncoderWorkers = runtimeSettings.EncoderWorkers.Value,
                    CronWorkers = runtimeSettings.CronWorkers.Value,
                    ImageWorkers = runtimeSettings.ImageWorkers.Value,
                    FileWorkers = runtimeSettings.FileWorkers.Value,
                    MusicWorkers = runtimeSettings.MusicWorkers.Value,
                    ServerName = await serverConfiguration.GetServerNameAsync(),
                    Swagger = runtimeSettings.Swagger,
                    AllowAdultContent = runtimeSettings.ShowAdultContent,
                    UseSynthesizedDns = runtimeSettings.UseSynthesizedDns,
                    DerivedAudioCapGb = (int)(
                        runtimeSettings.DerivedAudioCapBytes / (1024L * 1024 * 1024)
                    ),
                },
            }
        );
    }

    /// <summary>
    /// Belt-and-suspenders persist for worker counts. Writes the value to the
    /// Configuration table directly AND tells the QueueRunner to resize live
    /// workers. The previous flow only called QueueRunner.SetWorkerCount,
    /// which short-circuits with a no-op when the new count equals the
    /// current count — leaving the DB stale and the value resetting to the
    /// default on next boot. Two writes both have to land or the persistence
    /// silently rots.
    /// </summary>
    [NonAction]
    /// <summary>
    /// Applies a requested worker count to a queue: persisted, handed to the running
    /// queue and recorded as a change. The setting is returned unchanged when none was requested.
    /// </summary>
    private async Task<KeyValuePair<string, int>> UpdateWorkerCountAsync(
        KeyValuePair<string, int> current,
        int? requested,
        Guid userId,
        List<(string key, object? oldVal, object? newVal)> changes
    )
    {
        if (requested is not { } newCount)
            return current;

        await PersistWorkerCount(current.Key, newCount, userId);
        changes.Add((current.Key, current.Value, newCount));
        return new(current.Key, newCount);
    }

    private async Task PersistWorkerCount(string queueName, int count, Guid userId)
    {
        string key = $"{queueName}Runners";
        await serverConfiguration.SetValueAsync(key, count.ToString(), userId);

        await queueRunner.SetWorkerCount(queueName, count, userId);
    }

    [HttpPost]
    [Authorize(Policy = "Moderator")]
    public IActionResult Store()
    {
        return NotImplementedResponse(
            "Creating configuration is not supported. Use PATCH to change existing keys."
        );
    }

    [HttpPatch]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Update([FromBody] ConfigDtoData request)
    {
        Guid userId = User.UserId();
        List<(string key, object? oldVal, object? newVal)> changes = [];
        bool restartRequired = false;

        if (request.DerivedAudioCapGb is < 1)
        {
            return BadRequestResponse("derived_audio_cap_gb must be at least 1");
        }

        if (request.InternalServerPort != 0)
        {
            int oldPort = runtimeSettings.InternalServerPort;
            restartRequired = restartRequired || oldPort != request.InternalServerPort;
            runtimeSettings.InternalServerPort = request.InternalServerPort;
            await serverConfiguration.SetValueAsync(
                "internalPort",
                request.InternalServerPort.ToString(),
                userId
            );
            changes.Add(("internalPort", oldPort, request.InternalServerPort));
        }

        if (request.ExternalServerPort != 0)
        {
            int oldPort = runtimeSettings.ExternalServerPort;
            restartRequired = restartRequired || oldPort != request.ExternalServerPort;
            runtimeSettings.ExternalServerPort = request.ExternalServerPort;
            await serverConfiguration.SetValueAsync(
                "externalPort",
                request.ExternalServerPort.ToString(),
                userId
            );
            changes.Add(("externalPort", oldPort, request.ExternalServerPort));
        }

        runtimeSettings.LibraryWorkers = await UpdateWorkerCountAsync(
            runtimeSettings.LibraryWorkers,
            request.LibraryWorkers,
            userId,
            changes
        );
        runtimeSettings.ImportWorkers = await UpdateWorkerCountAsync(
            runtimeSettings.ImportWorkers,
            request.ImportWorkers,
            userId,
            changes
        );
        runtimeSettings.ExtrasWorkers = await UpdateWorkerCountAsync(
            runtimeSettings.ExtrasWorkers,
            request.ExtrasWorkers,
            userId,
            changes
        );
        runtimeSettings.EncoderWorkers = await UpdateWorkerCountAsync(
            runtimeSettings.EncoderWorkers,
            request.EncoderWorkers,
            userId,
            changes
        );
        runtimeSettings.CronWorkers = await UpdateWorkerCountAsync(
            runtimeSettings.CronWorkers,
            request.CronWorkers,
            userId,
            changes
        );
        runtimeSettings.ImageWorkers = await UpdateWorkerCountAsync(
            runtimeSettings.ImageWorkers,
            request.ImageWorkers,
            userId,
            changes
        );
        runtimeSettings.FileWorkers = await UpdateWorkerCountAsync(
            runtimeSettings.FileWorkers,
            request.FileWorkers,
            userId,
            changes
        );
        runtimeSettings.MusicWorkers = await UpdateWorkerCountAsync(
            runtimeSettings.MusicWorkers,
            request.MusicWorkers,
            userId,
            changes
        );

        if (request.Swagger is not null)
        {
            bool oldSwagger = runtimeSettings.Swagger;
            runtimeSettings.Swagger = (bool)request.Swagger;
            await serverConfiguration.SetValueAsync(
                "swagger",
                runtimeSettings.Swagger.ToString(),
                User.UserId()
            );
            changes.Add(("swagger", oldSwagger, (bool)request.Swagger));
        }

        if (request.UseSynthesizedDns is not null)
        {
            bool oldUseSynthesizedDns = runtimeSettings.UseSynthesizedDns;
            runtimeSettings.UseSynthesizedDns = (bool)request.UseSynthesizedDns;
            await serverConfiguration.SetValueAsync(
                "UseSynthesizedDns",
                runtimeSettings.UseSynthesizedDns.ToString(),
                userId
            );
            changes.Add(
                ("UseSynthesizedDns", oldUseSynthesizedDns, (bool)request.UseSynthesizedDns)
            );
        }

        if (request.AllowAdultContent is not null)
        {
            bool oldAllowAdult = runtimeSettings.ShowAdultContent;
            runtimeSettings.AllowAdultContent = request.AllowAdultContent;
            await serverConfiguration.SetValueAsync(
                "allowAdultContent",
                runtimeSettings.ShowAdultContent.ToString(),
                userId
            );
            changes.Add(("allowAdultContent", oldAllowAdult, (bool)request.AllowAdultContent));
        }

        if (request.DerivedAudioCapGb is not null)
        {
            int newCapGb = (int)request.DerivedAudioCapGb;
            long oldCapBytes = runtimeSettings.DerivedAudioCapBytes;
            long newCapBytes = newCapGb * 1024L * 1024 * 1024;
            runtimeSettings.DerivedAudioCapBytes = newCapBytes;
            await serverConfiguration.SetValueAsync(
                "derivedAudioCapGb",
                newCapGb.ToString(),
                userId
            );
            changes.Add(("derivedAudioCapGb", oldCapBytes, newCapBytes));
        }

        if (request.ServerName is not null)
        {
            string oldName = await serverConfiguration.GetServerNameAsync();
            await serverConfiguration.SetValueAsync(
                "serverName",
                request.ServerName,
                User.UserId()
            );
            changes.Add(("serverName", oldName, request.ServerName));
        }

        foreach ((string key, object? oldVal, object? newVal) in changes)
        {
            try
            {
                await activityLogger.LogConfigurationAsync(
                    "config.server_changed",
                    userId,
                    deviceId: Ulid.Empty,
                    configKey: key,
                    oldValue: oldVal,
                    newValue: newVal
                );
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to log config change: {Message}", ex.Message);
            }
        }

        return Ok(
            new StatusResponseDto<string>
            {
                Message = restartRequired
                    ? "Configuration updated successfully. Restart required for the port change to take effect."
                    : "Configuration updated successfully",
                Status = "success",
                Args = [],
            }
        );
    }

    [HttpGet]
    [Route("languages")]
    [ResponseCache(Duration = 3600)]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> Languages()
    {
        List<Language> languages = await languageRepository.GetLanguagesAsync();

        return Ok(
            languages
                .Select(language => new LanguageDto
                {
                    Id = language.Id,
                    Iso6391 = language.Iso6391,
                    EnglishName = language.EnglishName,
                    Name = language.Name,
                })
                .ToList()
        );
    }

    [HttpGet]
    [Route("countries")]
    [ResponseCache(Duration = 3600)]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> Countries()
    {
        List<Country> countries = await languageRepository.GetCountriesAsync();

        return Ok(
            countries
                .Select(country => new CountryDto
                {
                    Name = country.EnglishName,
                    Code = country.Iso31661,
                })
                .ToList()
        );
    }
}
