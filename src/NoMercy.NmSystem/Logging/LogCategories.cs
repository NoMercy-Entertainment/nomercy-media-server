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

using System.Collections.Concurrent;
using System.Globalization;

namespace NoMercy.NmSystem.Logging;

/// <summary>
/// Registry of console log categories with their dark/light colours. Curated
/// colours cover the level/system/worker/networking groups; the provider group is
/// coloured from an evenly-spaced, dark- and light-legible hue ramp so every
/// provider (TheMovieDB, MusicBrainz, TheTVDB, ...) is visually distinct.
/// <see cref="ResolveSource"/> maps a logger's source context (namespace/type) to a
/// category so call sites never pass a category by hand.
/// <para>
/// Plugins are the one group registered at runtime: a plugin only exists once it
/// loads. Each plugin gets its own category, named after it, and the whole group
/// shares one colour.
/// </para>
/// </summary>
public static class LogCategories
{
    /// <summary>
    /// The start of every plugin's category key, which is also the name of the
    /// logger a plugin logs through: <c>plugin:{id}</c>.
    /// </summary>
    public const string PluginKeyPrefix = "plugin:";

    public const string PluginGroup = "Plugins";

    // One colour for the whole group. The useful signal is "this line came from
    // code that is not ours", and the name already says which plugin. A hue per
    // plugin would land on top of the provider ramp below. Lime sits in the gap
    // the curated groups leave between yellow and green.
    public const string PluginDarkHex = "#d1e467";
    public const string PluginLightHex = "#718500";

    // The category for a plugin line whose plugin is not registered in this
    // process: an old log read after a restart, or by the CLI.
    private const string UnregisteredPluginKey = "plugin";

    private static readonly string[] ProviderKeys =
    {
        "youtube",
        "acoustid",
        "anidb",
        "audiodb",
        "coverart",
        "fanart",
        "fingerprint",
        "lrclib",
        "moviedb",
        "musicbrainz",
        "musixmatch",
        "opensubs",
        "tvdb",
    };

    private static readonly Dictionary<string, string> ProviderDisplayNames = new()
    {
        ["youtube"] = "YouTube",
        ["acoustid"] = "AcoustID",
        ["anidb"] = "AniDB",
        ["audiodb"] = "AudioDB",
        ["coverart"] = "CoverArt",
        ["fanart"] = "Fanart",
        ["fingerprint"] = "Fingerprint",
        ["lrclib"] = "Lrclib",
        ["moviedb"] = "TheMovieDB",
        ["musicbrainz"] = "MusicBrainz",
        ["musixmatch"] = "MusixMatch",
        ["opensubs"] = "OpenSubs",
        ["tvdb"] = "TheTVDB",
    };

    // Ordered longest/most-specific first so "moviedb" wins before "movie", etc.
    private static readonly (string Fragment, string Key)[] SourceFragments =
    {
        ("moviedb", "moviedb"),
        ("tmdb", "moviedb"),
        ("musicbrainz", "musicbrainz"),
        ("musixmatch", "musixmatch"),
        ("audiodb", "audiodb"),
        ("tadb", "audiodb"),
        ("coverart", "coverart"),
        ("acoustid", "acoustid"),
        ("anidb", "anidb"),
        ("lrclib", "lrclib"),
        ("fanart", "fanart"),
        ("fingerprint", "fingerprint"),
        ("opensub", "opensubs"),
        ("youtube", "youtube"),
        ("tvdb", "tvdb"),
        ("encoder", "encoder"),
        ("queue", "queue"),
        ("ripper", "ripper"),
        ("optical", "ripper"),
        ("certificate", "certificate"),
        ("registration", "register"),
        ("setup", "setup"),
        ("auth", "auth"),
        ("socket", "socket"),
        ("hub", "socket"),
        ("notify", "notify"),
        ("http", "http"),
        ("seed", "system"),
        ("configuration", "configuration"),
    };

