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

using Mono.Nat;
using Newtonsoft.Json;
using NoMercy.Providers.Helpers;

namespace NoMercy.Api.DTOs.Common;

public class Version
{
    [JsonProperty("version")]
    public string? VersionVersion { get; set; }

    [JsonProperty("current_git_head")]
    public object? CurrentGitHead { get; set; }

    [JsonProperty("release_git_head")]
    public string? ReleaseGitHead { get; set; }

    [JsonProperty("repository")]
    public string? Repository { get; set; }
}
