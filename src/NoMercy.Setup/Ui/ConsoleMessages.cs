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

using System.Text;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Setup.Server;
using Pastel;
using Serilog.Events;

namespace NoMercy.Setup.Ui;

public abstract class ConsoleMessages
{
    private static string[] Colors => ApiKeyStore.Current.Colors;

    private static string Quote => ApiKeyStore.Current.Quote;

    public static Task ServerRunning()
    {
        if (Console.IsOutputRedirected)
            return Task.CompletedTask;

        ConsoleExtensions.Enable();

        string visitLine = Config.IsDev
            ? $"{_("#00a10d")}".Pastel("#00a10d")
                + "      "
                + "  visit:".Pastel("#cccccc")
                + $"  {ExternalServicesConfig.Current.AppBaseUrl}   ".Pastel("#ffffff")
                + $"{_("#00a10d")}".Pastel("#00a10d")
            : $"{_("#00a10d")}".Pastel("#00a10d")
                + "      "
                + "  visit:".Pastel("#cccccc")
                + $"  {ExternalServicesConfig.Current.AppBaseUrl}       ".Pastel("#ffffff")
                + $"{_("#00a10d")}".Pastel("#00a10d");

        Logger.WriteBanner([
            ("setup", ("╔" + Repeat("═", 46) + "╗").Pastel("#00a10d"), LogEventLevel.Information),
            (
                "setup",
                $"{_("#00a10d")}".Pastel("#00a10d")
                    + "     "
                    + "Secure Server running: on port:".Pastel("#5ffa71")
                    + $" {RuntimeServerSettings.Current.InternalServerPort}     ".Pastel("#ffffff")
                    + $"{_("#00a10d")}".Pastel("#00a10d"),
                LogEventLevel.Information
            ),
            ("setup", visitLine, LogEventLevel.Information),
            ("setup", ("╚" + Repeat("═", 46) + "╝").Pastel("#00a10d"), LogEventLevel.Information),
        ]);

        return Task.CompletedTask;
    }

    private static string _(string? color = null)
    {
        return "║".Pastel(color ?? Colors[0]);
    }

    private static string Repeat(string stringToRepeat, int repeat)
    {
        StringBuilder builder = new(repeat * stringToRepeat.Length);
        for (int i = 0; i < repeat; i++)
            builder.Append(stringToRepeat);

        return builder.ToString();
    }

    private static string CreateQuote(string text, int rightPadding)
    {
        // if (text.Length + rightPadding > totalSize - 2) throw new Exception("The text is too long to fit in the quote");

        List<string> spacing = [];

        // Add spaces to the left of the text
        for (int i = 102 - rightPadding; i > text.Length; i--)
            spacing.Add("");

        spacing.Add(text);

        // Add spaces to the right of the text
        for (int i = 0; i < rightPadding; i++)
            spacing.Add("");

        return string.Join(" ", spacing);
    }

    private static bool IsXmasTime()
    {
        DateTime today = DateTime.Today;
        int currentYear = today.Year;

        long xmasBeginDate = new DateTime(currentYear, 12, 7).Ticks;
        long xmasEndDate = new DateTime(currentYear + 1, 1, 5).Ticks;

        return today.Ticks > xmasBeginDate && xmasEndDate < today.Ticks;
    }

    public static void Logo()
    {
        if (Console.IsOutputRedirected)
            return;

        Console.Clear();

        StringBuilder builder = new();
        string outputString = "║  NoMercy MediaServer  ║";
        int totalWidth = 0;

        bool isXmas = IsXmasTime();

        Dictionary<string, List<string>> letters = isXmas
            ? ConsoleLetters.ColossalXmas
            : ConsoleLetters.Colossal;

        for (int i = 0; i < letters.FirstOrDefault().Value.Count - 1; i++)
        {
            foreach (char letter in outputString)
            {
                string? text = letters[letter.ToString()][i];

                text = letter switch
                {
                    '║' => text.Pastel(Colors[0]),
                    'N' or 'M' or 'S' => text.Pastel(Colors[1]),
                    _ => text.Pastel(Colors[2]),
                };

                builder.Append(text);

                if (i == 5)
                    totalWidth += letters[letter.ToString()][i].Length;
            }

            if (i == 9)
                continue;
            builder.AppendLine();
        }

        int magicSpacer = totalWidth - 2;

        Console.WriteLine($"{("╔" + Repeat("═", magicSpacer) + "╗").Pastel(Colors[0])}");
        Console.WriteLine($"{_()}{Repeat(" ", magicSpacer)}{_()}");

        Console.WriteLine(builder.ToString());

        Console.WriteLine(
            $"{_()}{Repeat(" ", 63)}{letters["y"][10].Pastel(Colors[2])}"
                + CreateQuote(Quote, 4)
                + $"{letters["║"][0].Pastel(Colors[0])}"
        );
        // Console.WriteLine($"{_()}" + CreateQuote(Quote, totalWidth, 4) + $"{(isXmas() ? ConsoleLetters.ColossalXmas : ConsoleLetters.Colossal)["║"][0].Pastel(Colors[0])}");
        Console.WriteLine($"{("╚" + Repeat("═", magicSpacer) + "╝").Pastel(Colors[0])}");
    }

    public static Task Welcome()
    {
        Console.WriteLine(("╔" + Repeat("═", 46) + "╗").Pastel("#00a10d"));
        Console.WriteLine(
            $"{_("#00a10d")}".Pastel("#00a10d")
                + @"     "
                + "Welcome to NoMercy MediaServer".Pastel("#5ffa71")
                + "     ".Pastel("#ffffff")
                + $"{_("#00a10d")}".Pastel("#00a10d")
        );
        Console.WriteLine(
            $"{_("#00a10d")}".Pastel("#00a10d")
                + @"      "
                + "Version:".Pastel("#cccccc")
                + $"  {Software.GetReleaseVersion()}      ".Pastel("#ffffff")
                + $"{_("#00a10d")}".Pastel("#00a10d")
        );
        Console.WriteLine(("╚" + Repeat("═", 46) + "╝").Pastel("#00a10d"));

        return Task.CompletedTask;
    }

    private static void SetConsoleSize(int width, int height)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Console.SetWindowSize(
                    Math.Min(width, Console.LargestWindowWidth),
                    Math.Min(height, Console.LargestWindowHeight)
                );
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                Console.Write($"\x1b[8;{height};{width}t");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to resize console: {ex.Message}");
        }
    }

    private static void ClearConsole()
    {
        Console.Clear();
        Console.SetCursorPosition(0, 0);
    }
}
