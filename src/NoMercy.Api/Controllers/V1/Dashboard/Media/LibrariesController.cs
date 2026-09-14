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
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Api.Middleware;
using NoMercy.Authorization;
using NoMercy.Data.DTOs;
using NoMercy.Data.Repositories;
using NoMercy.Data.Requests;
using NoMercy.Database;
using NoMercy.Database.Activity;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.Files;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.MediaProcessing.Shows;
using NoMercy.NmSystem.Domain;
using FolderPresetDto = NoMercy.Data.DTOs.Encoder.FolderPresetDto;
using IDefaultEncodingPresetLinker = NoMercy.MediaProcessing.Libraries.IDefaultEncodingPresetLinker;
using IJobDispatcher = NoMercy.MediaProcessing.Jobs.IJobDispatcher;

namespace NoMercy.Api.Controllers.V1.Dashboard.Media;

[ApiController]
[Tags("Dashboard Libraries")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/dashboard/libraries", Order = 10)]
public class LibrariesController(
    ILibraryRepository libraryRepository,
    IEncodingPresetRepository encodingPresetRepository,
    IFolderRepository folderRepository,
    IJobDispatcher jobDispatcher,
    ILanguageRepository languageRepository,
    IDbContextFactory<MediaContext> mediaContextFactory,
    IActivityLogger activityLogger,
    IDefaultEncodingPresetLinker defaultEncodingPresetLinker,
    IFileManager fileManager,
    IAnimeClassificationAuditService animeClassificationAuditService,
    ILogger<LibrariesController> logger,
    IEventBus eventBus
) : BaseController
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        Guid userId = User.UserId();

        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view libraries");

        IEnumerable<Library> libraries = await libraryRepository.GetLibraries(userId);

        return Ok(
            new LibrariesDto
            {
                Data = libraries.Select(library => new LibrariesResponseItemDto(library)),
            }
        );
    }

    [HttpPost]
    public async Task<IActionResult> Store()
    {
        Guid userId = User.UserId();

        if (!AuthPolicy.IsModerator(User))
            return UnauthorizedResponse("You do not have permission to create a new library");

        try
        {
            await using MediaContext mediaContext =
                await mediaContextFactory.CreateDbContextAsync();
            int libraries = await mediaContext.Libraries.CountAsync();

            Library library = new()
            {
                Id = Ulid.NewUlid(),
                Title = $"Library {libraries}",
                AutoRefreshInterval = 30,
                ChapterImages = true,
                ExtractChapters = true,
                ExtractChaptersDuring = true,
                PerfectSubtitleMatch = true,
                Realtime = true,
                SpecialSeasonName = "Specials",
                Type = MediaTypes.MovieMediaType,
                Order = 99,
            };

            await libraryRepository.AddLibraryAsync(library, userId);

            await LogLibraryCreatedAsync(userId, library);

            return Ok(
                new StatusResponseDto<Library>
                {
                    Status = "ok",
                    Data = library,
                    Message = "Successfully created a new library.",
                    Args = [],
                }
            );
        }
        catch (Exception)
        {
            return InternalServerErrorResponse("Something went wrong creating the library");
        }
    }

    private async Task LogLibraryCreatedAsync(Guid userId, Library library)
    {
        try
        {
            await activityLogger.LogConfigurationAsync(
                "config.library_added",
                userId,
                Ulid.Empty,
                configKey: $"library.{library.Id}",
                oldValue: null,
                newValue: new
                {
                    id = library.Id.ToString(),
                    name = library.Title,
                    type = library.Type,
                }
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to log library created: {Message}", ex.Message);
        }
    }

    [HttpPatch]
    [Route("{id:ulid}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Update(Ulid id, [FromBody] LibraryUpdateRequest request)
    {
        Library? library = await libraryRepository.GetLibraryByIdAsync(id);
        if (library is null)
            return NotFoundResponse("Library not found");

        if (request.EncodePresetId.HasValue)
        {
            EncodingPreset? encodingPreset = await encodingPresetRepository.GetByIdAsync(
                request.EncodePresetId.Value
            );
            if (encodingPreset is null)
                return NotFoundResponse("Encoding preset not found");
        }

        Guid userId = User.UserId();
        bool? oldRealtime = request.Realtime.HasValue ? library.Realtime : null;

        try
        {
            ApplyFieldUpdates(library, request);
            await libraryRepository.UpdateLibraryAsync(library);
            await UpdateSubtitleLanguagesAsync(library, request);
        }
        catch (Exception e)
        {
            logger.LogError(e, e.Message);
            return InternalServerErrorResponse(
                $"Something went wrong updating the library: {e.GetType().Name}: {e.Message}"
            );
        }

        if (oldRealtime.HasValue)
            await LogRealtimeScheduleChangeAsync(userId, library, oldRealtime.Value);

        string? folderSyncError = await SyncFolderLibrariesAsync(library, request);
        if (folderSyncError != null)
            return InternalServerErrorResponse(folderSyncError);

        return Ok(
            new StatusResponseDto<Library>
            {
                Status = "ok",
                Message = "Successfully updated {0} library.",
                Args = [library.Title],
                Data = library,
            }
        );
    }

    /// <summary>Only fields provided in the request are applied; everything else is left as-is.</summary>
    private static void ApplyFieldUpdates(Library library, LibraryUpdateRequest request)
    {
        if (request.Title != null)
            library.Title = request.Title;

        if (request.Image != null)
            library.Image = string.IsNullOrWhiteSpace(request.Image) ? null : request.Image;

        if (request.PerfectSubtitleMatch.HasValue)
            library.PerfectSubtitleMatch = request.PerfectSubtitleMatch.Value;

        if (request.Realtime.HasValue)
            library.Realtime = request.Realtime.Value;

        if (request.AutoEncodeOnScan.HasValue)
            library.AutoEncodeOnScan = request.AutoEncodeOnScan.Value;

        if (request.AutoConfirmDiscMatches.HasValue)
            library.AutoConfirmDiscMatches = request.AutoConfirmDiscMatches.Value;

        if (request.EncodePresetId.HasValue)
            library.EncodePresetId = request.EncodePresetId.Value;

        if (request.SpecialSeasonName != null)
            library.SpecialSeasonName = request.SpecialSeasonName;

        if (request.Type != null)
            library.Type = request.Type;
    }

    private async Task UpdateSubtitleLanguagesAsync(Library library, LibraryUpdateRequest request)
    {
        if (request.Subtitles is null)
            return;

        List<Language> languages = await languageRepository.GetLanguagesAsync();
        List<int> languageIds = request
            .Subtitles.Select(subtitle => languages.FirstOrDefault(l => l.Iso6391 == subtitle))
            .OfType<Language>()
            .Select(language => language.Id)
            .ToList();

        await libraryRepository.SetLibraryLanguagesAsync(library.Id, languageIds);
    }

    private async Task LogRealtimeScheduleChangeAsync(
        Guid userId,
        Library library,
        bool oldRealtime
    )
    {
        try
        {
            await activityLogger.LogConfigurationAsync(
                "config.library_scan_schedule_changed",
                userId,
                Ulid.Empty,
                configKey: $"library.{library.Id}.scan_schedule",
                oldValue: new { realtime = oldRealtime },
                newValue: new { realtime = library.Realtime }
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to log library scan schedule change: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Syncs the library's folders and, per folder, its encoder-profile links. Both steps
    /// need the request's <see cref="FolderLibraryDto"/> array resolved against real
    /// <see cref="Folder"/> rows, so the same lookup is shared between them. Returns a
    /// caller-facing error message on failure, or null on success.
    /// </summary>
    private async Task<string?> SyncFolderLibrariesAsync(
        Library library,
        LibraryUpdateRequest request
    )
    {
        if (request.FolderLibrary is null)
            return null;

        List<Folder> folders;
        try
        {
            folders = await folderRepository.GetFoldersByLibraryIdAsync(request.FolderLibrary);
            FolderLibrary[] folderLibraries = folders
                .Select(folder => new FolderLibrary
                {
                    LibraryId = library.Id,
                    FolderId = folder.Id,
                })
                .ToArray();

            await folderRepository.SyncFolderLibraryAsync(folderLibraries, folders);
        }
        catch (Exception e)
        {
            logger.LogError(e, e.Message);
            return $"Something went wrong updating the library folders: {e.GetType().Name}: {e.Message}";
        }

        try
        {
            List<EncodingPresetFolder> encodingPresetFolders = [];

            foreach (FolderLibraryDto folder in request.FolderLibrary)
            {
                Folder? folderDb = folders.FirstOrDefault(f => f.Id == folder.FolderId);
                if (folderDb is null)
                    continue;

                foreach (FolderPresetDto profile in folder.Folder.EncoderProfiles)
                {
                    EncodingPreset? encodingPreset = await encodingPresetRepository.GetByIdAsync(
                        profile.Id
                    );
                    if (encodingPreset is null)
                        continue;

                    encodingPresetFolders.Add(
                        new() { FolderId = folderDb.Id, PresetId = encodingPreset.Id }
                    );
                }
            }

            await libraryRepository.SyncEncodingPresetFolderAsync(encodingPresetFolders, folders);
        }
        catch (Exception e)
        {
            logger.LogError(e, e.Message);
            return $"Something went wrong updating the library encoder profiles: {e.GetType().Name}: {e.Message}";
        }

        return null;
    }

    [HttpDelete]
    [Route("{id:ulid}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Delete(Ulid id)
    {
        Library? library = await libraryRepository.GetLibraryByIdAsync(id);

        if (library is null)
            return NotFoundResponse("Library not found");

        Guid userId = User.UserId();

        try
        {
            await libraryRepository.DeleteLibraryAsync(library);

            await RemoveLibraryFromRuntimeAsync(library);
            await PublishLibraryDeletedEventsAsync(library);
            await LogLibraryRemovedAsync(userId, library);

            return Ok(
                new StatusResponseDto<string>
                {
                    Status = "ok",
                    Message = "Successfully deleted {0} library.",
                    Args = [library.Title],
                }
            );
        }
        catch (Exception e)
        {
            logger.LogError(e, e.Message);
            return InternalServerErrorResponse("Something went wrong deleting the library");
        }
    }

    private async Task RemoveLibraryFromRuntimeAsync(Library library)
    {
        // Remove all associated folders from the middleware immediately
        foreach (FolderLibrary fl in library.FolderLibraries)
            DynamicStaticFilesMiddleware.RemoveFolder(fl.FolderId);

        await using MediaContext refreshContext = await mediaContextFactory.CreateDbContextAsync();
        await UserCacheService.RefreshFolderIdsAsync(refreshContext);
    }

    private async Task PublishLibraryDeletedEventsAsync(Library library)
    {
        await eventBus.PublishAsync(
            new LibraryDeletedEvent { LibraryId = library.Id, LibraryName = library.Title }
        );

        foreach (FolderLibrary fl in library.FolderLibraries)
            await eventBus.PublishAsync(new FolderPathRemovedEvent { RequestPath = fl.FolderId });
    }

    private async Task LogLibraryRemovedAsync(Guid userId, Library library)
    {
        try
        {
            await activityLogger.LogConfigurationAsync(
                "config.library_removed",
                userId,
                Ulid.Empty,
                configKey: $"library.{library.Id}",
                oldValue: new { id = library.Id.ToString(), name = library.Title },
                newValue: null
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to log library removed: {Message}", ex.Message);
        }
    }

    [HttpPatch]
    [Route("sort")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Sort([FromBody] LibrarySortRequest request)
    {
        List<Library> libraries = await libraryRepository.GetAllLibrariesAsync();

        if (libraries.Count == 0)
            return NotFoundResponse("No libraries exist");

        try
        {
            foreach (LibrarySortRequestItem item in request.Libraries)
            {
                Library? lib = libraries.FirstOrDefault(l => l.Id == item.Id);
                if (lib is null)
                    continue;
                lib.Order = item.Order;
                await libraryRepository.UpdateLibraryAsync(lib);
            }

            return Ok(
                new StatusResponseDto<string>
                {
                    Status = "ok",
                    Message = "Successfully sorted libraries.",
                    Args = [],
                }
            );
        }
        catch (Exception)
        {
            return InternalServerErrorResponse("Something went wrong sorting the libraries");
        }
    }

    [HttpPost]
    [Route("rescan")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Rescan()
    {
        List<Library> librariesList = await libraryRepository.GetAllLibrariesAsync();

        if (librariesList.Count == 0)
            return NotFoundResponse("No libraries found to rescan");

        foreach (Library library in librariesList)
        {
            RescanLibraryFiles(library);
        }

        return Ok(
            new StatusResponseDto<List<string?>>
            {
                Status = "ok",
                Message = "Rescanning all libraries.",
            }
        );
    }

    /// <summary>
    /// A music library has no LibraryMovies and no LibraryTvs, so walking those two
    /// collections dispatched nothing at all for it and the rescan reported success
    /// having done no work. Music is not addressed per title either — its file matching
    /// runs over the library's own folders — so it dispatches once for the library.
    /// It dispatches <see cref="LibraryScanJob"/> rather than <see cref="FileRescanJob"/>:
    /// the latter is the video path, which found no parseable candidates in a music
    /// folder and went on to delete video-file and metadata records.
    /// </summary>
    private void RescanLibraryFiles(Library library)
    {
        if (library.Type == MediaTypes.MusicMediaType)
        {
            jobDispatcher.DispatchJob<LibraryScanJob>(library.Id);
            return;
        }

        foreach (LibraryMovie movie in library.LibraryMovies)
        {
            jobDispatcher.DispatchJob<FileRescanJob>(movie.MovieId, movie.LibraryId);
        }

        foreach (LibraryTv show in library.LibraryTvs)
        {
            jobDispatcher.DispatchJob<FileRescanJob>(show.TvId, show.LibraryId);
        }
    }

    [HttpPost]
    [Route("{id:ulid}/rescan")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Rescan(Ulid id)
    {
        Library? library = await libraryRepository.GetLibraryByIdAsync(id);

        if (library is null)
            return NotFoundResponse("Library not found");

        RescanLibraryFiles(library);

        return Ok(
            new StatusResponseDto<List<dynamic>>
            {
                Status = "ok",
                Message = "Rescanning {0} library.",
                Args = [library.Title],
            }
        );
    }

    /// <summary>
    /// A regular library rescan only discovers NEW folders (<see cref="ScanNewAudioFolder"/>
    /// filters by <c>existingFolders</c>), so it can never revisit a folder whose tracks are
    /// already registered — which is exactly the state a track-matching bug leaves behind: the
    /// rows exist, they are just paired with the wrong file. A mismatch is not always several
    /// tracks sharing one file (a distinct-file-count check only catches that shape) — it can
    /// also be a 1:1 swap, where two tracks each keep their own distinct file but hold each
    /// other's title. So every folder with tracked files is walked, re-running the corrected
    /// matcher (<see cref="AudioImportJob.ResolveFilesForTracks"/>) against what is actually on
    /// disk and re-storing each corrected pairing onto the SAME track ids. The job is a no-op
    /// where nothing was wrong. No file is re-encoded — only which title is attached to which
    /// already-playable source file changes.
    /// </summary>
    [HttpPost]
    [Route("{id:ulid}/repair-track-matches")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> RepairTrackMatches(Ulid id)
    {
        Library? library = await libraryRepository.GetLibraryByIdAsync(id);

        if (library is null)
            return NotFoundResponse("Library not found");

        if (library.Type != MediaTypes.MusicMediaType)
            return BadRequestResponse("This operation only applies to music libraries");

        List<TrackHostFolderDto> rows = await libraryRepository.GetTrackHostFoldersForLibraryAsync(
            id
        );

        List<(string HostFolder, Guid AlbumId)> affectedFolders =
        [
            .. rows.GroupBy(row => row.HostFolder)
                .Select(group => (group.Key, group.First().AlbumId)),
        ];

        if (affectedFolders.Count == 0)
            return Ok(
                new StatusResponseDto<List<dynamic>>
                {
                    Status = "ok",
                    Message = "No mismatched tracks found in {0}.",
                    Args = [library.Title],
                }
            );

        Folder folderLibrary = library.FolderLibraries.First().Folder;

        foreach ((string hostFolder, Guid albumId) in affectedFolders)
            jobDispatcher.DispatchJob<MusicTrackRepairJob>(
                library.Id,
                folderLibrary.Id,
                albumId,
                hostFolder
            );

        return Ok(
            new StatusResponseDto<List<dynamic>>
            {
                Status = "ok",
                Message = "Repairing track matches in {0} folder(s) of {1}.",
                Args = [affectedFolders.Count, library.Title],
            }
        );
    }

    [HttpPost]
    [Route("refresh")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> RefreshAll()
    {
        List<Library> librariesList = await libraryRepository.GetAllLibrariesAsync();

        if (librariesList.Count == 0)
            return NotFoundResponse("No libraries found to refresh");

        List<string?> titles = [];

        foreach (Library library in librariesList)
        {
            jobDispatcher.DispatchJob<LibraryRescanJob>(library.Id);
        }

        return Ok(
            new StatusResponseDto<List<string?>>
            {
                Status = "ok",
                Data = titles,
                Message = "Rescanning all libraries.",
            }
        );
    }

    [HttpPost]
    [Route("{id:ulid}/refresh")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Refresh(Ulid id)
    {
        Library? library = await libraryRepository.GetLibraryByIdAsync(id);

        if (library is null)
            return NotFoundResponse("Library not found");

        jobDispatcher.DispatchJob<LibraryRescanJob>(id);

        return Ok(
            new StatusResponseDto<List<dynamic>>
            {
                Status = "ok",
                Message = "Rescanning {0} library.",
                Args = [library.Title],
            }
        );
    }

    [HttpPost]
    [Route("scan-new")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> ScanNewAll()
    {
        List<Library> librariesList = await libraryRepository.GetAllLibrariesAsync();

        if (librariesList.Count == 0)
            return NotFoundResponse("No libraries found to scan");

        foreach (Library library in librariesList)
        {
            jobDispatcher.DispatchJob<LibraryScanJob>(library.Id);
        }

        return Ok(
            new StatusResponseDto<List<string?>>
            {
                Status = "ok",
                Message = "Scanning all libraries for new items.",
            }
        );
    }

    [HttpPost]
    [Route("{id:ulid}/scan-new")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> ScanNew(Ulid id)
    {
        Library? library = await libraryRepository.GetLibraryByIdAsync(id);

        if (library is null)
            return NotFoundResponse("Library not found");

        jobDispatcher.DispatchJob<LibraryScanJob>(id);

        return Ok(
            new StatusResponseDto<List<dynamic>>
            {
                Status = "ok",
                Message = "Scanning {0} library for new items.",
                Args = [library.Title],
            }
        );
    }

    [HttpPost]
    [Route("{id:ulid}/folders")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> AddFolder(Ulid id, [FromBody] FolderRequest request)
    {
        Library? library = await libraryRepository.GetLibraryByIdAsync(id);

        if (library is null)
            return NotFoundResponse("Library not found");

        if (request.DriverId == default)
            return BadRequestResponse("driver_id is required. Every folder must have a driver.");

        (Folder? pathAsync, bool isNewFolder, bool failed) = await ResolveOrCreateFolderAsync(
            id,
            request
        );

        if (failed)
            return InternalServerErrorResponse("Something went wrong adding the folder");

        if (pathAsync is null)
            return NotFoundResponse("Folder not found");

        FolderLibrary folderLibrary = new() { LibraryId = library.Id, FolderId = pathAsync.Id };

        await folderRepository.AddFolderLibraryAsync(folderLibrary);

        if (isNewFolder)
        {
            await defaultEncodingPresetLinker.AttachDefaultIfMissingAsync(pathAsync.Id);

            // A brand-new folder can already contain media on disk (the
            // common "point me at my existing library" flow). Dispatch the
            // same scan-new job the scan-new endpoint uses so pre-existing
            // files get imported without the user having to hit that
            // endpoint manually. Never runs for a pre-existing folder being
            // attached to another library — isNewFolder guards that — so
            // upgrades never trigger a scan for folders that were already
            // known before this slice shipped.
            jobDispatcher.DispatchJob<LibraryScanJob>(library.Id);
        }

        await RegisterFolderRuntimeAsync(pathAsync);

        return Ok(
            new StatusResponseDto<FolderLibrary>
            {
                Status = "ok",
                Message = "Successfully added folder to {0} library.",
                Args = [pathAsync.Path],
                Data = folderLibrary,
            }
        );
    }

    /// <summary>
    /// Resolves the folder a caller wants attached to a library, creating it first when it
    /// doesn't already exist. <paramref name="libraryId"/> is only used for error logging.
    /// <c>IsNewFolder</c> is captured before the create so an existing folder being attached
    /// to a second library never gets the default auto-encode preset link the new-folder
    /// branch applies.
    /// </summary>
    private async Task<(Folder? Folder, bool IsNewFolder, bool Failed)> ResolveOrCreateFolderAsync(
        Ulid libraryId,
        FolderRequest request
    )
    {
        Folder? preExistingFolder = await folderRepository.GetFolderByDriverAndPathAsync(
            request.DriverId,
            request.Path
        );
        bool isNewFolder = preExistingFolder is null;

        try
        {
            Folder folder = new()
            {
                Id = Ulid.NewUlid(),
                Path = request.Path,
                DriverId = request.DriverId,
            };

            await folderRepository.AddFolderAsync(folder);
        }
        catch (Exception ex)
        {
            logger.LogError(
                "[AddFolder] failed for library={Id} driver={DriverId} path='{Path}': {Ex}",
                libraryId,
                request.DriverId,
                request.Path,
                ex
            );
            return (null, isNewFolder, Failed: true);
        }

        // Scope the lookup to the same driver so two folders with the same
        // sub-path on different drivers (e.g. NFS Anime/Anime + S3 Anime-S3
        // both mapped to library "Anime") don't cross-link.
        Folder? pathAsync = await folderRepository.GetFolderByDriverAndPathAsync(
            request.DriverId,
            request.Path
        );

        return (pathAsync, isNewFolder, Failed: false);
    }

    private async Task RegisterFolderRuntimeAsync(Folder folder)
    {
        // Register the folder with the middleware directly so it can serve files immediately
        DynamicStaticFilesMiddleware.AddFolder(folder.Id, folder.DriverId, folder.Path);
        await using MediaContext refreshContext = await mediaContextFactory.CreateDbContextAsync();
        await UserCacheService.RefreshFolderIdsAsync(refreshContext);

        await eventBus.PublishAsync(
            new FolderPathAddedEvent
            {
                RequestPath = folder.Id,
                DriverId = folder.DriverId,
                SubPath = folder.Path,
            }
        );
    }

    [HttpPatch]
    [Route("{id:ulid}/folders/{folderId:ulid}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> UpdateFolder(
        Ulid id,
        Ulid folderId,
        [FromBody] FolderRequest request
    )
    {
        Folder? folder = await folderRepository.GetFolderByIdAsync(folderId);

        if (folder is null)
            return NotFoundResponse("Folder not found");

        try
        {
            folder.Path = request.Path;
            await folderRepository.UpdateFolderAsync(folder);

            // Update the middleware directly so it can serve files from the new path immediately
            DynamicStaticFilesMiddleware.RemoveFolder(folder.Id);
            DynamicStaticFilesMiddleware.AddFolder(folder.Id, folder.DriverId, folder.Path);
            await using (
                MediaContext refreshContext = await mediaContextFactory.CreateDbContextAsync()
            )
            {
                await UserCacheService.RefreshFolderIdsAsync(refreshContext);
            }

            await eventBus.PublishAsync(new FolderPathRemovedEvent { RequestPath = folder.Id });
            await eventBus.PublishAsync(
                new FolderPathAddedEvent
                {
                    RequestPath = folder.Id,
                    DriverId = folder.DriverId,
                    SubPath = folder.Path,
                }
            );

            return Ok(
                new StatusResponseDto<string>
                {
                    Status = "ok",
                    Message = "Successfully updated folder {0}.",
                    Args = [folder.Path],
                }
            );
        }
        catch (Exception)
        {
            return InternalServerErrorResponse("Something went wrong updating the library folder");
        }
    }

    [HttpDelete]
    [Route("{id:ulid}/folders/{folderId:ulid}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> DeleteFolder(Ulid id, Ulid folderId)
    {
        Folder? folder = await folderRepository.GetFolderByIdAsync(folderId);

        if (folder is null)
            return NotFoundResponse("Folder not found");

        try
        {
            await folderRepository.DeleteFolderAsync(folder);

            // Remove the folder from the middleware immediately
            DynamicStaticFilesMiddleware.RemoveFolder(folder.Id);
            await using (
                MediaContext refreshContext = await mediaContextFactory.CreateDbContextAsync()
            )
            {
                await UserCacheService.RefreshFolderIdsAsync(refreshContext);
            }

            await eventBus.PublishAsync(new FolderPathRemovedEvent { RequestPath = folder.Id });

            return Ok(
                new StatusResponseDto<string>
                {
                    Status = "ok",
                    Message = "Successfully deleted folder {0}.",
                    Args = [folder.Path],
                }
            );
        }
        catch (Exception ex)
        {
            // Surface the underlying failure (FK constraint, missing dep,
            // event-bus crash) so future delete-folder regressions don't
            // require Stoney to grep for a generic 500 in production logs.
            logger.LogError(
                "[DeleteFolder] folder={FolderId} library={Id} failed: {Ex}",
                folderId,
                id,
                ex
            );
            return InternalServerErrorResponse("Something went wrong deleting the library folder");
        }
    }

    [HttpPost]
    [Route("{id:ulid}/folders/{folderId:ulid}/encoder_profiles")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> AddEncoderProfile(
        Ulid id,
        Ulid folderId,
        [FromBody] ProfilesRequest request
    )
    {
        Folder? folder = await folderRepository.GetFolderByIdAsync(folderId);

        if (folder is null)
            return NotFoundResponse("Folder not found");

        try
        {
            EncodingPresetFolder[] encodingPresetFolder = request
                .Profiles.Select(profile => new EncodingPresetFolder
                {
                    FolderId = folder.Id,
                    PresetId = Ulid.Parse(profile),
                })
                .ToArray();

            await libraryRepository.AddEncodingPresetFolderAsync(encodingPresetFolder);

            return Ok(
                new StatusResponseDto<string>
                {
                    Status = "ok",
                    Message = "Successfully added encoder profile to {0} folder.",
                    Args = [folder.Path],
                }
            );
        }
        catch (Exception)
        {
            return InternalServerErrorResponse("Something went wrong adding the encoder profile");
        }
    }

    [HttpDelete]
    [Route("{id:ulid}/folders/{folderId:ulid}/encoder_profiles/{encoderProfileId:ulid}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> DeleteEncoderProfile(
        Ulid id,
        Ulid folderId,
        Ulid encoderProfileId
    )
    {
        EncodingPreset? encodingPreset = await encodingPresetRepository.GetByIdAsync(
            encoderProfileId
        );

        if (encodingPreset is null)
            return NotFoundResponse("Encoder profile not found");

        try
        {
            await libraryRepository.DeleteEncodingPresetFolderLinkAsync(folderId, encoderProfileId);

            return Ok(
                new StatusResponseDto<string>
                {
                    Status = "ok",
                    Message = "Successfully deleted encoder profile {0}.",
                    Args = [encodingPreset.Name],
                }
            );
        }
        catch (Exception)
        {
            return InternalServerErrorResponse("Something went wrong deleting the encoder profile");
        }
    }

    /// <summary>
    /// Read-only diagnostic: re-runs every existing Tv row through the
    /// shared AniList/Jikan-backed classifier and reports rows whose classification
    /// disagrees with the Type of the Library they are currently filed
    /// under. Detection only — does not move files or rewrite any row.
    /// </summary>
    [HttpGet]
    [Route("anime-classification-audit")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> AnimeClassificationAudit()
    {
        IReadOnlyList<AnimeClassificationMismatch> mismatches =
            await animeClassificationAuditService.FindMismatchesAsync(HttpContext.RequestAborted);

        return Ok(
            new StatusResponseDto<IReadOnlyList<AnimeClassificationMismatch>>
            {
                Status = "ok",
                Message = "Found {0} shows filed under the wrong library type.",
                Args = [mismatches.Count.ToString()],
                Data = mismatches,
            }
        );
    }

    [HttpPost]
    [Route("move")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Move([FromBody] MoveRequest request)
    {
        Folder? folder = await folderRepository.GetFolderByIdAsync(request.FolderId);

        if (folder is null)
            return NotFoundResponse("Folder not found");

        try
        {
            await fileManager.MoveToLibraryFolder(request.Id, folder);

            return Ok(
                new StatusResponseDto<string>
                {
                    Status = "ok",
                    Message = "Successfully moved item {0}.",
                    Args = [request.Id.ToString()],
                }
            );
        }
        catch (Exception)
        {
            return InternalServerErrorResponse("Something went wrong moving the item");
        }
    }
}