    // Concurrent: plugins register while other threads resolve categories.
    private static readonly ConcurrentDictionary<string, LogCategory> Map = new(
        Build(),
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>The fallback category used when nothing else matches.</summary>
    public static LogCategory Default => Map["app"];

    /// <summary>Resolves a category by its key, falling back to <see cref="Default"/>.</summary>
    public static LogCategory Resolve(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return Default;
        if (Map.TryGetValue(key, out LogCategory? category))
            return category;
        if (IsPluginKey(key))
            return Map[UnregisteredPluginKey];
        return Default;
    }

    /// <summary>
    /// Maps a logger source context (e.g. a full type name) to a category by scanning
    /// for known subsystem fragments. Falls back to <see cref="Default"/>.
    /// </summary>
    public static LogCategory ResolveSource(string? sourceContext)
    {
        if (string.IsNullOrEmpty(sourceContext))
            return Default;

        // Before the fragment scan: a plugin id is random letters and digits,
        // and one that happens to contain "http" or "tvdb" must still read as
        // the plugin.
        if (IsPluginKey(sourceContext))
            return Resolve(sourceContext);

        string haystack = sourceContext.ToLowerInvariant();
        foreach ((string fragment, string key) in SourceFragments)
        {
            if (haystack.Contains(fragment, StringComparison.Ordinal))
                return Map[key];
        }

        return Default;
    }

    /// <summary>The category key, and logger name, of one plugin.</summary>
    public static string PluginKey(Ulid pluginId) => PluginKeyPrefix + pluginId;

    /// <summary>
    /// Registers the category a plugin logs under, named after the plugin, in the
    /// shared plugin colour. Registering again replaces the name, so a plugin that
    /// was renamed by an update logs under its new name from then on.
    /// </summary>
    public static LogCategory RegisterPlugin(Ulid pluginId, string? displayName)
    {
        string key = PluginKey(pluginId);
        string name = string.IsNullOrWhiteSpace(displayName) ? "Plugin" : displayName.Trim();

        LogCategory category = new(key, name, PluginGroup, PluginDarkHex, PluginLightHex);
        Map[key] = category;
        return category;
    }

    private static bool IsPluginKey(string key) =>
        key.StartsWith(PluginKeyPrefix, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, LogCategory> Build()
    {
        Dictionary<string, LogCategory> map = new(StringComparer.OrdinalIgnoreCase);

        void Add(string key, string display, string group, string dark, string light) =>
            map[key] = new(key, display, group, dark, light);

        // Levels
        Add("debug", "Debug", "Level", "#6c7086", "#8c8fa1");
        Add("verbose", "Verbose", "Level", "#585b70", "#9ca0b0");
        Add("info", "Info", "Level", "#cdd6f4", "#4c4f69");
        Add("warning", "Warning", "Level", "#f9e2af", "#df8e1d");
        Add("error", "Error", "Level", "#f38ba8", "#d20f39");
        Add("fatal", "Fatal", "Level", "#ff5370", "#e64553");

        // System
        Add("app", "App", "System", "#b4befe", "#7287fd");
        Add("access", "Access", "System", "#cba6f7", "#8839ef");
        Add("configuration", "Configuration", "System", "#89b4fa", "#1e66f5");
        Add("setup", "Setup", "System", "#74c7ec", "#209fb5");
        Add("system", "System", "System", "#89dceb", "#04a5e5");
        Add("service", "Service", "System", "#94e2d5", "#179299");
        Add("auth", "Auth", "System", "#f5c2e7", "#ea76cb");
        Add("register", "Register", "System", "#f2cdcd", "#dd7878");
        Add("certificate", "Certificate", "System", "#f5e0dc", "#dc8a78");

        // Workers
        Add("queue", "Queue", "Workers", "#fab387", "#fe640b");
        Add("encoder", "Encoder", "Workers", "#eba0ac", "#e64553");
        Add("ripper", "Ripper", "Workers", "#e5b0c5", "#b4377f");

        // Networking
        Add("http", "Http", "Networking", "#a6e3a1", "#40a02b");
        Add("notify", "Notify", "Networking", "#b5e8a0", "#5a9e1f");
        Add("ping", "Ping", "Networking", "#94e2d5", "#0a7fa8");
        Add("socket", "Socket", "Networking", "#7fc8f0", "#1e66f5");
        Add("request", "Request", "Networking", "#89dceb", "#04a5e5");

        // Providers (deterministic hue ramp -> matches the approved swatch sheets)
        for (int i = 0; i < ProviderKeys.Length; i++)
        {
            double hue = (double)i / ProviderKeys.Length;
            string dark = Hsl(hue, 0.72, 0.55);
            string light = Hsl(hue, 0.42, 0.68);
            Add(ProviderKeys[i], ProviderDisplayNames[ProviderKeys[i]], "Providers", dark, light);
        }

        // Plugins (each registers its own category when it loads)
        Add(UnregisteredPluginKey, "Plugin", PluginGroup, PluginDarkHex, PluginLightHex);

        return map;
    }

    /// <summary>HSL (matching Python colorsys.hls_to_rgb) to a #rrggbb hex string.</summary>
    private static string Hsl(double hue, double lightness, double saturation)
    {
        double m2 =
            lightness <= 0.5
                ? lightness * (1.0 + saturation)
                : lightness + saturation - lightness * saturation;
        double m1 = 2.0 * lightness - m2;

        double r = Channel(m1, m2, hue + 1.0 / 3.0);
        double g = Channel(m1, m2, hue);
        double b = Channel(m1, m2, hue - 1.0 / 3.0);

        return string.Format(
            CultureInfo.InvariantCulture,
            "#{0:x2}{1:x2}{2:x2}",
            (int)Math.Round(r * 255.0),
            (int)Math.Round(g * 255.0),
            (int)Math.Round(b * 255.0)
        );
    }

    private static double Channel(double m1, double m2, double hue)
    {
        hue %= 1.0;
        if (hue < 0.0)
            hue += 1.0;

        if (hue < 1.0 / 6.0)
            return m1 + (m2 - m1) * 6.0 * hue;
        if (hue < 0.5)
            return m2;
        if (hue < 2.0 / 3.0)
            return m1 + (m2 - m1) * (2.0 / 3.0 - hue) * 6.0;
        return m1;
    }
}
