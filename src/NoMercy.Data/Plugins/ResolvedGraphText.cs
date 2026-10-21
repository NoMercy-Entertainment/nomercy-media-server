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

namespace NoMercy.Data.Plugins;

/// <summary>
/// A plugin's filter text after <see cref="PluginGraphTokens.Resolve" />: the
/// text to run, or the reason a token could not be replaced.
/// </summary>
/// <param name="Text">The text with every token replaced. The original text when refused.</param>
/// <param name="Refusal">Why a token could not be replaced, in words the plugin author can act on. Null when every token resolved.</param>
internal sealed record ResolvedGraphText(string Text, string? Refusal);
