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

using NoMercy.Encoder.Naming;

namespace NoMercy.Encoder.Metadata;

/// <summary>
/// Builds FFmpeg -metadata / per-stream -metadata:s:* / -disposition:* /
/// -attach arguments from DB metadata. Each flag is emitted as two separate
/// list elements (flag, value) so the process invoker handles quoting.
/// </summary>
public class MetadataInjector : IMetadataInjector
{
    public IReadOnlyList<string> BuildArgs(MetadataInjectionContext ctx)
    {
        List<string> args = [];

        EmitGlobalMetadata(args, ctx.Media);
        EmitTrackMetadata(args, ctx.Tracks);
        EmitAttachments(args, ctx.AttachmentPaths);

        return args;
    }

    // -----------------------------------------------------------------------
    // Global (container-level) metadata
    // -----------------------------------------------------------------------

    private static void EmitGlobalMetadata(List<string> args, MediaItemRef media)
    {
        AddMeta(args, "title", media.Title);

        if (media.Year.HasValue)
            AddMeta(args, "year", media.Year.Value.ToString());

        if (media is MovieMediaRef { Description: not null } movie)
            AddMeta(args, "description", movie.Description);

        if (media is EpisodeMediaRef episode)
        {
            AddMeta(args, "show", episode.ShowTitle);
            AddMeta(args, "season_number", episode.SeasonNumber.ToString());
            AddMeta(args, "episode_id", episode.EpisodeNumber.ToString());

            if (episode.Description is not null)
                AddMeta(args, "description", episode.Description);
        }
    }

    // -----------------------------------------------------------------------
    // Per-stream metadata and disposition
    // -----------------------------------------------------------------------

    private static void EmitTrackMetadata(List<string> args, IReadOnlyList<TrackMetadata> tracks)
    {
        foreach (TrackMetadata track in tracks)
        {
            string spec = StreamSpec(track.Kind, track.OutputIndex);

            if (track.Language is not null)
                AddStreamMeta(args, spec, "language", track.Language);

            if (track.Title is not null)
                AddStreamMeta(args, spec, "title", track.Title);

            EmitDisposition(args, track);
        }
    }

    /// <summary>
    /// Always writes the disposition, including an explicit <c>0</c> when the
    /// track is neither default nor forced. In copy mode ffmpeg keeps the
    /// source disposition unless told otherwise, so leaving the flag out would
    /// let a source "default" survive a library that says the track is not
    /// default (issue #349).
    /// </summary>
    private static void EmitDisposition(List<string> args, TrackMetadata track)
    {
        string dispSpec = DispositionSpec(track.Kind, track.OutputIndex);
        string value = (track.IsDefault, track.IsForced) switch
        {
            (true, true) => "default+forced",
            (true, false) => "default",
            (false, true) => "forced",
            (false, false) => "0",
        };

        args.Add(dispSpec);
        args.Add(value);
    }

    // -----------------------------------------------------------------------
    // Attachment (cover art etc.)
    // -----------------------------------------------------------------------

    private static void EmitAttachments(List<string> args, IReadOnlyList<string> attachmentPaths)
    {
        // FFmpeg attachment streams are numbered from 0. Each -attach adds one
        // stream; -metadata:s:t:N tags that stream. We track a local counter
        // so the :N index stays correct regardless of how many video/audio
        // streams precede the attachments.
        int attachIndex = 0;

        foreach (string path in attachmentPaths)
        {
            args.Add("-attach");
            args.Add(path);

            string tagSpec = $"-metadata:s:t:{attachIndex}";
            string mime = MimeTypeForExtension(Path.GetExtension(path));
            args.Add(tagSpec);
            args.Add($"mimetype={mime}");

            string filename = Path.GetFileName(path);
            args.Add(tagSpec);
            args.Add($"filename={filename}");

            attachIndex++;
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static void AddMeta(List<string> args, string key, string value)
    {
        args.Add("-metadata");
        args.Add($"{key}={value}");
    }

    private static void AddStreamMeta(
        List<string> args,
        string streamSpec,
        string key,
        string value
    )
    {
        args.Add($"-metadata:{streamSpec}");
        args.Add($"{key}={value}");
    }

    /// <summary>Returns the FFmpeg stream specifier segment for -metadata:s:* and -disposition:*.</summary>
    private static string StreamSpec(string kind, int index) =>
        kind switch
        {
            "video" => $"s:v:{index}",
            "audio" => $"s:a:{index}",
            "subtitle" => $"s:s:{index}",
            _ => $"s:a:{index}",
        };

    /// <summary>Returns the FFmpeg -disposition flag key for the given stream kind and index.</summary>
    private static string DispositionSpec(string kind, int index) =>
        kind switch
        {
            "video" => $"-disposition:v:{index}",
            "audio" => $"-disposition:a:{index}",
            "subtitle" => $"-disposition:s:{index}",
            _ => $"-disposition:a:{index}",
        };

    private static string MimeTypeForExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };
}
