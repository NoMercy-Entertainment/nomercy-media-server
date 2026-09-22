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

namespace NoMercy.NmSystem.Lifecycle;

/// <summary>
/// Coarse-grained boot stages a subsystem can mark complete. Stages run in
/// parallel — there is no implicit ordering — so consumers wait for the
/// specific stage(s) they depend on. Use <see cref="All"/> for "fully idle".
/// </summary>
[Flags]
public enum BootStage
{
    None = 0,

    /// <summary>Phase 1 essentials: app folders, settings, ApiInfo.</summary>
    Essential = 1 << 0,

    /// <summary>Tokens loaded + validated (or interactive auth pending in setup mode).</summary>
    Auth = 1 << 1,

    /// <summary>ffmpeg and ffprobe on disk — safe for the encoder worker to spawn ffmpeg
    /// and for any queue to call ffprobe. Marked as soon as those two binaries are
    /// present, not when every downloadable binary is: yt-dlp, whisper models and
    /// tesseract language data continue downloading in the background after this
    /// fires, so a consumer that genuinely needs one of those must check for it
    /// directly rather than assume this stage covers it.</summary>
    Binaries = 1 << 2,

    /// <summary>External IP discovered and UPnP/port-forward attempted. This is
    /// discovery only — it says nothing about whether a connectivity strategy
    /// (port forward, Cloudflare tunnel, quick tunnel) actually succeeded, and
    /// can complete before, during, or after those are still being tried. Watch
    /// <c>IConnectivityStatus</c> for the real outcome.</summary>
    Network = 1 << 3,

    /// <summary>Hardware probe complete: GPU enumerated, encoder speed index seeded.</summary>
    Hardware = 1 << 4,

    /// <summary>SSL certificate acquired and server registered with the cloud API.</summary>
    Registered = 1 << 5,

    /// <summary>Composite "server ready" stage — login, registration, SSL and the
    /// rest of the user-facing boot have completed, so queue workers may run.
    /// Deliberately EXCLUDES <see cref="Hardware"/>: GPU/encoder detection is a
    /// deferred background task that must not hold up the server becoming usable.
    /// The encoder queues additionally wait on <see cref="Hardware"/> through their
    /// per-queue ready stage so encodes don't start before detection completes.</summary>
    All = Essential | Auth | Binaries | Network | Registered,
}
