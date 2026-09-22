using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Api.NmComponents;

public class PluginTranslationValidatorTests
{
    private static Func<string, string?> Files(Dictionary<string, string> files)
    {
        return locale => files.TryGetValue(locale, out string? text) ? text : null;
    }

    private static PluginTranslations Declared(params string[] locales)
    {
        return new() { Source = "en", Locales = [.. locales] };
    }

    [Fact]
    public void PassesWhenEveryKeyIsTranslated()
    {
        List<PluginTranslationProblem> problems = PluginTranslationValidator.Validate(
            Declared("en", "nl"),
            Files(
                new()
                {
                    ["en"] = """{"title":"Library","empty":"Nothing here"}""",
                    ["nl"] = """{"title":"Bibliotheek","empty":"Niets hier"}""",
                }
            )
        );

        Assert.Empty(problems);
    }

    [Fact]
    public void CatchesAKeyThatWasNeverTranslated()
    {
        // The failure nobody notices: the plugin loads, the page renders, and one
        // label sits in English for every Dutch viewer.
        List<PluginTranslationProblem> problems = PluginTranslationValidator.Validate(
            Declared("en", "nl"),
            Files(
                new()
                {
                    ["en"] = """{"title":"Library","empty":"Nothing here"}""",
                    ["nl"] = """{"title":"Bibliotheek"}""",
                }
            )
        );

        Assert.Contains(
            problems,
            problem => problem.Locale == "nl" && problem.Detail.Contains("empty")
        );
    }

    [Fact]
    public void CatchesAnEmptyStringRatherThanCountingItAsTranslated()
    {
        // An empty value passes a key check and renders a blank label, which
        // reads as a broken page rather than as an untranslated one.
        List<PluginTranslationProblem> problems = PluginTranslationValidator.Validate(
            Declared("en", "nl"),
            Files(new() { ["en"] = """{"title":"Library"}""", ["nl"] = """{"title":"   "}""" })
        );

        Assert.Contains(problems, problem => problem.Detail.Contains("empty"));
    }

    [Fact]
    public void CatchesAKeyNothingWillEverRead()
    {
        // Left behind by a rename. It translates fine and is never shown.
        List<PluginTranslationProblem> problems = PluginTranslationValidator.Validate(
            Declared("en", "nl"),
            Files(
                new()
                {
                    ["en"] = """{"title":"Library"}""",
                    ["nl"] = """{"title":"Bibliotheek","heading":"Oud"}""",
                }
            )
        );

        Assert.Contains(problems, problem => problem.Detail.Contains("heading"));
    }

    [Fact]
    public void CatchesADeclaredLocaleThatShippedNoFile()
    {
        List<PluginTranslationProblem> problems = PluginTranslationValidator.Validate(
            Declared("en", "de"),
            Files(new() { ["en"] = """{"title":"Library"}""" })
        );

        Assert.Contains(problems, problem => problem.Locale == "de");
    }

    [Fact]
    public void SaysSoWhenTheSourceItselfIsMissing()
    {
        // Without the source there is nothing to measure against, and reporting
        // every other locale as complete would be worse than reporting nothing.
        List<PluginTranslationProblem> problems = PluginTranslationValidator.Validate(
            Declared("en", "nl"),
            Files(new() { ["nl"] = """{"title":"Bibliotheek"}""" })
        );

        Assert.Single(problems);
        Assert.Equal("en", problems[0].Locale);
    }
}

public class PluginSurfaceTests
{
    [Fact]
    public void SpeaksTheSameThreeNamesTheComponentsDo()
    {
        // A fourth vocabulary here would let a plugin target a surface that no
        // component could hide from.
        Assert.Equal(["web", "mobile", "tv"], PluginSurface.All);
    }

    [Fact]
    public void ServesTheFullestViewWhenTheCallerSaysNothing()
    {
        PluginViewRequest request = new()
        {
            Route = "/",
            Caller = new PluginCaller(
                UserId.Empty,
                "Stoney",
                PluginRole.Owner,
                PluginAccess.Owned,
                "en",
                PluginSurface.Web
            ),
        };

        Assert.Equal(PluginSurface.Web, request.Surface);
    }

    [Fact]
    public void RejectsASurfaceNothingServes()
    {
        Assert.True(PluginSurface.IsKnown("tv"));
        Assert.False(PluginSurface.IsKnown("watch"));
        Assert.False(PluginSurface.IsKnown(null));
    }
}
