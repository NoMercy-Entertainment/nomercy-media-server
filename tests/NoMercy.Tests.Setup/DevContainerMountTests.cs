using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NoMercy.Tests.Setup;

public class DevContainerMountTests
{
    private const string ConfigTarget = "/home/vscode/.local/share/NoMercy_dev/config";

    [Fact]
    public void ConfigMountUsesHomeWhenLocalAppDataIsUnavailable()
    {
        using JsonDocument config = ReadConfig();
        string mount = ConfigMount(config);
        string source = Expand(Source(mount), new Dictionary<string, string>
        {
            ["HOME"] = "/home/contributor",
            ["LOCALAPPDATA"] = ""
        });

        Assert.Equal("/home/contributor/NoMercy_dev/config", source);
    }

    [Fact]
    public void ConfigMountPreservesWindowsLocalAppData()
    {
        using JsonDocument config = ReadConfig();
        string mount = ConfigMount(config);
        string source = Expand(Source(mount), new Dictionary<string, string>
        {
            ["HOME"] = "",
            ["LOCALAPPDATA"] = "C:/Users/Contributor/AppData/Local"
        });

        Assert.Equal("C:/Users/Contributor/AppData/Local/NoMercy_dev/config", source);
    }

    [Fact]
    public void InitializeCommandCreatesConfigSourceBeforeContainerCreation()
    {
        using JsonDocument config = ReadConfig();
        string command = config.RootElement.GetProperty("initializeCommand").GetString()!;
        string mount = ConfigMount(config);
        string temporaryHome = Path.Combine(Path.GetTempPath(), "NoMercy devcontainer test " + Guid.NewGuid());
        Directory.CreateDirectory(temporaryHome);

        try
        {
            Dictionary<string, string> environment = new()
            {
                ["HOME"] = OperatingSystem.IsWindows() ? "" : temporaryHome,
                ["LOCALAPPDATA"] = OperatingSystem.IsWindows() ? temporaryHome : ""
            };
            string source = Expand(Source(mount), environment);
            string expandedCommand = Expand(command, environment);
            string script = Path.Combine(temporaryHome, OperatingSystem.IsWindows() ? "initialize.cmd" : "initialize.sh");
            File.WriteAllText(script, expandedCommand + Environment.NewLine);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                using Process process = new();
                process.StartInfo.FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
                if (OperatingSystem.IsWindows())
                {
                    process.StartInfo.ArgumentList.Add("/d");
                    process.StartInfo.ArgumentList.Add("/c");
                }

                process.StartInfo.ArgumentList.Add(script);
                process.StartInfo.RedirectStandardError = true;
                process.Start();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                Assert.True(process.ExitCode == 0, $"Command: {expandedCommand}{Environment.NewLine}{error}");
                Assert.True(Directory.Exists(source), $"Missing bind source: {source}");
            }
        }
        finally
        {
            Directory.Delete(temporaryHome, recursive: true);
        }
    }

    private static JsonDocument ReadConfig()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName, ".devcontainer", "devcontainer.json");
            if (File.Exists(path))
            {
                return JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip
                });
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find .devcontainer/devcontainer.json");
    }

    private static string ConfigMount(JsonDocument config) => config.RootElement
        .GetProperty("mounts")
        .EnumerateArray()
        .Select(mount => mount.GetString()!)
        .Single(mount => mount.Contains($"target={ConfigTarget}", StringComparison.Ordinal));

    private static string Source(string mount) => mount.Split(',')
        .Single(part => part.StartsWith("source=", StringComparison.Ordinal))["source=".Length..];

    private static string Expand(string value, Dictionary<string, string> environment) => Regex.Replace(
        value,
        @"\$\{localEnv:([A-Z_]+)\}",
        match => environment[match.Groups[1].Value],
        RegexOptions.None,
        TimeSpan.FromMilliseconds(1000));
}
