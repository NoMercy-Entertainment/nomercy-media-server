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

namespace NoMercy.Data.Plugins;

/// <summary>
/// The GPU's one place for a RoFormer run. The four models of
/// <c>preset=full</c> take 5.4 GB on an RTX 3070, so two runs at once do not
/// fit on most cards, and the second would be refused for its memory and
/// remembered as if the card could not run RoFormer at all.
/// <para>
/// One place for the whole machine, not one per GPU: the filter picks its
/// device itself under <c>use_gpu=auto</c>, and the host cannot tell which
/// one a run will take.
/// </para>
/// </summary>
public interface IRoformerGpuSlot
{
    /// <summary>Waits until no other RoFormer run holds the GPU, and holds it until the result is disposed.</summary>
    Task<IDisposable> AcquireAsync(CancellationToken ct);
}
