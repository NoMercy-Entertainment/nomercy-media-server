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
using Newtonsoft.Json;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Api.Services;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Data.Services;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Profiles;
using V2EncodingProfile = NoMercy.Encoder.Profiles.EncodingProfile;
using V2IPresetLookup = NoMercy.Encoder.Profiles.IPresetLookup;
using V2PresetResolver = NoMercy.Encoder.Profiles.PresetResolver;
using V2ProfileDiffer = NoMercy.Encoder.Profiles.ProfileDiffer;
using V2ProfileValidationResult = NoMercy.Encoder.Profiles.ProfileValidationResult;
using V2ProfileValidator = NoMercy.Encoder.Profiles.ProfileValidator;

namespace NoMercy.Api.Controllers.V1.Encoder;

public record PreviewEncoderProfileRequest(
    [property: JsonProperty("profile_json")] string ProfileJson,
    [property: JsonProperty("source_path")] string? SourcePath
);
