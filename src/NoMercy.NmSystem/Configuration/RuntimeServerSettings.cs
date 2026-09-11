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

using NoMercy.NmSystem.Dto;
using NoMercyQueue.Core;

namespace NoMercy.NmSystem.Configuration;

// Process-wide mutable runtime server settings: hydrated from the database
// Configuration table at boot (UserSettings) and mutable at runtime (dashboard).
// Consumers should inject this singleton via DI; the static Current accessor
// bridges the legacy static boot path (and the Config facade) that cannot yet
// resolve services. DI hands out this same Current instance.
public class RuntimeServerSettings
{
    public static RuntimeServerSettings Current { get; } = new();

    public int InternalServerPort { get; set; } = 7626;

    public int ExternalServerPort { get; set; } = 7626;

    public int StunPort => InternalServerPort + 1;

    public KeyValuePair<string, int> LibraryWorkers { get; set; } = new(QueueNames.Library, 1);

    public KeyValuePair<string, int> ImportWorkers { get; set; } = new(QueueNames.Import, 2);

    public KeyValuePair<string, int> ExtrasWorkers { get; set; } = new(QueueNames.Extras, 4);

    public KeyValuePair<string, int> EncoderWorkers { get; set; } = new(QueueNames.Encoder, 1);

    public KeyValuePair<string, int> GpuEncoderWorkers { get; set; } =
        new(QueueNames.EncoderGpu, 1);

    public KeyValuePair<string, int> CpuEncoderWorkers { get; set; } =
        new(QueueNames.EncoderCpu, 1);

    public KeyValuePair<string, int> CronWorkers { get; set; } = new(QueueNames.Cron, 1);

    public KeyValuePair<string, int> ImageWorkers { get; set; } = new(QueueNames.Image, 3);

    public KeyValuePair<string, int> FileWorkers { get; set; } = new(QueueNames.File, 2);

    public KeyValuePair<string, int> MusicWorkers { get; set; } = new(QueueNames.Music, 2);

    public KeyValuePair<string, int> PaletteWorkers { get; set; } = new(QueueNames.Palette, 1);

    public bool Swagger { get; set; } = true;

    public bool UseSynthesizedDns { get; set; }

    // Local-only until the operator explicitly opts in. A fresh install must never start
    // trying to open itself to the internet (UPnP, tunnels) before anyone has chosen that —
    // WAN exposure is a setup step, not a default.
    public ConnectivityMode ConnectivityMode { get; set; } = ConnectivityMode.LocalOnly;

    public bool? AllowAdultContent { get; set; }

    // Safe-by-default: adult content is shown only when explicitly enabled.
    // A null (never configured) or false setting both resolve to hidden.
    public bool ShowAdultContent => AllowAdultContent == true;

    // Size cap for the content-addressed derived-audio store (rendered stems
    // and transitions); the hourly eviction cron job evicts oldest-first down
    // to this cap. 50 GiB is a reasonable default for a self-hosted server.
    public long DerivedAudioCapBytes { get; set; } = 50L * 1024 * 1024 * 1024;
}
