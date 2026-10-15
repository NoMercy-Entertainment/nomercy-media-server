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

using Newtonsoft.Json;
using NoMercy.Encoder.Analysis;

namespace NoMercy.Api.Services;

/// <summary>
/// A video file's analysis, kept with the file's id so a playback start reads it
/// instead of running ffprobe again. ffprobe on every start cost seconds on slow
/// storage, before the client could fetch a single byte. The stored analysis is
/// used only while the file's row is unchanged: a rescan that touches the row
/// (a replaced or re-encoded file) makes the next start analyze it again.
/// </summary>
public sealed class StoredMediaAnalysis(IMediaAnalyzer analyzer, string folder)
{
    public async Task<MediaInfo> GetOrAnalyzeAsync(
        Ulid videoFileId,
        DateTime fileUpdatedAt,
        string inputPath,
        string[]? extraInputArgs,
        CancellationToken ct
    )
    {
        string path = Path.Combine(folder, $"{videoFileId}.json");

        StoredEntry? stored = await ReadAsync(path, ct);
        if (stored is not null && stored.FileUpdatedAt == fileUpdatedAt)
            return stored.Info;

        MediaInfo info = await analyzer.AnalyzeAsync(inputPath, extraInputArgs, ct);
        await WriteAsync(path, new StoredEntry(fileUpdatedAt, info), ct);
        return info;
    }

    private static async Task<StoredEntry?> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            string json = await File.ReadAllTextAsync(path, ct);
            return JsonConvert.DeserializeObject<StoredEntry>(json);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // A torn or unreadable entry is a missing one: analyze and rewrite it.
            return null;
        }
    }

    private static async Task WriteAsync(string path, StoredEntry entry, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonConvert.SerializeObject(entry), ct);
        File.Move(temp, path, overwrite: true);
    }

    private sealed record StoredEntry(DateTime FileUpdatedAt, MediaInfo Info);
}
