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

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Events;
using NoMercy.Events.FileWatcher;
using NoMercy.Events.Inbox;
using NoMercy.MediaProcessing.Inbox;
using NoMercy.NmSystem.Domain;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.EventHandlers;

public class InboxClassifierEventHandler : EventSubscriber
{
    private readonly IEventBus _eventBus;
    private readonly InboxClassifier _classifier;
    private readonly InboxRoutingService _routing;
    private readonly Func<MediaContext> _contextFactory;
    private readonly IStorageFactory _storageFactory;

    // Content-hash dedup keyed by (size, first-64KB MD5). Catches hard links and
    // duplicate copies that the SourcePath check misses. Per-process lifetime.
    private readonly ConcurrentDictionary<FileContentFingerprint, string> _seenContent = new();

    private readonly record struct FileContentFingerprint(long SizeBytes, string HashPrefix);

    private readonly ILogger<InboxClassifierEventHandler> _logger;

    public InboxClassifierEventHandler(
        ILogger<InboxClassifierEventHandler> logger,
        IEventBus eventBus,
        InboxClassifier classifier,
        InboxRoutingService routing,
        Func<MediaContext> contextFactory,
        IStorageFactory storageFactory
    )
    {
        _logger = logger;
        _eventBus = eventBus;
        _classifier = classifier;
        _routing = routing;
        _contextFactory = contextFactory;
        _storageFactory = storageFactory;
        Track(eventBus.Subscribe<FileCreatedEvent>(OnFileCreated));
    }

    private static async Task<FileContentFingerprint?> TryComputeFingerprintAsync(
        IStorage storage,
        string path,
        long sizeBytes,
        CancellationToken ct
    )
    {
        try
        {
            await using Stream stream = storage.OpenRead(path);
            int len = (int)Math.Min(65536, sizeBytes <= 0 ? 65536 : sizeBytes);
            byte[] buffer = new byte[len];
            int read = await stream.ReadAsync(buffer.AsMemory(0, len), ct);
            string hash = Convert.ToHexString(MD5.HashData(buffer.AsSpan(0, read)));
            return new FileContentFingerprint(sizeBytes, hash);
        }
        catch
        {
            // Can't fingerprint (directory, transient IO, permissions) — let the
            // item proceed rather than risk dropping a real file.
            return null;
        }
    }

    internal async Task OnFileCreated(FileCreatedEvent @event, CancellationToken ct)
    {
        if (@event.LibraryType != MediaTypes.InboxMediaType)
            return;

        _logger.LogInformation(
            "InboxClassifier: Processing drop event in {FolderPath}",
            @event.FolderPath
        );

        await using MediaContext context = _contextFactory();

        Library? library = await context
            .Libraries.AsNoTracking()
            .Include(l => l.FolderLibraries)
                .ThenInclude(fl => fl.Folder)
            .FirstOrDefaultAsync(l => l.Id == @event.LibraryId, ct);

        if (library is null)
        {
            _logger.LogWarning(
                "InboxClassifier: Library {LibraryId} not found, dropping event",
                @event.LibraryId
            );
            return;
        }

        FolderLibrary? folderLibrary =
            library.FolderLibraries.FirstOrDefault(fl =>
                @event.FolderPath.StartsWith(fl.Folder.Path, StringComparison.OrdinalIgnoreCase)
            ) ?? library.FolderLibraries.FirstOrDefault();

        if (folderLibrary is null)
        {
            _logger.LogWarning(
                "InboxClassifier: No folder found for library {LibraryId}",
                @event.LibraryId
            );
            return;
        }

        string inboxRoot = folderLibrary.Folder.Path;
        Ulid folderId = folderLibrary.FolderId;
        Ulid driverId = folderLibrary.Folder.DriverId;

        IStorage storage = _storageFactory.For(folderId, driverId, inboxRoot);

        IReadOnlyList<StorageEntry> children = storage.List("", null, recursive: false);

        if (children.Count == 0)
        {
            _logger.LogWarning(
                "InboxClassifier: No children found in inbox root {InboxRoot}",
                inboxRoot
            );
            return;
        }

        HashSet<string> tracked = await context
            .InboxItems.AsNoTracking()
            .Select(item => item.SourcePath)
            .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, ct);

        foreach (StorageEntry child in children)
        {
            string childPath = storage.CombinePath(inboxRoot, child.Path);

            if (tracked.Contains(childPath))
                continue;

            FileContentFingerprint? fingerprint = await TryComputeFingerprintAsync(
                storage,
                childPath,
                child.SizeBytes,
                ct
            );
            if (fingerprint is { } fp && !_seenContent.TryAdd(fp, childPath))
            {
                _logger.LogInformation(
                    "InboxClassifier: skipping {ChildPath} — duplicate content already seen at {Fp}",
                    childPath,
                    _seenContent[fp]
                );
                continue;
            }

            _logger.LogInformation(
                "InboxClassifier: Classifying inbox child {ChildPath}",
                childPath
            );

            try
            {
                ClassificationResult classification = await _classifier.Classify(
                    childPath,
                    driverId,
                    ct
                );
                RouteOutcome outcome = await _routing.Route(
                    classification,
                    childPath,
                    driverId,
                    context,
                    ct
                );

                if (outcome.Mode == "auto")
                {
                    await _routing.ExecuteAuto(outcome, context, ct);
                }
                else
                {
                    InboxItem item = outcome.Item;
                    context.InboxItems.Add(item);
                    await context.SaveChangesAsync(ct);

                    await _eventBus.PublishAsync(
                        new InboxItemDetectedEvent
                        {
                            Id = item.Id.ToString(),
                            DetectedType = item.DetectedType,
                            Confidence = item.Confidence,
                            Status = item.Status,
                        }
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "InboxClassifier: Error processing {ChildPath}: {Message}",
                    childPath,
                    ex.Message
                );

                InboxItem failedItem = new()
                {
                    Id = Ulid.NewUlid(),
                    SourcePath = childPath,
                    DriverId = driverId,
                    DetectedType = "unknown",
                    Status = "Failed",
                    Error = ex.Message,
                };

                try
                {
                    await using MediaContext failContext = _contextFactory();
                    failContext.InboxItems.Add(failedItem);
                    await failContext.SaveChangesAsync(ct);
                }
                catch (Exception saveEx)
                {
                    _logger.LogError(
                        "InboxClassifier: Could not persist Failed item for {ChildPath}: {Message}",
                        childPath,
                        saveEx.Message
                    );
                }

                await _eventBus.PublishAsync(
                    new InboxItemDetectedEvent
                    {
                        Id = failedItem.Id.ToString(),
                        DetectedType = failedItem.DetectedType,
                        Confidence = "low",
                        Status = failedItem.Status,
                    }
                );
            }
        }
    }
}
