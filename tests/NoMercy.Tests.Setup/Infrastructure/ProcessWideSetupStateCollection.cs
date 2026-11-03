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

namespace NoMercy.Tests.Setup.Infrastructure;

/// <summary>
/// Serialises the test classes that touch state shared by the whole process — the
/// <c>ExternalServicesConfig.Current</c> statics, the on-disk API key cache, the
/// <c>SetupTerminalUi.ForceInteractiveForTests</c> console override and the
/// <c>NOMERCY_APP_PATH</c> / <c>NOMERCY_INSTALL_DIR</c> environment variables that
/// <c>AppFiles</c> resolves on every read. xUnit runs classes in parallel by default, so a
/// class that points a base URL at a loopback server, writes the key cache or moves the app
/// path to its own temp tree is visible to every other class running at that moment; the
/// symptom is one unrelated assertion flipping while both pass in isolation — a file
/// written under one class's app path and looked for under another's.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ProcessWideSetupStateCollection
{
    public const string Name = "process-wide setup state";
}
