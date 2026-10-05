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
/// <c>ExternalServicesConfig.Current</c> statics and the on-disk API key cache. xUnit runs
/// classes in parallel by default, so a class that points a base URL at a loopback server
/// or writes the key cache is visible to every other class running at that moment; the
/// symptom is one unrelated assertion flipping while both pass in isolation.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ProcessWideSetupStateCollection
{
    public const string Name = "process-wide setup state";
}
