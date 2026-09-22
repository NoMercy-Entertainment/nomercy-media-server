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
using System.Diagnostics.CodeAnalysis;

namespace NoMercy.PluginSdk;

/// <summary>
/// Owns the set of loaded plugins. Separating this storage concern from
/// <see cref="PluginManager"/> keeps the manager focused on orchestration and
/// makes the in-memory plugin set independently testable.
/// </summary>
internal interface IPluginRegistry
{
    LoadedPlugin this[Ulid id] { set; }

    bool TryGetValue(Ulid id, [MaybeNullWhen(false)] out LoadedPlugin plugin);

    bool TryRemove(Ulid id, [MaybeNullWhen(false)] out LoadedPlugin plugin);

    ICollection<LoadedPlugin> Values { get; }

    void Clear();
}
