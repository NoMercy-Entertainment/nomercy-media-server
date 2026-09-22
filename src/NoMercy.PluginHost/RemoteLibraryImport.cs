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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginHost;

/// <summary>Handing a finished download or recording to the library.</summary>
public sealed class RemoteLibraryImport(Ulid pluginId, RemoteCall call) : IPluginLibraryImport
{
    public Task<PluginImportResult> RegisterAsync(
        PluginImportRequest request,
        CancellationToken ct = default
    ) => AskAsync(nameof(IPluginLibraryImport.RegisterAsync), request);

    public Task<PluginImportResult> StreamAsync(
        PluginImportRequest request,
        CancellationToken ct = default
    ) => AskAsync(nameof(IPluginLibraryImport.StreamAsync), request);

    private async Task<PluginImportResult> AskAsync(string member, PluginImportRequest request) =>
        await call.AskAsync<PluginImportResult>("libraryImport", member, new { request })
        ?? new PluginImportResult(
            default,
            new PluginRefusal(
                PluginRefusalCodes.HostServicesRemoved,
                pluginId.ToString(),
                $"The plugin called libraryImport.{member}.",
                "The server answered with nothing where an import result belongs.",
                "Report this with the server log around the call. Docs: /nomercy-plugins/handbook/runtime-and-isolation",
                PluginRefusalSeverity.Blocked
            )
        );
}
