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

using System.Reflection;
using System.Xml.Linq;
using I18N.DotNet;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.SystemCalls;
using Serilog.Events;

namespace NoMercy.NmSystem.Extensions;

public static class LocalizationHelper
{
    private static readonly AsyncLocal<ILocalizer?> RequestLocalizer = new();

    /// <summary>The localizer used outside a request, and by requests that set none.</summary>
    public static ILocalizer GlobalLocalizer { get; set; } = new Localizer();

    /// <summary>
    /// The localizer for the running request. It flows with the request's async
    /// context, so concurrent requests in different languages never share one.
    /// </summary>
    public static ILocalizer CurrentLocalizer => RequestLocalizer.Value ?? GlobalLocalizer;

    public static void UseForCurrentRequest(ILocalizer localizer) =>
        RequestLocalizer.Value = localizer;

    // Single-writer lock + in-process dedup so two concurrent requests can't
    // race on the I18N.xml file handle and so we don't repeatedly load+save
    // the document for the same missing key while a previous append is mid-write.
    private static readonly object WriteLock = new();
    private static readonly HashSet<string> SeenMissingKeys = new(StringComparer.Ordinal);

    public static string Localize(this string key)
    {
        string localized = CurrentLocalizer.Localize(key);
        if (key == localized && Config.IsDev && IsCollectableKey(key))
        {
            TryAppendMissingLocalization(key);
            return key;
        }

        return localized;
    }

    // A missing key is only worth recording as a translatable string when it is a
    // stable, human-authored label. Runtime-interpolated messages — "Segment 78 is
    // not ready yet", a served file path, "Maximum 3 sessions" — bake variable data
    // into the text, so every occurrence is a unique throwaway key that pollutes
    // I18N.xml and can never be usefully translated. Any digit or path separator is
    // the tell: a real UI label carries a placeholder token, never a live value.
    private static bool IsCollectableKey(string key) =>
        !key.Any(c => char.IsDigit(c) || c == '\\' || c == '/');

    private static void TryAppendMissingLocalization(string key)
    {
        // A best-effort dev-only convenience must NEVER break a controller
        // response. Localize() is called from DTO setters; an IOException
        // here was bubbling all the way back to the request pipeline and
        // returning 500 to the dashboard.
        try
        {
            AppendMissingLocalization(key);
        }
        catch (Exception ex)
        {
            Logger.App(
                $"LocalizationHelper: failed to record missing key '{key}': {ex.Message}",
                LogEventLevel.Warning
            );
        }
    }

    /// <summary>
    /// Path of the checked-in I18N.xml relative to a build output directory, or
    /// null when it is not there — which is every installed server, because the
    /// file only exists in a source checkout.
    ///
    /// Harvesting missing keys is a developer convenience, so on a real install it
    /// must be a silent no-op. Without this check the walk resolved to
    /// "/NoMercy.Api/Resources/I18N.xml", the load threw, and every untranslated
    /// string logged "LocalizationHelper: failed to record missing key" at users
    /// who can do nothing about it.
    /// </summary>
    internal static string? ResolveSourceI18NPath(string baseDirectory)
    {
        // Walk up rather than counting a fixed number of parents: the old
        // five-deep hop assumed bin/<cfg>/<tfm> exactly, so it missed the "src"
        // segment entirely (it looked for <repo>/NoMercy.Api/... instead of
        // <repo>/src/NoMercy.Api/...) and a publish layout with a RID folder sits
        // one level deeper again. Searching upward finds the checked-in file from
        // any build output shape, and finds nothing on an installed server.
        DirectoryInfo? directory = new DirectoryInfo(baseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "src",
                "NoMercy.Api",
                "Resources",
                "I18N.xml"
            );
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        return null;
    }

    private static void AppendMissingLocalization(string key)
    {
        // Cheap pre-check outside the lock — vast majority of calls are
        // repeat hits for the same handful of keys during a session.
        lock (SeenMissingKeys)
        {
            if (!SeenMissingKeys.Add(key))
                return;
        }

        string? filePath = ResolveSourceI18NPath(AppDomain.CurrentDomain.BaseDirectory);
        if (filePath is null)
            return;

        lock (WriteLock)
        {
            XDocument doc = XDocument.Load(filePath);

            // Re-check inside the lock against the on-disk file — a previous
            // session may have added the key already.
            bool exists =
                doc.Root?.Elements("Entry").Any(e => e.Element("Key")?.Value == key) == true;
            if (exists)
                return;

            XElement newEntry = new(
                "Entry",
                [
                    new XElement("Key", key),
                    new XElement("Value", [new XAttribute("lang", "nl"), key]),
                ]
            );

            doc.Root?.Add(newEntry);
            doc.Save(filePath);

            // Reload the localizer to include the new entry
            Localizer reportLocalizer = new();
            reportLocalizer.LoadXML(Assembly.GetExecutingAssembly(), "Resources.I18N.xml", "nl");
            GlobalLocalizer = reportLocalizer;
        }
    }
}
