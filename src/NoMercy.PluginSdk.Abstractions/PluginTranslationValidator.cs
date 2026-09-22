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

using System.Text.Json;

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// Checks a plugin's translations against the locale it was authored in.
///
/// A missing key is the failure nobody notices: the plugin loads, the page
/// renders, and one label sits there in English for every Dutch viewer. Checking
/// at load turns that into something the plugin author sees on their own machine.
/// </summary>
public static class PluginTranslationValidator
{
    /// <summary>
    /// Reads every declared locale and reports what does not line up with the
    /// source. An empty list means the plugin is fully translated.
    /// </summary>
    public static List<PluginTranslationProblem> Validate(
        PluginTranslations translations,
        Func<string, string?> readLocaleFile
    )
    {
        List<PluginTranslationProblem> problems = [];

        string? sourceText = readLocaleFile(translations.Source);
        if (sourceText is null)
        {
            problems.Add(
                new()
                {
                    Locale = translations.Source,
                    Detail =
                        "the source locale is declared but its file is missing, so there is nothing to measure against",
                }
            );
            return problems;
        }

        Dictionary<string, string>? source = Read(sourceText);
        if (source is null)
        {
            problems.Add(
                new()
                {
                    Locale = translations.Source,
                    Detail = "the source locale is not readable as a flat set of strings",
                }
            );
            return problems;
        }

        foreach (string locale in translations.Locales)
        {
            if (locale == translations.Source)
                continue;

            string? text = readLocaleFile(locale);
            if (text is null)
            {
                problems.Add(new() { Locale = locale, Detail = "declared but no file was found" });
                continue;
            }

            Dictionary<string, string>? entries = Read(text);
            if (entries is null)
            {
                problems.Add(
                    new() { Locale = locale, Detail = "not readable as a flat set of strings" }
                );
                continue;
            }

            foreach (string key in source.Keys)
            {
                if (!entries.ContainsKey(key))
                    problems.Add(new() { Locale = locale, Detail = $"missing key '{key}'" });
                else if (string.IsNullOrWhiteSpace(entries[key]))
                    problems.Add(
                        new()
                        {
                            Locale = locale,
                            Detail =
                                $"key '{key}' is empty, which reads as a blank label rather than as untranslated",
                        }
                    );
            }

            foreach (string key in entries.Keys)
            {
                if (!source.ContainsKey(key))
                    problems.Add(
                        new()
                        {
                            Locale = locale,
                            Detail =
                                $"key '{key}' is not in the source locale, so nothing will ever read it",
                        }
                    );
            }
        }

        return problems;
    }

    private static Dictionary<string, string>? Read(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
