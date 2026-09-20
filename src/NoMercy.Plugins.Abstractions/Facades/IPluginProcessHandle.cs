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
/// One child process a plugin started.
/// <para>
/// Output is read as a stream rather than handed over at the end, because a
/// plugin watching ffmpeg's progress needs the lines as they arrive, and a
/// transcode that buffered its whole log into memory first is a plugin that
/// looks hung.
/// </para>
/// </summary>
public interface IPluginProcessHandle : IAsyncDisposable
{
    int Id { get; }

    bool HasExited { get; }

    /// <summary>Only meaningful once <see cref="HasExited" /> is true.</summary>
    int ExitCode { get; }

    TextReader StandardOutput { get; }

    TextReader StandardError { get; }

    Task<int> WaitForExitAsync(CancellationToken ct = default);

    void Kill();
}
