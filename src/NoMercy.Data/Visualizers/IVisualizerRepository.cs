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

using NoMercy.Database.Models.Media;

namespace NoMercy.Data.Visualizers;

public interface IVisualizerRepository
{
    Task<VisualizerPage> ListAsync(VisualizerListQuery query);

    Task<Visualizer?> GetAsync(Ulid id, bool includeDisabled);

    Task<VisualizerBundleVersion> GetBundleVersionAsync();

    /// <summary>Stores a new user entry; the slug is made unique here.</summary>
    Task<Visualizer> CreateAsync(Visualizer visualizer);

    Task<Visualizer?> UpdateAsync(Ulid id, Action<Visualizer> update);

    Task<bool> DeleteAsync(Ulid id);

    Task<bool> SetEnabledAsync(Ulid id, bool enabled);

    Task<VisualizerWriteResult> SetFavoriteAsync(
        Guid userId,
        Ulid id,
        bool favorite,
        bool includeDisabled
    );

    Task<VisualizerChoiceState> GetChoiceAsync(Guid userId);

    Task<VisualizerWriteResult> SetChoiceAsync(Guid userId, Ulid? id);

    Task<VisualizerImportResult> ImportDeviceStateAsync(
        Guid userId,
        string? choice,
        IReadOnlyList<string> favorites,
        IReadOnlyList<string> recents
    );
}
