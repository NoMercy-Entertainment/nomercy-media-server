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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// A SQLite database inside the plugin's private folder.
/// <para>
/// The host opens it, applies the plugin's migrations in order and records how
/// far it got, so a plugin never ships its own schema-version table again. The
/// file is the plugin's alone: it is not the server's library database and
/// nothing here can reach that.
/// </para>
/// <para>
/// Parameters are passed rather than interpolated. A plugin that concatenates a
/// value into the SQL can only injure its own database, but the parameter path
/// is the one that survives a value containing a quote.
/// </para>
/// </summary>
public interface IPluginDatabase : IAsyncDisposable
{
    /// <summary>
    /// Applies any statement the plugin has not applied yet, in order, in one
    /// transaction each, and remembers the high-water mark. Calling it again
    /// with the same list does nothing.
    /// </summary>
    Task MigrateAsync(IReadOnlyList<string> statements, CancellationToken ct = default);

    /// <summary>Rows affected.</summary>
    Task<int> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Every row, held in memory. Right for a query whose answer is small and
    /// known to be small.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Rows as they are read, for a query whose answer is large or whose size
    /// the plugin does not know.
    /// <para>
    /// A torrent plugin's piece table runs to tens of thousands of rows, and
    /// this facade exists to retire the hand-rolled layer that read all of them
    /// into a list first. Offering only <see cref="QueryAsync" /> would have put
    /// the same cost back on the first plugin to arrive.
    /// </para>
    /// <para>
    /// The reader is held open until enumeration ends or is cancelled, and the
    /// host closes it either way. A plugin that stops early must not keep the
    /// enumerator.
    /// </para>
    /// </summary>
    IAsyncEnumerable<IReadOnlyDictionary<string, object?>> QueryStreamAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default
    );

    /// <summary>The first column of the first row, or null when nothing came back.</summary>
    Task<object?> ScalarAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default
    );
}
