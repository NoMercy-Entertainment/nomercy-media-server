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
using FluentAssertions;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginAbiTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("not-a-version", false)]
    public void IsCompatible_HandlesTheNonVersionInputsRegardlessOfWhereCurrentIs(
        string? targetAbi,
        bool expected
    )
    {
        Assert.Equal(expected, PluginAbi.IsCompatible(targetAbi));
    }

    /// <summary>
    /// Every case below is derived from <see cref="PluginAbi.Current"/> and
    /// <see cref="PluginAbi.Oldest"/> rather than a literal version, so a
    /// routine minor/major bump never requires touching this test — only an
    /// actual change to the compatibility rule itself would fail it.
    /// </summary>
    [Fact]
    public void IsCompatible_AcceptsNothingOlderThanTheOldestLoadableMajor()
    {
        Version current = PluginAbi.Current;
        Version oldest = PluginAbi.Oldest;

        // The oldest loadable major, and everything up to and including current, load.
        Assert.True(PluginAbi.IsCompatible($"{oldest.Major}.0"));
        Assert.True(PluginAbi.IsCompatible($"{current.Major}.{current.Minor}"));

        // A minor newer than current does not load yet.
        Assert.False(PluginAbi.IsCompatible($"{current.Major}.{current.Minor + 1}"));

        // A major newer than current, and anything older than the oldest
        // loadable major, do not load.
        Assert.False(PluginAbi.IsCompatible($"{current.Major + 1}.0"));
        if (oldest.Major > 0)
            Assert.False(PluginAbi.IsCompatible($"{oldest.Major - 1}.0"));
    }

    /// <summary>
    /// Eleven is refused rather than given the usual one-major grace. That
    /// grace exists because a major normally only removes members, so a plugin
    /// that never called them keeps working. Twelve renamed the SDK assembly
    /// and every namespace in it, so an eleven plugin resolves no type at all;
    /// accepting it would install something that fails at load with a
    /// missing-type error naming nothing its author recognises.
    /// </summary>
    [Fact]
    public void ThePreviousMajorIsRefusedBecauseItsTypesNoLongerExist()
    {
        PluginAbi.IsCompatible("11.0").Should().BeFalse();
        PluginAbi.Oldest.Major.Should().Be(PluginAbi.Current.Major);
    }

    [Fact]
    public void Current_IsNeverOlderThanOldest()
    {
        Assert.True(
            PluginAbi.Current.Major > PluginAbi.Oldest.Major
                || (
                    PluginAbi.Current.Major == PluginAbi.Oldest.Major
                    && PluginAbi.Current.Minor >= PluginAbi.Oldest.Minor
                )
        );
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
