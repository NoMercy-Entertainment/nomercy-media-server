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

namespace NoMercy.MediaProcessing.DerivedAudio;

/// <summary>
/// What a derived-audio key is allowed to look like.
/// <para>
/// <see cref="IDerivedAudioStore.PutAsync" /> is the only thing that mints a
/// key, and it always mints the lowercase hex of a SHA-256 digest: exactly 64
/// characters from <c>0-9a-f</c>. Anything else was invented by a caller, and
/// a caller here can be a third-party plugin, so it is checked rather than
/// trusted - <see cref="IDerivedAudioStore.RelativePath" /> slices a key into
/// a path, and a key that is not a digest is a path traversal waiting to
/// happen.
/// </para>
/// </summary>
public static class DerivedAudioKey
{
    /// <summary>The length of a lowercase hex SHA-256 digest.</summary>
    public const int Length = 64;

    /// <summary>
    /// True only for a key <see cref="IDerivedAudioStore.PutAsync" /> could
    /// have produced. Uppercase hex is rejected on purpose: the store writes
    /// lowercase, so an uppercase key would miss on a case-sensitive
    /// filesystem and hit on a case-insensitive one.
    /// </summary>
    public static bool IsValid(string? key)
    {
        if (key is null || key.Length != Length)
        {
            return false;
        }

        foreach (char character in key)
        {
            bool isHexDigit = character is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isHexDigit)
            {
                return false;
            }
        }

        return true;
    }
}
