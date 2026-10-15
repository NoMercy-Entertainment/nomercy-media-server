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

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// An exception a plugin logged in its own process, as it arrives here: text.
/// <para>
/// The exception object stayed in the plugin's process. What crossed is its
/// <c>ToString()</c>, type, message and stack trace included. Handing that
/// text to the logger as an exception, rather than appending it to the
/// message, keeps it where the log puts every other exception: under the
/// line, and in the run file's exception field.
/// </para>
/// </summary>
public sealed class PluginHostException(string remote) : Exception(FirstLine(remote))
{
    public override string ToString() => remote;

    private static string FirstLine(string text)
    {
        int end = text.IndexOf('\n');
        return (end < 0 ? text : text[..end]).TrimEnd('\r');
    }
}
