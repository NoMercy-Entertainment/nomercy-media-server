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

using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Logging.Rendering;

namespace NoMercy.NmSystem.Logging;

/// <summary>Options for the NoMercy console logger.</summary>
public sealed class NoMercyLoggerOptions
{
    /// <summary>Colour theme. Dark by default.</summary>
    public NoMercyConsoleTheme Theme { get; set; } = NoMercyConsoleTheme.Dark;

    /// <summary>Default minimum level when a category has no explicit override.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>Per-category (by <see cref="LogCategory.Key"/>) minimum level overrides.</summary>
    public Dictionary<string, LogLevel> CategoryLevels { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The default level of the session and cast categories.</summary>
    public static readonly IReadOnlyDictionary<string, LogLevel> DefaultCategoryLevels =
        new Dictionary<string, LogLevel>
        {
            ["session"] = LogLevel.Information,
            ["cast"] = LogLevel.Information,
        };

    public const string CategoryLevelVariablePrefix = "NOMERCY_LOG_LEVEL_";

    /// <summary>Sets the defaults, then applies <c>NOMERCY_LOG_LEVEL_{KEY}</c> per category.</summary>
    public void ApplyEnvironment(Func<string, string?> getVariable)
    {
        foreach ((string key, LogLevel level) in DefaultCategoryLevels)
            CategoryLevels[key] = level;

        foreach (string key in LogCategories.Keys)
        {
            string? value = getVariable(CategoryLevelVariablePrefix + key.ToUpperInvariant());
            if (Enum.TryParse(value?.Trim(), ignoreCase: true, out LogLevel level))
                CategoryLevels[key] = level;
        }
    }

    /// <summary>Force colour on/off. Null auto-detects (off when redirected or NO_COLOR is set).</summary>
    public bool? Color { get; set; }

    /// <summary>Returns the current wrap width in columns; 0 disables wrapping.</summary>
    public Func<int> WidthProvider { get; set; } = static () => 0;

    /// <summary>
    /// Directory for per-run JSONL log files. Each process start writes a new
    /// <c>run-{yyyyMMdd-HHmmss}-{pid}.jsonl</c> so readers can pick only the latest run.
    /// Null disables file logging.
    /// </summary>
    public string? LogDirectory { get; set; }

    /// <summary>How many per-run files to keep (oldest are pruned on start). Default 10.</summary>
    public int MaxRunFiles { get; set; } = 10;

    /// <summary>
    /// When true, entries from the legacy static <c>Logger</c> are also written to the per-run
    /// file (transitional, until all call sites use ILogger&lt;T&gt;). Off by default.
    /// </summary>
    public bool BridgeLegacyLogger { get; set; }

    /// <summary>When set, invoked for every entry (dashboard live-log / event bus bridge).</summary>
    public Action<NoMercyLogRecord>? OnRecord { get; set; }
}
