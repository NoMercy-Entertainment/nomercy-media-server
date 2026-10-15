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

using System.Drawing;
using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Logging;
using NoMercy.NmSystem.SystemCalls;

namespace NoMercy.PluginSdk.Logging;

/// <summary>
/// Registers each plugin in both log registries and hands out its logger.
/// <para>
/// Both registries, because the server reads two: <see cref="LogCategories" />
/// names and colours the console line and the run file, and
/// <see cref="Logger.LogTypes" /> is the list the dashboard filters on and
/// looks names up in. A plugin in only one of them shows up nameless in the
/// other.
/// </para>
/// </summary>
public sealed class PluginLoggers(ILoggerFactory loggers) : IPluginLoggers
{
    public void Register(Ulid pluginId, string displayName)
    {
        LogCategory category = LogCategories.RegisterPlugin(pluginId, displayName);

        Logger.AddLogType(
            new(
                category.Key,
                category.DisplayName,
                ColorTranslator.FromHtml(category.DarkHex),
                category.Group
            )
        );
    }

    public ILogger For(Ulid pluginId) => loggers.CreateLogger(LogCategories.PluginKey(pluginId));
}
