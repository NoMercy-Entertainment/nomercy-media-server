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
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Verification;

// The exact text the marketplace signs: sorted keys, no whitespace, non-ASCII
// as lowercase \uXXXX, "/" left alone. Written by hand because a general JSON
// writer escapes differently, and one different byte fails every signature.
public static class PluginSignatureSubject
{
    public static string Build(PluginIndexSignature index)
    {
        StringBuilder subject = new();

        subject.Append("{\"capabilities\":[");
        for (int i = 0; i < index.Capabilities.Count; i++)
        {
            if (i > 0)
                subject.Append(',');
            AppendString(subject, index.Capabilities[i]);
        }
        subject.Append("],\"id\":");
        AppendString(subject, index.PluginId);
        subject.Append(",\"publisher\":");
        AppendString(subject, index.PublisherId ?? string.Empty);
        subject.Append(",\"sha256\":");
        AppendString(subject, index.Sha256 ?? string.Empty);
        subject.Append(",\"tier\":");
        AppendString(subject, index.Tier ?? string.Empty);
        subject.Append(",\"version\":");
        AppendString(subject, index.Version);
        subject.Append('}');

        return subject.ToString();
    }

    private static void AppendString(StringBuilder subject, string value)
    {
        subject.Append('"');

        foreach (char character in value)
        {
            switch (character)
            {
                case '"':
                    subject.Append("\\\"");
                    break;
                case '\\':
                    subject.Append("\\\\");
                    break;
                case '\b':
                    subject.Append("\\b");
                    break;
                case '\f':
                    subject.Append("\\f");
                    break;
                case '\n':
                    subject.Append("\\n");
                    break;
                case '\r':
                    subject.Append("\\r");
                    break;
                case '\t':
                    subject.Append("\\t");
                    break;
                default:
                    // A char is one UTF-16 unit, so an astral character comes
                    // out as its surrogate pair, which is what the signer writes.
                    if (character < ' ' || character > '\u007f')
                        subject
                            .Append("\\u")
                            .Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        subject.Append(character);
                    break;
            }
        }

        subject.Append('"');
    }
}
