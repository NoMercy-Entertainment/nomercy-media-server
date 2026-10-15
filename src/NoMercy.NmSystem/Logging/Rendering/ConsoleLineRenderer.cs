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

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Text;
using Pastel;

namespace NoMercy.NmSystem.Logging.Rendering;

/// <summary>
/// Renders one log entry into an aligned, optionally-coloured console block:
/// <c>HH:mm:ss [marker] [right-aligned category] | message</c>, with message
/// values token-coloured, continuation/wrapped lines hung under the gutter, and
/// any exception indented under the gutter. All column maths go through
/// <see cref="DisplayWidth"/> so alignment holds for CJK, emoji and
/// already-coloured strings.
/// </summary>
public static class ConsoleLineRenderer
{
    private const int CategoryWidth = 14;
    private const int GutterColumn = 26; // 8 time + 1 + 1 marker + 1 + 14 category + 1

    public static string Render(
        DateTime timestamp,
        LogLevel level,
        LogCategory category,
        string message,
        Exception? exception,
        NoMercyConsoleTheme theme,
        bool color,
        int width = 0
    )
    {
        string dim = ConsoleThemeColors.Dim(theme);
        (string marker, string levelHex) = ConsoleThemeColors.Level(level, theme);
        string categoryHex = ConsoleThemeColors.Category(category, theme);

        string time = timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        // Cut before padding: a name wider than the column (a plugin can be called
        // anything) would otherwise push the rule and the message out of line.
        string label = DisplayWidth.PadLeft(
            DisplayWidth.Truncate(category.DisplayName, CategoryWidth),
            CategoryWidth
        );

        StringBuilder head = new();
        head.Append(Paint(time, dim, color)).Append(' ');
        head.Append(Paint(marker, levelHex, color)).Append(' ');
        head.Append(Paint(label, categoryHex, color)).Append(' ');
        head.Append(Rule(dim, color)).Append(' ');

        string gutter = new string(' ', GutterColumn) + Rule(dim, color) + " ";

        int wrapWidth = width > GutterColumn + 4 ? width - GutterColumn - 2 : 0;
        List<string> messageLines = SplitAndWrap(message ?? string.Empty, wrapWidth);

        List<string> output = [];
        for (int i = 0; i < messageLines.Count; i++)
        {
            string rendered = RenderMessage(messageLines[i], theme, color);
            output.Add(i == 0 ? head.ToString() + rendered : gutter + rendered);
        }

        if (output.Count == 0)
            output.Add(head.ToString());

        if (exception is not null)
        {
            foreach (string raw in exception.ToString().Split('\n'))
                output.Add(gutter + Paint("└ " + raw.TrimEnd('\r'), dim, color));
        }

        return string.Join("\n", output);
    }

    private static List<string> SplitAndWrap(string message, int wrapWidth)
    {
        List<string> lines = [];
        foreach (string raw in message.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (wrapWidth <= 0 || DisplayWidth.Of(line) <= wrapWidth)
                lines.Add(line);
            else
                lines.AddRange(DisplayWidth.Wrap(line, wrapWidth));
        }

        return lines;
    }

    private static string RenderMessage(string line, NoMercyConsoleTheme theme, bool color)
    {
        if (!color)
            return line;

        // Pre-coloured content (e.g. the startup banner) already carries ANSI
        // escape sequences. Re-tokenising it would split those sequences and
        // surface their raw characters, so pass such lines through untouched.
        if (line.Contains('\u001b'))
            return line;

        string text = ConsoleThemeColors.Text(theme);
        string number = ConsoleThemeColors.Number(theme);
        string str = ConsoleThemeColors.Str(theme);

        StringBuilder builder = new();
        int index = 0;
        while (index < line.Length)
        {
            char current = line[index];
            if (current == '"')
            {
                int end = line.IndexOf('"', index + 1);
                if (end < 0)
                    end = line.Length - 1;
                builder.Append(line.Substring(index, end - index + 1).Pastel(str));
                index = end + 1;
            }
            else if (char.IsDigit(current))
            {
                int end = index;
                while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '.'))
                    end++;
                builder.Append(line.Substring(index, end - index).Pastel(number));
                index = end;
            }
            else
            {
                int end = index;
                while (end < line.Length && line[end] != '"' && !char.IsDigit(line[end]))
                    end++;
                builder.Append(line.Substring(index, end - index).Pastel(text));
                index = end;
            }
        }

        return builder.ToString();
    }

    private static string Paint(string text, string hex, bool color) =>
        color ? text.Pastel(hex) : text;

    /// <summary>
    /// The column rule is drawn as a space with a background colour rather than a
    /// box-drawing glyph. A terminal has no non-selectable pixels — everything on
    /// screen is a character cell and copying takes the cells — so the only way to
    /// keep the separator out of a pasted log line is to make the character itself
    /// something harmless. It looks identical and pastes as whitespace. With colour
    /// off, which is every redirected stream, it is simply a space.
    /// </summary>
    private static string Rule(string hex, bool color) => color ? " ".PastelBg(hex) : " ";
}
