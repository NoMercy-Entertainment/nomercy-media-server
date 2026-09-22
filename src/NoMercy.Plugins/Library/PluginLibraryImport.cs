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

using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Library;

/// <summary>
/// Where a plugin hands over a finished file.
/// <para>
/// A plugin that downloaded or recorded something had nowhere to put it, so it
/// wrote into a library folder and hoped the scanner noticed. A half-written
/// file picked up mid-copy was filed as a broken episode the owner then had to
/// delete twice.
/// </para>
/// <para>
/// Registering says the file is finished. The server files it, so the plugin
/// never has to know how this library names its folders, and the scan that
/// files it runs once rather than on a timer that might catch a copy halfway.
/// </para>
/// </summary>
public class PluginLibraryImport(
    Ulid pluginId,
    IPluginLibraryWriter writer,
    IPluginLibraryScanner scanner,
    ILogger logger
) : IPluginLibraryImport
{
    public Task<PluginImportResult> RegisterAsync(
        PluginImportRequest request,
        CancellationToken ct = default
    ) => ImportAsync(request, streaming: false, ct);

    public Task<PluginImportResult> StreamAsync(
        PluginImportRequest request,
        CancellationToken ct = default
    ) => ImportAsync(request, streaming: true, ct);

    private async Task<PluginImportResult> ImportAsync(
        PluginImportRequest request,
        bool streaming,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath))
            return Refused("The request named no file.", "Give SourcePath the file to import.");

        // The same two checks the writer makes, because this is a write: the
        // plugin holds a grant for the library, and the path resolves inside
        // it. A grant names a library, not a starting point to walk out of.
        if (!await writer.CanWriteAsync(request.SourcePath, ct))
            return Refused(
                "The file is not somewhere this plugin may write.",
                "Ask the owner to grant this plugin write access to the library the file is in."
            );

        IReadOnlyList<PluginLibrary> writable = await writer.GetWritableLibrariesAsync(ct);
        string library = request.Library.ToString();

        if (!writable.Any(entry => entry.Id == library))
            return Refused(
                $"This plugin may not write to library {library}.",
                "Ask the owner to grant this plugin write access to that library."
            );

        logger.LogInformation(
            "Plugin {PluginId} is importing {Path} into library {LibraryId}{Streaming}",
            pluginId,
            request.SourcePath,
            library,
            streaming ? " while it is still being written" : string.Empty
        );

        // A file still being written is filed now and scanned when it ends, so
        // a viewer can start a program that has not finished airing. Scanning
        // it now would measure a length that is still growing.
        MediaId media = await scanner.ImportAsync(request, streaming, ct);

        return new(media, null);
    }

    private PluginImportResult Refused(string why, string fix) =>
        new(
            default,
            new(
                PluginRefusalCodes.LibraryImportDenied,
                pluginId.ToString(),
                "The server did not import the file.",
                why,
                fix,
                PluginRefusalSeverity.Blocked
            )
        );
}

/// <summary>
/// The server side of an import: filing one finished file into a library.
/// <para>
/// A seam rather than a direct call into media processing, because what filing
/// means differs per library type and a plugin should not learn any of it.
/// </para>
/// </summary>
public interface IPluginLibraryScanner
{
    Task<MediaId> ImportAsync(
        PluginImportRequest request,
        bool streaming,
        CancellationToken ct = default
    );
}
