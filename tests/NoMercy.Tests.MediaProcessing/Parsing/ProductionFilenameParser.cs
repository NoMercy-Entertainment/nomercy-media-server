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

using NoMercy.MediaProcessing.Files.Parsing;

namespace NoMercy.Tests.MediaProcessing.Parsing;

/// <summary>
/// The adapter set the server registers in ServiceConfiguration.Core.cs, in the
/// same order. ProductionAdapterSetTests fails when the two lists drift.
/// </summary>
internal static class ProductionFilenameParser
{
    public static IFilenameParserPipeline Pipeline() => new FilenameParserPipeline(Adapters());

    public static IFilenameParseAdapter[] Adapters() => BuiltInFilenameParser.Adapters();
}
