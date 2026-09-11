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
    AppDbContext appContext,
    QueueRunner queueRunner,
    IActivityLogger activityLogger,
    ILanguageRepository languageRepository,
    RuntimeServerSettings runtimeSettings,
    ILogger<ConfigurationController> logger
) : BaseController
{
    [HttpGet]
    [Authorize(Policy = "Moderator")]
    public IActionResult Index()
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
                    ServerName = DeviceName(),
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

    [NonAction]
    private string DeviceName()
    {
        Configuration? device = appContext.Configuration.FirstOrDefault(device =>
            device.Key == "serverName"
        );
        return device?.Value ?? Environment.MachineName;
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
    private async Task PersistWorkerCount(string queueName, int count, Guid userId)
    {
        string key = $"{queueName}Runners";
        await appContext
            .Configuration.Upsert(
                new()
                {
                    Key = key,
                    Value = count.ToString(),
                    ModifiedBy = userId,
                }
            )
            .On(configuration => configuration.Key)
            .WhenMatched(
                (_, configuration) =>
                    new() { Value = configuration.Value, ModifiedBy = configuration.ModifiedBy }
            )
            .RunAsync();

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
            await appContext
                .Configuration.Upsert(
                    new()
                    {
                        Key = "internalPort",
                        Value = request.InternalServerPort.ToString(),
                        ModifiedBy = userId,
                    }
                )
                .On(configuration => configuration.Key)
                .WhenMatched(
                    (o, configuration) =>
                        new() { Value = configuration.Value, ModifiedBy = configuration.ModifiedBy }
                )
                .RunAsync();
            changes.Add(("internalPort", oldPort, request.InternalServerPort));
        }

        if (request.ExternalServerPort != 0)
        {
            int oldPort = runtimeSettings.ExternalServerPort;
            restartRequired = restartRequired || oldPort != request.ExternalServerPort;
            runtimeSettings.ExternalServerPort = request.ExternalServerPort;
            await appContext
                .Configuration.Upsert(
                    new()
                    {
                        Key = "externalPort",
                        Value = request.ExternalServerPort.ToString(),
                        ModifiedBy = userId,
                    }
                )
                .On(configuration => configuration.Key)
                .WhenMatched(
                    (o, configuration) =>
                        new() { Value = configuration.Value, ModifiedBy = configuration.ModifiedBy }
                )
                .RunAsync();
            changes.Add(("externalPort", oldPort, request.ExternalServerPort));
        }

        if (request.LibraryWorkers is not null)
        {
            int oldCount = runtimeSettings.LibraryWorkers.Value;
            int newCount = (int)request.LibraryWorkers;
            runtimeSettings.LibraryWorkers = new(runtimeSettings.LibraryWorkers.Key, newCount);
            await PersistWorkerCount(runtimeSettings.LibraryWorkers.Key, newCount, userId);
            changes.Add((runtimeSettings.LibraryWorkers.Key, oldCount, newCount));
        }

        if (request.ImportWorkers is not null)
        {
            int oldCount = runtimeSettings.ImportWorkers.Value;
            int newCount = (int)request.ImportWorkers;
            runtimeSettings.ImportWorkers = new(runtimeSettings.ImportWorkers.Key, newCount);
            await PersistWorkerCount(runtimeSettings.ImportWorkers.Key, newCount, userId);
            changes.Add((runtimeSettings.ImportWorkers.Key, oldCount, newCount));
        }

        if (request.ExtrasWorkers is not null)
        {
            int oldCount = runtimeSettings.ExtrasWorkers.Value;
            int newCount = (int)request.ExtrasWorkers;
            runtimeSettings.ExtrasWorkers = new(runtimeSettings.ExtrasWorkers.Key, newCount);
            await PersistWorkerCount(runtimeSettings.ExtrasWorkers.Key, newCount, userId);
            changes.Add((runtimeSettings.ExtrasWorkers.Key, oldCount, newCount));
        }

        if (request.EncoderWorkers is not null)
        {
            int oldCount = runtimeSettings.EncoderWorkers.Value;
            int newCount = (int)request.EncoderWorkers;
            runtimeSettings.EncoderWorkers = new(runtimeSettings.EncoderWorkers.Key, newCount);
            await PersistWorkerCount(runtimeSettings.EncoderWorkers.Key, newCount, userId);
            changes.Add((runtimeSettings.EncoderWorkers.Key, oldCount, newCount));
        }

        if (request.CronWorkers is not null)
        {
            int oldCount = runtimeSettings.CronWorkers.Value;
            int newCount = (int)request.CronWorkers;
            runtimeSettings.CronWorkers = new(runtimeSettings.CronWorkers.Key, newCount);
            await PersistWorkerCount(runtimeSettings.CronWorkers.Key, newCount, userId);
            changes.Add((runtimeSettings.CronWorkers.Key, oldCount, newCount));
        }

        if (request.ImageWorkers is not null)
        {
            int oldCount = runtimeSettings.ImageWorkers.Value;
            int newCount = (int)request.ImageWorkers;
            runtimeSettings.ImageWorkers = new(runtimeSettings.ImageWorkers.Key, newCount);
            await PersistWorkerCount(runtimeSettings.ImageWorkers.Key, newCount, userId);
            changes.Add((runtimeSettings.ImageWorkers.Key, oldCount, newCount));
        }

        if (request.FileWorkers is not null)
        {
            int oldCount = runtimeSettings.FileWorkers.Value;
            int newCount = (int)request.FileWorkers;
            runtimeSettings.FileWorkers = new(runtimeSettings.FileWorkers.Key, newCount);
            await PersistWorkerCount(runtimeSettings.FileWorkers.Key, newCount, userId);
            changes.Add((runtimeSettings.FileWorkers.Key, oldCount, newCount));
        }

        if (request.MusicWorkers is not null)
        {
            int oldCount = runtimeSettings.MusicWorkers.Value;
            int newCount = (int)request.MusicWorkers;
            runtimeSettings.MusicWorkers = new(runtimeSettings.MusicWorkers.Key, newCount);
            await PersistWorkerCount(runtimeSettings.MusicWorkers.Key, newCount, userId);
            changes.Add((runtimeSettings.MusicWorkers.Key, oldCount, newCount));
        }

        if (request.Swagger is not null)
        {
            bool oldSwagger = runtimeSettings.Swagger;
            runtimeSettings.Swagger = (bool)request.Swagger;
            await appContext
                .Configuration.Upsert(
                    new()
                    {
                        Key = "swagger",
                        Value = runtimeSettings.Swagger.ToString(),
                        ModifiedBy = User.UserId(),
                    }
                )
                .On(configuration => configuration.Key)
                .WhenMatched(
                    (o, configuration) =>
                        new()
                        {
                            Value = runtimeSettings.Swagger.ToString(),
                            ModifiedBy = configuration.ModifiedBy,
                        }
                )
                .RunAsync();
            changes.Add(("swagger", oldSwagger, (bool)request.Swagger));
        }

        if (request.UseSynthesizedDns is not null)
        {
            bool oldUseSynthesizedDns = runtimeSettings.UseSynthesizedDns;
            runtimeSettings.UseSynthesizedDns = (bool)request.UseSynthesizedDns;
            await appContext
                .Configuration.Upsert(
                    new()
                    {
                        Key = "UseSynthesizedDns",
                        Value = runtimeSettings.UseSynthesizedDns.ToString(),
                        ModifiedBy = userId,
                    }
                )
                .On(configuration => configuration.Key)
                .WhenMatched(
                    (o, configuration) =>
                        new()
                        {
                            Value = runtimeSettings.UseSynthesizedDns.ToString(),
                            ModifiedBy = configuration.ModifiedBy,
                        }
                )
                .RunAsync();
            changes.Add(
                ("UseSynthesizedDns", oldUseSynthesizedDns, (bool)request.UseSynthesizedDns)
            );
        }

        if (request.AllowAdultContent is not null)
        {
            bool oldAllowAdult = runtimeSettings.ShowAdultContent;
            runtimeSettings.AllowAdultContent = request.AllowAdultContent;
            await appContext
                .Configuration.Upsert(
                    new()
                    {
                        Key = "allowAdultContent",
                        Value = runtimeSettings.ShowAdultContent.ToString(),
                        ModifiedBy = userId,
                    }
                )
                .On(configuration => configuration.Key)
                .WhenMatched(
                    (o, configuration) =>
                        new() { Value = configuration.Value, ModifiedBy = configuration.ModifiedBy }
                )
                .RunAsync();
            changes.Add(("allowAdultContent", oldAllowAdult, (bool)request.AllowAdultContent));
        }

        if (request.DerivedAudioCapGb is not null)
        {
            int newCapGb = (int)request.DerivedAudioCapGb;
            long oldCapBytes = runtimeSettings.DerivedAudioCapBytes;
            long newCapBytes = newCapGb * 1024L * 1024 * 1024;
            runtimeSettings.DerivedAudioCapBytes = newCapBytes;
            await appContext
                .Configuration.Upsert(
                    new()
                    {
                        Key = "derivedAudioCapGb",
                        Value = newCapGb.ToString(),
                        ModifiedBy = userId,
                    }
                )
                .On(configuration => configuration.Key)
                .WhenMatched(
                    (o, configuration) =>
                        new() { Value = configuration.Value, ModifiedBy = configuration.ModifiedBy }
                )
                .RunAsync();
            changes.Add(("derivedAudioCapGb", oldCapBytes, newCapBytes));
        }

        if (request.ServerName is not null)
        {
            string oldName = DeviceName();
            await appContext
                .Configuration.Upsert(
                    new()
                    {
                        Key = "serverName",
                        Value = request.ServerName,
                        ModifiedBy = User.UserId(),
                    }
                )
                .On(configuration => configuration.Key)
                .WhenMatched(
                    (o, configuration) =>
                        new() { Value = request.ServerName, ModifiedBy = configuration.ModifiedBy }
                )
                .RunAsync();
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
