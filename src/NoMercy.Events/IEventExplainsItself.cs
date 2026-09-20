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

namespace NoMercy.Events;

/// <summary>
/// An event that carries a reason worth putting in the log beside its name.
/// <para>
/// The event line names the type, the source, an id and a timestamp. For most
/// events that is the whole story. For a failure it is none of it: a plugin
/// that would not load logged that something went wrong and wrote the reason to
/// a logger nothing was reading, so the owner saw a plugin missing from the
/// list and had nowhere to find out why.
/// </para>
/// </summary>
public interface IEventExplainsItself
{
    /// <summary>One line. It sits on the end of the event line, so keep it short.</summary>
    string Why { get; }
}
