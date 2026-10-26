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

using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NoMercy.Database.Models.Users;

namespace NoMercy.Database.Models.Media;

/// <summary>
/// What one user keeps about one visualizer: favorite, last use and whether it
/// is the current choice. A row with none of the three is removed.
/// </summary>
[PrimaryKey(nameof(VisualizerId), nameof(UserId))]
[Index(nameof(VisualizerId))]
[Index(nameof(UserId))]
public class VisualizerUser
{
    [JsonProperty("visualizer_id")]
    public Ulid VisualizerId { get; set; }
    public Visualizer Visualizer { get; set; } = null!;

    [JsonProperty("user_id")]
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    [JsonProperty("is_favorite")]
    public bool IsFavorite { get; set; }

    [JsonProperty("last_used_at")]
    public DateTime? LastUsedAt { get; set; }

    /// <summary>Set while this visualizer is the user's current choice.</summary>
    [JsonProperty("chosen_at")]
    public DateTime? ChosenAt { get; set; }
}
