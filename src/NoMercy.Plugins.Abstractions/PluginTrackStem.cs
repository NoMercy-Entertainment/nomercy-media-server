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
/// One stem's entry in the register - what
/// <see cref="IPluginMusicAnalysisWriter.RegisterStemAsync" /> writes so a
/// later reader can find a stem by track and kind instead of having to know
/// the derived-store key <see cref="IPluginAudioTools.SplitStemsAsync" />
/// happened to produce.
/// </summary>
/// <param name="Kind">"vocals", "accompaniment", "drums", "bass" or "other", as Spleeter named it.</param>
/// <param name="WindowStartMs">Null when <paramref name="Coverage" /> is <see cref="PluginStemCoverage.Full" />.</param>
/// <param name="WindowEndMs">Null when <paramref name="Coverage" /> is <see cref="PluginStemCoverage.Full" />.</param>
/// <param name="Format">The container/codec the stem file was written in, for example "flac".</param>
/// <param name="StorageKey">The key to read it back with <see cref="IPluginDerivedAudio.OpenReadAsync" />.</param>
/// <param name="ProducerVersion">
/// Which build of the splitter produced this stem, so a model upgrade can be
/// detected the same way <see cref="PluginTrackDjAnalysis.DjAnalyzerVersion" />
/// detects an analyzer upgrade.
/// </param>
public sealed record PluginTrackStem(
    Guid TrackId,
    string Kind,
    PluginStemCoverage Coverage,
    int? WindowStartMs,
    int? WindowEndMs,
    string Format,
    int SampleRate,
    string StorageKey,
    string ProducerVersion
);
