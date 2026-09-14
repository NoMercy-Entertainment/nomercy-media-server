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

using System.Threading.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.Api.DTOs.Management;
using NoMercy.Api.Filters;
using NoMercy.Data.Repositories;
using NoMercy.Encoder.LiveTranscode;
using NoMercy.Monitoring;
using NoMercy.Networking.Connectivity;
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.Status;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Plugins.Abstractions;
using NoMercy.Queue.MediaServer.Repositories;
using NoMercy.Setup.Server;
using NoMercy.Storage;
using NoMercyQueue;
using Configuration = NoMercy.Database.Models.Common.Configuration;

namespace NoMercy.Api.Controllers;

[ApiController]
[Route("manage")]
[AllowAnonymous]
[LocalhostOnly]
[Tags("Management")]
public class ManagementController(
    ILogger<ManagementController> logger,
    ResourceMonitor resourceMonitor,
    IHostApplicationLifetime appLifetime,
    IServerConfigurationRepository serverConfiguration,
    QueueRunner queueRunner,
    IPluginManager pluginManager,
    AppProcessManager appProcessManager,
    SetupState setupState,
    INetworkDiscovery networkDiscovery,
    ISessionManager sessionManager,
    IStorageDriver storageDriver,
    IStorage storage,
    IQueueTaskRepository queueTaskRepository,
    IBootStatus bootStatus,
    IUpdateStatus updateStatus,
    IConnectivityManager connectivityManager,
    IConnectivityStatus connectivityStatus,
    RuntimeServerSettings runtimeSettings
) : BaseController
{
    [HttpGet("status")]
    [ProducesResponseType(typeof(ManagementStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus()
    {
        string serverName = await serverConfiguration.GetServerNameAsync();

        return Ok(
            new ManagementStatusDto
            {
                Status = bootStatus.IsStarted ? "running" : "starting",
                ServerName = serverName,
                Version = Software.GetReleaseVersion(),
                Platform = Info.Platform,
                Architecture = Info.Architecture,
                Os = $"{Info.Platform} {Info.OsVersion}",
                UptimeSeconds = (long)(DateTime.UtcNow - Info.StartTime).TotalSeconds,
                StartTime = Info.StartTime,
                IsDev = Config.IsDev,
                AutoStart = AutoStartupManager.IsEnabled(),
                IsDocker = Screen.IsDocker,
                UpdateAvailable = updateStatus.UpdateAvailable,
                RestartNeeded = updateStatus.RestartNeeded,
                LatestVersion = updateStatus.LatestVersion,
                SetupPhase = setupState.CurrentPhase.ToString(),
                InternalAddress = networkDiscovery.InternalAddress,
                ExternalAddress = networkDiscovery.ExternalAddress,
                // Connectivity was decided every boot and reported to nobody, so a server
                // sitting local-only looked identical to a reachable one from every surface
                // a user can actually see.
                Connectivity = new()
                {
                    State = connectivityManager.CurrentState.ToString(),
                    Transport = connectivityManager.ActiveStrategy.ToString(),
                    Mode = runtimeSettings.ConnectivityMode.ToString(),
                    NatStatus = connectivityStatus.NatStatus.ToString(),
                    TunnelAvailability = connectivityStatus.TunnelAvailability.ToString(),
                    PortForwarded = connectivityStatus.PortForwarded,
                },
                AppStatus = new()
                {
                    Running = appProcessManager.IsRunning,
                    Pid = appProcessManager.ProcessId,
                },
            }
        );
    }

    [HttpGet("logs")]
    [ProducesResponseType(typeof(List<LogEntry>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLogs(
        [FromQuery] int tail = 100,
        [FromQuery] string? types = null,
        [FromQuery] string? levels = null
    )
    {
        string[]? typeFilter = types?.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        string[]? levelFilter = levels?.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        List<LogEntry> logs = await Logger.GetLogs(
            tail,
            entry =>
            {
                bool typeMatch =
                    typeFilter is null
                    || typeFilter.Length == 0
                    || typeFilter.Any(t =>
                        string.Equals(t, entry.Type, StringComparison.OrdinalIgnoreCase)
                    );
                bool levelMatch =
                    levelFilter is null
                    || levelFilter.Length == 0
                    || levelFilter.Contains(
                        entry.Level.ToString(),
                        StringComparer.OrdinalIgnoreCase
                    );

                return typeMatch && levelMatch;
            }
        );

        return Ok(logs);
    }

    [HttpGet("logs/stream")]
    public async Task StreamLogs(
        [FromQuery] int backfill = 50,
        CancellationToken cancellationToken = default
    )
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        await Response.StartAsync(cancellationToken);

        // Bounded channel: drops oldest if client falls behind
        Channel<LogEntry> channel = Channel.CreateBounded<LogEntry>(
            new BoundedChannelOptions(500)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            }
        );

        void OnLogEmitted(LogEntry entry) => channel.Writer.TryWrite(entry);

        // Subscribe before backfill so no events are lost during backfill writes
        Logger.LogEmitted += OnLogEmitted;

        try
        {
            // Send backfill of recent log entries
            List<LogEntry> recentLogs = await Logger.GetLogs(backfill);
            foreach (LogEntry entry in recentLogs)
            {
                string json = JsonConvert.SerializeObject(entry);
                await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
            }

            await Response.Body.FlushAsync(cancellationToken);

            // Consume live events from the channel
            await foreach (LogEntry entry in channel.Reader.ReadAllAsync(cancellationToken))
            {
                string json = JsonConvert.SerializeObject(entry);
                await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            Logger.LogEmitted -= OnLogEmitted;
            channel.Writer.TryComplete();
        }
    }

    [HttpGet("activity")]
    [ProducesResponseType(typeof(ManagementActivityDto), StatusCodes.Status200OK)]
    public IActionResult GetActivity()
    {
        int activeStreams = sessionManager.ActiveSessionCount;

        IReadOnlyDictionary<string, Thread> activeThreads = queueRunner.GetActiveWorkerThreads();
        int activeEncodes = activeThreads.Count(t =>
            t.Key.StartsWith("encoder", StringComparison.OrdinalIgnoreCase)
        );

        // All encode jobs in V3 are split/resumable, so killing mid-encode is safe.
        // Streams are never "safe to interrupt" — stopping one ends playback for that user.
        bool canInterruptSafely = activeStreams == 0;

        return Ok(
            new ManagementActivityDto
            {
                ActiveStreams = activeStreams,
                ActiveEncodes = activeEncodes,
                CanInterruptSafely = canInterruptSafely,
            }
        );
    }

    [HttpPost("stop")]
    public IActionResult Stop()
    {
        appLifetime.StopApplication();
        return Ok(new { status = "ok", message = "Server is shutting down" });
    }

    [HttpPost("restart")]
    public IActionResult Restart()
    {
        appLifetime.StopApplication();
        return Ok(new { status = "ok", message = "Server is restarting" });
    }

    [HttpPost("update")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DownloadUpdate()
    {
        try
        {
            string tempPath = AppFiles.ServerTempExePath;

            StagingCheck check = ServerUpdateStaging.Check(
                storageDriver,
                Screen.IsDocker,
                Software.GetReleaseVersion(),
                tempPath,
                AppFiles.ServerExePath,
                path => Software.GetFileVersion(storageDriver, path)
            );

            if (check.DiscardedStaleVersion is not null)
                logger.LogInformation(
                    "Discarded a stale staged binary ({StagedVersion}) that is not newer than the running server.",
                    check.DiscardedStaleVersion
                );

            switch (check.State)
            {
                case StagingState.ContainerImage:
                    return Ok(ContainerImageResponse());

                case StagingState.AlreadyStaged:
                    logger.LogInformation("Update already staged, skipping download.");
                    return Ok(
                        new
                        {
                            status = "ok",
                            message = $"Update to {check.Version} already staged.",
                            path = tempPath,
                        }
                    );

                case StagingState.BinaryOnDiskIsNewer:
                    logger.LogInformation(
                        "Binary on disk is already {OnDiskVersion}, restart will apply the update.",
                        check.Version
                    );
                    return Ok(
                        new
                        {
                            status = "ok",
                            message = $"Binary on disk is already {check.Version}, restart needed.",
                        }
                    );
            }

            logger.LogInformation("Downloading server update on demand...");
            ServerUpdateResult result = await new Binaries(
                storageDriver,
                storage
            ).DownloadServerUpdate();

            switch (result)
            {
                case ServerUpdateResult.AlreadyUpToDate:
                    return Ok(new { status = "ok", message = "Server is already up to date." });

                case ServerUpdateResult.UseContainerImage:
                    return Ok(ContainerImageResponse());

                case ServerUpdateResult.UseInstaller:
                    return Ok(
                        new
                        {
                            status = "ok",
                            message = "This is an installer deployment. Use the installer to update.",
                            use_installer = true,
                            latest_version = updateStatus.LatestVersion,
                        }
                    );

                case ServerUpdateResult.RestartNeeded:
                    return Ok(
                        new
                        {
                            status = "ok",
                            message = "Binary on disk is already the latest version, restart needed to apply.",
                        }
                    );

                case ServerUpdateResult.NoAssetFound:
                    return InternalServerErrorResponse(
                        "No suitable update asset found for the current platform."
                    );

                case ServerUpdateResult.Downloaded:
                    if (!storageDriver.FileExists(tempPath))
                    {
                        logger.LogError(
                            "Server update staged file missing at {TempPath} after successful download",
                            tempPath
                        );
                        return InternalServerErrorResponse(
                            "Download completed but staged file not found. This may be caused by antivirus software quarantining the file."
                        );
                    }

                    long fileSize = storageDriver.GetFileSize(tempPath);
                    logger.LogInformation(
                        "Server update staged at {TempPath} ({FileSize} bytes)",
                        [tempPath, fileSize]
                    );
                    return Ok(
                        new
                        {
                            status = "ok",
                            message = "Update downloaded and staged.",
                            path = tempPath,
                            size = fileSize,
                        }
                    );

                default:
                    return InternalServerErrorResponse("Unexpected update result.");
            }
        }
        catch (Exception e)
        {
            logger.LogError("Failed to download update: {Message}", e.Message);
            return InternalServerErrorResponse("Failed to download update");
        }
    }

    private object ContainerImageResponse() =>
        new
        {
            status = "ok",
            message = "This server runs in a container. Pull the new image to update it — "
                + "a binary swap here cannot take effect.",
            use_container_image = true,
            latest_version = updateStatus.LatestVersion,
        };

    [HttpGet("autostart")]
    [ProducesResponseType(typeof(AutoStartDto), StatusCodes.Status200OK)]
    public IActionResult GetAutoStart()
    {
        return Ok(new AutoStartDto { Enabled = AutoStartupManager.IsEnabled() });
    }

    [HttpPost("autostart")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult SetAutoStart([FromBody] AutoStartDto request)
    {
        if (request.Enabled)
            AutoStartupManager.Initialize();
        else
            AutoStartupManager.Remove();

        return Ok(new AutoStartDto { Enabled = AutoStartupManager.IsEnabled() });
    }

    [HttpGet("config")]
    [ProducesResponseType(typeof(ManagementConfigDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConfig()
    {
        string serverName = await serverConfiguration.GetServerNameAsync();

        return Ok(
            new ManagementConfigDto
            {
                InternalPort = runtimeSettings.InternalServerPort,
                ExternalPort = runtimeSettings.ExternalServerPort,
                ServerName = serverName,
                LibraryWorkers = runtimeSettings.LibraryWorkers.Value,
                ImportWorkers = runtimeSettings.ImportWorkers.Value,
                ExtrasWorkers = runtimeSettings.ExtrasWorkers.Value,
                EncoderWorkers = runtimeSettings.EncoderWorkers.Value,
                CronWorkers = runtimeSettings.CronWorkers.Value,
                ImageWorkers = runtimeSettings.ImageWorkers.Value,
                FileWorkers = runtimeSettings.FileWorkers.Value,
                MusicWorkers = runtimeSettings.MusicWorkers.Value,
                Swagger = runtimeSettings.Swagger,
            }
        );
    }

    /// <summary>
    /// Belt-and-suspenders persist for worker counts (mirrors
    /// ConfigurationController.PersistWorkerCount). SetWorkerCount only
    /// resizes the live queue; without this write the Configuration table
    /// stays stale and the count reverts to the default on next boot.
    /// </summary>
    private async Task PersistWorkerCount(string queueName, int count)
    {
        string key = $"{queueName}Runners";
        await serverConfiguration.SetValueAsync(key, count.ToString(), null);

        await queueRunner.SetWorkerCount(queueName, count, null);
    }

    private async Task<KeyValuePair<string, int>> UpdateWorkerCountAsync(
        KeyValuePair<string, int> current,
        int? requested
    )
    {
        if (requested is not { } newCount)
            return current;

        await PersistWorkerCount(current.Key, newCount);
        return new(current.Key, newCount);
    }

    [HttpPut("config")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateConfig([FromBody] ManagementConfigUpdateDto request)
    {
        runtimeSettings.LibraryWorkers = await UpdateWorkerCountAsync(
            runtimeSettings.LibraryWorkers,
            request.LibraryWorkers
        );
        runtimeSettings.ImportWorkers = await UpdateWorkerCountAsync(
            runtimeSettings.ImportWorkers,
            request.ImportWorkers
        );
        runtimeSettings.ExtrasWorkers = await UpdateWorkerCountAsync(
            runtimeSettings.ExtrasWorkers,
            request.ExtrasWorkers
        );
        runtimeSettings.EncoderWorkers = await UpdateWorkerCountAsync(
            runtimeSettings.EncoderWorkers,
            request.EncoderWorkers
        );
        runtimeSettings.CronWorkers = await UpdateWorkerCountAsync(
            runtimeSettings.CronWorkers,
            request.CronWorkers
        );
        runtimeSettings.ImageWorkers = await UpdateWorkerCountAsync(
            runtimeSettings.ImageWorkers,
            request.ImageWorkers
        );
        runtimeSettings.FileWorkers = await UpdateWorkerCountAsync(
            runtimeSettings.FileWorkers,
            request.FileWorkers
        );
        runtimeSettings.MusicWorkers = await UpdateWorkerCountAsync(
            runtimeSettings.MusicWorkers,
            request.MusicWorkers
        );

        if (request.ServerName is not null)
        {
            await serverConfiguration.SetValueAsync(
                ServerConfigurationKeys.ServerName,
                request.ServerName,
                null
            );
        }

        return Ok(new { status = "ok", message = "Configuration updated" });
    }

    [HttpGet("plugins")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetPlugins()
    {
        IReadOnlyList<PluginInfo> plugins = pluginManager.GetInstalledPlugins();

        return Ok(
            plugins.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                description = p.Description,
                version = p.Version.ToString(),
                status = p.Status.ToString().ToLowerInvariant(),
                author = p.Author,
                project_url = p.ProjectUrl,
            })
        );
    }

    [HttpGet("queue")]
    [ProducesResponseType(typeof(ManagementQueueStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQueueStatus()
    {
        int pendingJobs = await queueTaskRepository.GetQueueJobCountAsync();
        int failedJobs = await queueTaskRepository.GetFailedJobCountAsync();

        IReadOnlyDictionary<string, Thread> activeThreads = queueRunner.GetActiveWorkerThreads();

        Dictionary<string, ManagementWorkerStatusDto> workers = new();
        foreach (
            IGrouping<string, KeyValuePair<string, Thread>> group in activeThreads.GroupBy(t =>
                t.Key.Split('-')[0]
            )
        )
        {
            workers[group.Key] = new() { ActiveThreads = group.Count() };
        }

        return Ok(
            new ManagementQueueStatusDto
            {
                Workers = workers,
                PendingJobs = pendingJobs,
                FailedJobs = failedJobs,
            }
        );
    }

    [HttpGet("resources")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetResources()
    {
        try
        {
            Resource? resource = resourceMonitor.Monitor();
            List<ResourceMonitorDto> storage = StorageMonitor.Main();

            return Ok(
                new
                {
                    cpu = resource.Cpu,
                    gpu = resource.Gpu,
                    memory = resource.Memory,
                    storage,
                }
            );
        }
        catch (Exception)
        {
            return InternalServerErrorResponse("Resource monitor failed");
        }
    }

    [HttpGet("app/status")]
    [ProducesResponseType(typeof(AppProcessStatusDto), StatusCodes.Status200OK)]
    public IActionResult GetAppStatus()
    {
        return Ok(
            new AppProcessStatusDto
            {
                Running = appProcessManager.IsRunning,
                Pid = appProcessManager.ProcessId,
            }
        );
    }

    [HttpPost("app/start")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public IActionResult StartApp()
    {
        if (appProcessManager.IsRunning)
            return ConflictResponse("App is already running");

        bool started = appProcessManager.Start();

        if (!started)
            return InternalServerErrorResponse("Failed to start app — binary not found");

        return Ok(new { status = "ok", message = "App started" });
    }

    [HttpPost("app/stop")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult StopApp()
    {
        bool stopped = appProcessManager.Stop();

        return Ok(new { status = "ok", message = stopped ? "App stopped" : "App was not running" });
    }
}
