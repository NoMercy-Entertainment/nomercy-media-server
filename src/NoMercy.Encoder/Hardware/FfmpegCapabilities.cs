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

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Infrastructure;
using NoMercy.NmSystem.Information;

namespace NoMercy.Encoder.Hardware;

public partial class FfmpegCapabilities(
    IProcessRunner processRunner,
    ILogger<FfmpegCapabilities>? logger = null
) : IFfmpegCapabilities
{
    private HashSet<string> _encoders = [];
    private HashSet<string> _decoders = [];
    private HashSet<string> _demuxers = [];
    private HashSet<string> _filters = [];
    private HashSet<string> _protocols = [];

    public IReadOnlySet<string> AvailableEncoders => _encoders;
    public IReadOnlySet<string> AvailableDecoders => _decoders;
    public IReadOnlySet<string> AvailableDemuxers => _demuxers;
    public IReadOnlySet<string> AvailableFilters => _filters;
    public IReadOnlySet<string> AvailableProtocols => _protocols;

    public bool HasEncoder(string name) => _encoders.Contains(name);

    public bool HasDemuxer(string name) => _demuxers.Contains(name);

    public bool HasFilter(string name) => _filters.Contains(name);

    public bool HasProtocol(string name) => _protocols.Contains(name);

    /// <summary>
    /// Re-reads what this ffmpeg build can do. Every list is replaced only when
    /// the new one has entries.
    /// <para>
    /// ffmpeg can exit 0 and print nothing — a truncated pipe, a binary being
    /// replaced, a host too loaded to flush. Parsing that produced an empty set
    /// which then replaced a good one, and an empty encoder set reads downstream
    /// as "software-only host": PlanStage drops hevc_nvenc from its candidates
    /// and resolves libx265 instead, silently, for every encode planned after
    /// that moment. 81 GPU-capable 1080p HEVC encodes were queued to libx265
    /// that way while the card sat idle, and the choice is frozen into the queue
    /// payload, so it outlives the process that made it. A probe that found
    /// nothing has learned nothing — the previous answer stands.
    /// </para>
    /// </summary>
    public async Task ProbeAsync(CancellationToken ct = default)
    {
        _encoders = Adopt(
            "-encoders",
            _encoders,
            await ProbeListAsync("-encoders", EncoderPattern(), ct)
        );
        _decoders = Adopt(
            "-decoders",
            _decoders,
            await ProbeListAsync("-decoders", EncoderPattern(), ct)
        );
        _demuxers = Adopt(
            "-demuxers",
            _demuxers,
            await ProbeListAsync("-demuxers", DemuxerPattern(), ct)
        );
        _filters = Adopt(
            "-filters",
            _filters,
            await ProbeListAsync("-filters", FilterPattern(), ct)
        );
        _protocols = Adopt(
            "-protocols",
            _protocols,
            await ProbeListAsync("-protocols", ProtocolPattern(), ct)
        );
    }

    private HashSet<string> Adopt(string flag, HashSet<string> current, HashSet<string> probed)
    {
        if (probed.Count > 0)
            return probed;

        if (current.Count == 0)
            return current;

        logger?.LogWarning(
            "ffmpeg {Flag} parsed to nothing — keeping the {Count} entries already known rather than reporting this host as having none",
            flag,
            current.Count
        );

        return current;
    }

    private async Task<HashSet<string>> ProbeListAsync(
        string flag,
        Regex pattern,
        CancellationToken ct
    )
    {
        ProcessResult result = await processRunner.RunAsync(AppFiles.FfmpegPath, [flag], null, ct);

        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"ffmpeg {flag} exited with code {result.ExitCode}: {result.StdErr.Trim()}"
            );

        HashSet<string> names = [];
        foreach (string line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = pattern.Match(line.Trim());
            if (match.Success)
                names.Add(match.Groups["name"].Value);
        }

        return names;
    }

    [GeneratedRegex(
        @"^[VASD][F.][S.][X.][B.][D.]\s+(?<name>\S+)",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex EncoderPattern();

    // Demuxer rows: "D   3dostr             3DO STR". The flag column is
    // 3 chars (D.. / .E. / ..d), real rows often print just "D" padded
    // with spaces. Exclude legend rows (third column is "=") by requiring
    // the name to start with [a-z0-9] and to be followed by a non-empty
    // description token.
    [GeneratedRegex(
        @"^[DE.d ]+\s+(?<name>[a-z0-9][a-z0-9_]*)\s+\S",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex DemuxerPattern();

    // Filter rows: optional flag chars (NoMercy fork shows 2, stock ffmpeg
    // shows 3), then a name, then a "AA->A" / "VV->V" / "N->V" type
    // signature. The signature requirement is what excludes legend rows
    // like "T.. = Timeline support" without needing a separate skip list.
    [GeneratedRegex(
        @"^[TSC.]+\s+(?<name>\S+)\s+[VANS|]+->[VANS|]+",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex FilterPattern();

    // Protocols print one identifier per line under "Input:" / "Output:"
    // headers. Match lowercase identifiers only — the headers (capitalised,
    // colon-suffixed) and the "Supported file protocols:" preamble are
    // automatically excluded.
    [GeneratedRegex(
        @"^(?<name>[a-z][a-z0-9_]*)$",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex ProtocolPattern();
}
