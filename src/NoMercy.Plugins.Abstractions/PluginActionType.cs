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
/// What a client does when the user activates a component. Named for the same
/// reason as <see cref="PluginComponentType"/>: an unrecognised type is a
/// silent no-op on both clients.
/// </summary>
public static class PluginActionType
{
    public const string PlayMedia = "playMedia";
    public const string Enqueue = "enqueue";
    public const string Navigate = "navigate";
    public const string CallPlugin = "callPlugin";
    public const string OpenWebView = "openWebView";
    public const string RefreshView = "refreshView";

    /// <summary>
    /// What the caller typed, posted back to the same view.
    ///
    /// A form is not a plugin method call: the answer is the next state of the
    /// page, so it goes through the view the caller is already looking at
    /// rather than through an endpoint that returns something else.
    /// </summary>
    public const string SubmitForm = "submitForm";

    /// <summary>
    /// A file the host resolved, named by the id the host gave it.
    ///
    /// Never a path. The person choosing is at a browser, a phone or a
    /// television, and the file has to exist on the server; a path typed on
    /// the wrong machine is the bug this replaces.
    /// </summary>
    public const string PickFile = "pickFile";

    /// <summary>A folder the host resolved, named by the host's own id.</summary>
    public const string PickFolder = "pickFolder";

    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            PlayMedia,
            Enqueue,
            Navigate,
            CallPlugin,
            OpenWebView,
            RefreshView,
            SubmitForm,
            PickFile,
            PickFolder,
        };
}
