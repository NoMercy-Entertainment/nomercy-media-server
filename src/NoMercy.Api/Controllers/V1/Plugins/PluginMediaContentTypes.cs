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

using Microsoft.AspNetCore.StaticFiles;

namespace NoMercy.Api.Controllers.V1.Plugins;

/// <summary>
/// The media type a plugin's file is served under, by its name. One place,
/// so a message that names the type of a file agrees with the answer that
/// serves it.
/// </summary>
internal static class PluginMediaContentTypes
{
    private static readonly FileExtensionContentTypeProvider Types = Known();

    public static string Of(string file) =>
        Types.TryGetContentType(file, out string? type) ? type : "application/octet-stream";

    private static FileExtensionContentTypeProvider Known()
    {
        FileExtensionContentTypeProvider types = new();
        types.Mappings[".ts"] = "video/mp2t";

        return types;
    }
}
