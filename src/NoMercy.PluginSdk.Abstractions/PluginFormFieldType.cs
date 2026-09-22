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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>What a form field collects.</summary>
public static class PluginFormFieldType
{
    public const string Text = "text";
    public const string Password = "password";
    public const string Number = "number";
    public const string Toggle = "toggle";
    public const string Select = "select";

    /// <summary>A single on/off box in a set, as opposed to a standalone toggle.</summary>
    public const string Checkbox = "checkbox";

    /// <summary>A file upload, posted to the plugin's own REST endpoint.</summary>
    public const string File = "file";

    /// <summary>
    /// A place on the server to put things, chosen rather than typed.
    /// <para>
    /// Every plugin that needs somewhere to write was asking its owner to type
    /// an absolute path into a text box. Nothing validated it, and it was typed
    /// on the wrong machine: the owner is at a browser and the path has to exist
    /// on the server, so a typo was accepted, saved, and discovered hours later
    /// when a download finished and could not be staged.
    /// </para>
    /// </summary>
    public const string Folder = "folder";

    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Text,
            Password,
            Number,
            Toggle,
            Select,
            Checkbox,
            File,
            Folder,
        };
}
