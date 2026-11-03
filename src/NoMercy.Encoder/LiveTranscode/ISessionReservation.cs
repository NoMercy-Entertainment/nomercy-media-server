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

namespace NoMercy.Encoder.LiveTranscode;

/// <summary>
/// A live-session slot claimed by <see cref="ISessionManager.TryReserve"/> for a
/// start that has not registered its session yet. The claim counts against both
/// caps until it is settled: <see cref="Complete"/> turns it into the registered
/// session and keeps the slot; <see cref="IDisposable.Dispose"/> on an
/// uncompleted claim hands the slot back. Hold it in a <c>using</c> around the
/// whole start sequence so a failed probe, a failed encoder start, a
/// cancellation or a direct-play decision all release it.
/// </summary>
public interface ISessionReservation : IDisposable
{
    /// <summary>The user the slot was claimed for; null for an anonymous start.</summary>
    string? UserId { get; }

    /// <summary>
    /// Registers <paramref name="session"/> under <see cref="UserId"/> in the same
    /// step that drops the claim, so the slot never reads as free in between.
    /// Throws when the claim was already completed or released.
    /// </summary>
    void Complete(ILiveSession session);
}
