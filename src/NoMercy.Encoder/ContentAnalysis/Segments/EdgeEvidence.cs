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

namespace NoMercy.Encoder.ContentAnalysis.Segments;

/// <summary>What confirmed a candidate's edge (stage 4), strongest first.</summary>
public enum EdgeEvidence
{
    /// <summary>The edge is the file start or end.</summary>
    File,

    /// <summary>A scene cut within 2 s.</summary>
    Cut,

    /// <summary>Black within 2 s.</summary>
    Black,

    /// <summary>Silence at the edge.</summary>
    Silence,

    /// <summary>A cut or black up to 5 s inward: the music led the picture.</summary>
    Lead,

    /// <summary>The picture was probed and shows no edge.</summary>
    None,

    /// <summary>No picture probe exists for this edge. Not a hard edge.</summary>
    Unprobed,
}
