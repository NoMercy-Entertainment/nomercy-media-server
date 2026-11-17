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

/// <summary>
/// The picture probes taken for one episode, keyed by the time they were taken
/// at. An edge uses the probe nearest to it, when that one was taken within
/// <see cref="SegmentRules.EdgeProbeMatchSeconds"/>; on a tie the earliest added wins.
/// </summary>
public sealed class EdgeProbeSet
{
    private readonly List<(double At, EdgeProbe Probe)> _probes = [];

    public int Count => _probes.Count;

    public void Add(double at, EdgeProbe probe) => _probes.Add((at, probe));

    public bool Contains(double at) => _probes.Any(p => p.At.Equals(at));

    public EdgeProbe? Nearest(double t)
    {
        EdgeProbe? best = null;
        double bestDistance = double.MaxValue;
        foreach ((double at, EdgeProbe probe) in _probes)
        {
            double distance = Math.Abs(at - t);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = probe;
            }
        }

        return bestDistance <= SegmentRules.EdgeProbeMatchSeconds ? best : null;
    }
}
