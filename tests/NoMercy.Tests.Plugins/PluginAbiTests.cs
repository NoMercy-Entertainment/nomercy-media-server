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
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginAbiTests
{
    [Theory]
    [InlineData([null, true])]
    [InlineData(["", true])]
    [InlineData(["10.0", true])]
    [InlineData(["10.2", true])]
    [InlineData(["10.9", true])]
    [InlineData(["11.0", true])]
    [InlineData(["11.1", false])]
    [InlineData(["9.5", false])]
    [InlineData(["12.0", false])]
    [InlineData(["not-a-version", false])]
    public void IsCompatible_AcceptsThisMajorAndTheWholePrevious(string? targetAbi, bool expected)
    {
        Assert.Equal(expected, PluginAbi.IsCompatible(targetAbi));
    }

    [Fact]
    public void Current_IsElevenZero()
    {
        Assert.Equal(new Version(11, 0), PluginAbi.Current);
    }

    /// <summary>
    /// A plugin pins the major, so a member that existed in 11.0 is still
    /// called by binaries nobody is going to rebuild. This one went away and
    /// took both installed plugins off every screen in the ecosystem, and the
    /// only word anywhere was a MissingMethodException in the server log.
    /// </summary>
    [Theory]
    [InlineData(typeof(PluginScheduledJob), new[] { typeof(string), typeof(string), typeof(bool) })]
    public void ConstructorsElevenZeroPluginsCall_AreStillThere(Type type, Type[] parameters)
    {
        ConstructorInfo? found = type.GetConstructor(parameters);

        Assert.True(
            found is not null,
            $"{type.Name} no longer has .ctor({string.Join(", ", parameters.Select(one => one.Name))}). "
                + "Every plugin built against 11.0 fails to load with MissingMethodException."
        );
    }

    /// <summary>
    /// A property that keeps its name and changes its type is a break with no
    /// compile error anywhere: the plugin's IL still asks for
    /// <c>get_UserId</c> returning String, and the load fails at the first call.
    /// </summary>
    [Theory]
    [InlineData(typeof(PluginViewRequest), "UserId", typeof(string))]
    public void PropertiesElevenZeroPluginsRead_KeepTheirType(
        Type type,
        string property,
        Type expected
    )
    {
        PropertyInfo? found = type.GetProperty(property);

        Assert.True(found is not null, $"{type.Name}.{property} is gone.");
        Assert.True(
            found!.PropertyType == expected,
            $"{type.Name}.{property} returns {found.PropertyType.Name}, and every plugin built "
                + $"against 11.0 reads it as {expected.Name}. The call fails with MissingMethodException."
        );
    }

    [Fact]
    public void ScheduledJob_BuiltTheWayElevenZeroBuiltIt_CarriesItsValues()
    {
        PluginScheduledJob job = new("refresh", "0 */6 * * *", true);

        Assert.Equal("refresh", job.Name);
        Assert.Equal("0 */6 * * *", job.CronExpression);
        Assert.True(job.AllowConcurrent);
    }
}
