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

        if (request.DerivedAudioCapGb is < 1)
        {
            return BadRequestResponse("derived_audio_cap_gb must be at least 1");
        }

        bool restartRequired = await UpdatePortsAsync(request, userId, changes);
        await UpdateWorkerCountsAsync(request, userId, changes);
        await UpdateServerOptionsAsync(request, userId, changes);
        await LogChangesAsync(userId, changes);

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

    private async Task PersistAsync(
        string key,
        string storedValue,
        object? oldValue,
        object? newValue,
        Guid userId,
        List<(string key, object? oldVal, object? newVal)> changes
    )
    {
        await serverConfiguration.SetValueAsync(key, storedValue, userId);
        changes.Add((key, oldValue, newValue));
    }

    /// <returns>True when a port changed, which only takes effect after a restart.</returns>
    private async Task<bool> UpdatePortsAsync(
        ConfigDtoData request,
        Guid userId,
        List<(string key, object? oldVal, object? newVal)> changes
    )
    {
        bool restartRequired = false;

        if (request.InternalServerPort != 0)
        {
            int oldPort = runtimeSettings.InternalServerPort;
            restartRequired = oldPort != request.InternalServerPort;
            runtimeSettings.InternalServerPort = request.InternalServerPort;
            await PersistAsync(
                "internalPort",
                request.InternalServerPort.ToString(),
                oldPort,
                request.InternalServerPort,
                userId,
                changes
            );
        }

        if (request.ExternalServerPort != 0)
        {
            int oldPort = runtimeSettings.ExternalServerPort;
            restartRequired = restartRequired || oldPort != request.ExternalServerPort;
            runtimeSettings.ExternalServerPort = request.ExternalServerPort;
            await PersistAsync(
                "externalPort",
                request.ExternalServerPort.ToString(),
                oldPort,
                request.ExternalServerPort,
                userId,
                changes
            );
        }

        return restartRequired;
    }

    private async Task UpdateWorkerCountsAsync(
        ConfigDtoData request,
        Guid userId,
        List<(string key, object? oldVal, object? newVal)> changes
    )
    {
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
    }

    private async Task UpdateServerOptionsAsync(
        ConfigDtoData request,
        Guid userId,
        List<(string key, object? oldVal, object? newVal)> changes
    )
    {
        if (request.Swagger is bool swagger)
        {
            bool oldSwagger = runtimeSettings.Swagger;
            runtimeSettings.Swagger = swagger;
            await PersistAsync("swagger", swagger.ToString(), oldSwagger, swagger, userId, changes);
        }

        if (request.UseSynthesizedDns is bool useSynthesizedDns)
        {
            bool oldUseSynthesizedDns = runtimeSettings.UseSynthesizedDns;
            runtimeSettings.UseSynthesizedDns = useSynthesizedDns;
            await PersistAsync(
                "UseSynthesizedDns",
                useSynthesizedDns.ToString(),
                oldUseSynthesizedDns,
                useSynthesizedDns,
                userId,
                changes
            );
        }

        if (request.AllowAdultContent is bool allowAdultContent)
        {
            bool oldAllowAdult = runtimeSettings.ShowAdultContent;
            runtimeSettings.AllowAdultContent = allowAdultContent;
            await PersistAsync(
                "allowAdultContent",
                runtimeSettings.ShowAdultContent.ToString(),
                oldAllowAdult,
                allowAdultContent,
                userId,
                changes
            );
        }

        if (request.DerivedAudioCapGb is int newCapGb)
        {
            long oldCapBytes = runtimeSettings.DerivedAudioCapBytes;
            long newCapBytes = newCapGb * 1024L * 1024 * 1024;
            runtimeSettings.DerivedAudioCapBytes = newCapBytes;
            await PersistAsync(
                "derivedAudioCapGb",
                newCapGb.ToString(),
                oldCapBytes,
                newCapBytes,
                userId,
                changes
            );
        }

        if (request.ServerName is not null)
        {
            string oldName = await serverConfiguration.GetServerNameAsync();
            await PersistAsync(
                "serverName",
                request.ServerName,
                oldName,
                request.ServerName,
                userId,
                changes
            );
        }
    }

    private async Task LogChangesAsync(
        Guid userId,
        List<(string key, object? oldVal, object? newVal)> changes
    )
    {
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
