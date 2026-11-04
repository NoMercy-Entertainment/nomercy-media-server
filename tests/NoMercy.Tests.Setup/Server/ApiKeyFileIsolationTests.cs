using System.Reflection;
using NoMercy.Tests.Setup.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Setup.Server;

[Trait("Category", "Unit")]
public class ApiKeyFileIsolationTests
{
    [Fact]
    public void EveryApiKeyLoaderTestClass_RunsInTheProcessWideSetupStateCollection()
    {
        Type[] loaderTests = typeof(ApiKeyFileIsolationTests)
            .Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(ApiKeyFileIsolationTests).Namespace)
            .Where(t => t.Name.StartsWith("ApiKeyLoader", StringComparison.Ordinal))
            .Where(t => t.GetMethods().Any(m => m.GetCustomAttributes<FactAttribute>().Any()))
            .ToArray();

        Assert.NotEmpty(loaderTests);

        string[] outside = loaderTests
            .Where(t =>
                !t.GetCustomAttributesData()
                    .Any(a =>
                        a.AttributeType == typeof(CollectionAttribute)
                        && a.ConstructorArguments.Count == 1
                        && Equals(a.ConstructorArguments[0].Value, ProcessWideSetupStateCollection.Name)
                    )
            )
            .Select(t => t.Name)
            .ToArray();

        Assert.True(outside.Length == 0, "Outside the collection: " + string.Join(", ", outside));
    }
}
