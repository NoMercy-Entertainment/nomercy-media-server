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

namespace NoMercy.NmSystem.Configuration;

public class EncoderResourceConfig
{
    // TryAcquire refuses a new encoder lease once host CPU is at or above this,
    // so it is the point the box stops being usable for anything else. At 90 two
    // CPU tasks pin the machine and the gate still says yes; ResourceBudgetOptions
    // documents 75 as the "leave headroom for the user's other work" figure and
    // this is what actually reaches the gate.
    public double EncoderCpuHeadroomPercent { get; set; } = 75.0;

    // The GPU is the one piece we do want saturated: it is dedicated to encoding
    // and nothing else on the host competes for its encoder blocks.
    public double EncoderGpuHeadroomPercent { get; set; } = 95.0;

    // Estimated encoder load of one running GPU encode. Counts against the GPU
    // headroom where nothing measures the encoder; 0 turns the estimate off.
    public double EncoderGpuPercentPerEncode { get; set; } = 20.0;
    public long EncoderMinFreeMemoryMb { get; set; } = 1024;
}
