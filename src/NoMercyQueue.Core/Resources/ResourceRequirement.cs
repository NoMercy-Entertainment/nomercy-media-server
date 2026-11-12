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

namespace NoMercyQueue.Core.Resources;

/// <summary>
/// Declares the hardware resources required to run one encode task.
/// <see cref="GpuDeviceKey"/> is the canonical name string from
/// <c>GpuDevice.Name</c>; null means CPU-only.
/// </summary>
public record ResourceRequirement(
    string? GpuDeviceKey,
    int GpuSlots,
    int CpuThreads,
    bool Interactive = false
);
