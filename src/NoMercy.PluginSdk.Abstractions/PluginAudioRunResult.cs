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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// What became of a run of <see cref="IPluginAudioTools" />. A run that never
/// started - a bad filter graph, a missing model file, a track the host would
/// not open - looks identical to a crash from the outside unless the reason
/// travels with the result, so it does here rather than as an exception a
/// caller has to know to catch.
/// </summary>
/// <param name="ExitCode">
/// ffmpeg's own exit code. <see cref="RefusedExitCode" /> when the run never
/// started or its result was not kept, see <see cref="Refused" />;
/// <see cref="TimedOutExitCode" /> when the host ended it at its total
/// timeout; <see cref="StalledExitCode" /> when the host ended it for making
/// no progress.
/// </param>
/// <param name="Refusal">Why not, in words the owner can act on. Null when the run started.</param>
public sealed record PluginAudioRunResult(int ExitCode, string? Refusal)
{
    /// <summary>The exit code of a refused run. The value is as old as the type; the name was added in ABI 12.7.</summary>
    public const int RefusedExitCode = -1;

    /// <summary>The exit code of a run the host ended at its total timeout. The value is as old as the type; the name was added in ABI 12.7.</summary>
    public const int TimedOutExitCode = -2;

    /// <summary>
    /// The exit code of a run the host ended because it made no progress for
    /// <see cref="PluginAudioRunOptions.StallTimeout" />. Added in ABI 12.7.
    /// </summary>
    public const int StalledExitCode = -3;

    /// <summary>
    /// The files <see cref="IPluginAudioTools.RenderAsync" /> left in the
    /// plugin's data folder, in the order they were asked for. Empty for
    /// <see cref="IPluginAudioTools.RunFilterGraphAsync(PluginAudioInput, PluginFilterGraph, Action{string}?, Action{string}?, CancellationToken)" />,
    /// which writes none, and for a render that did not exit 0, which keeps
    /// none. Added in ABI 12.7.
    /// </summary>
    public IReadOnlyList<PluginAudioRenderedFile> Outputs { get; init; } = [];

    /// <summary>True once the run started, whatever ffmpeg's own exit code turned out to be.</summary>
    public bool Ran => Refusal is null;

    public static PluginAudioRunResult Refused(string reason) => new(RefusedExitCode, reason);
}
