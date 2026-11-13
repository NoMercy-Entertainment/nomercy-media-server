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

using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Tests that read the Echo sample's process-wide construction marker. They
/// run alone, so another test that loads Echo cannot write the marker.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EchoConstructionMarkerCollection
{
    public const string Name = "Echo construction marker";
}
