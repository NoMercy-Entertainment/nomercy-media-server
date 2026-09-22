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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// Changing what is in a user's library: the highest-risk thing a plugin can
/// do, and until now the only one nothing gated.
/// <para>
/// A plugin could already delete a user's media, because it runs in-process and
/// <c>System.IO</c> is right there. Nothing declared it, nothing prompted, and
/// nothing bounded where it could reach. The hole was exactly where the
/// consequences are worst.
/// </para>
/// <para>
/// Four things make this safe to hand out, and all four matter:
/// <list type="bullet">
/// <item>It is its own capability, so declaring it is visible in the manifest
/// and deliberate.</item>
/// <item>It is elevated by definition, so it can never ride in on a baseline
/// auto-enable — the owner grants it or it does not happen.</item>
/// <item>It is jailed to the libraries the owner granted, by id, and every path
/// resolves through the storage facade rather than raw file calls, so the jail
/// is enforced where the rest of the server enforces it.</item>
/// <item><see cref="RecycleAsync"/> exists so a plugin can be built to never
/// hard-delete, and is what a well-behaved plugin calls instead of
/// <see cref="DeleteAsync"/>.</item>
/// </list>
/// </para>
/// <para>
/// Every method throws when the plugin holds no grant for the library the path
/// belongs to, and when the path escapes that library's root. Neither is a
/// return value: a plugin that ignores a false and carries on would be the
/// whole problem back again.
/// </para>
/// </summary>
public interface IPluginLibraryWriter
{
    /// <summary>The libraries this plugin may write to, resolved from its grants.</summary>
    Task<IReadOnlyList<PluginLibrary>> GetWritableLibrariesAsync(CancellationToken ct = default);

    /// <summary>
    /// Moves <paramref name="path"/> to the server's recycle area, where the
    /// owner can still get it back. The preferred way to remove a superseded
    /// file — a quality upgrade that replaces an episode should call this.
    /// </summary>
    Task RecycleAsync(string path, CancellationToken ct = default);

    /// <summary>Deletes <paramref name="path"/> outright, with no way back.</summary>
    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>Moves a file within the libraries this plugin may write to. Both ends are checked.</summary>
    Task MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default);

    /// <summary>Whether the plugin may write at <paramref name="path"/>, for a plugin that wants to check before it acts.</summary>
    Task<bool> CanWriteAsync(string path, CancellationToken ct = default);
}

/// <summary>Thrown when a plugin reaches outside what the owner granted it.</summary>
public class PluginLibraryAccessDeniedException(string path, string reason)
    : Exception($"Plugin library access to '{path}' was denied: {reason}")
{
    public string Path { get; } = path;
}
