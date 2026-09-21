using NoMercy.Plugin.Cli;

string verb = args.Length > 0 ? args[0] : "help";
string folder =
    args.Length > 1 && !args[1].StartsWith('-') ? args[1] : Directory.GetCurrentDirectory();
bool asJson = args.Contains("--json");

switch (verb)
{
    case "verify":
    {
        ScanReport report = await VerifyCommand.RunAsync(folder);
        VerifyCommand.Print(report, asJson, Console.Out);

        if (report.HasWarnings && report.ExitCode == 0)
            Console.Error.WriteLine(
                "Verified with warnings. Nothing here stops a release; a gate that fails on advice is a gate people switch off."
            );

        return report.ExitCode;
    }

    // These are the dotnet commands an author would type, kept here so one
    // tool covers the whole loop rather than half of it.
    case "build":
        return Passthrough("build", folder);
    case "test":
        return Passthrough("test", folder);
    case "pack":
        return Passthrough("publish", folder);
    case "new":
        return Passthrough("new nomercy-plugin", folder);

    default:
        Console.Out.WriteLine(
            """
            nomercy-plugin <verb> [folder] [--json]

              new       scaffold a plugin from the template
              build     build it
              test      run its tests
              pack      publish it to a folder ready to zip
              verify    run the scan the marketplace runs

            verify exits 1 when anything is blocked, and 0 with a banner when
            only warnings were found.
            """
        );
        return verb == "help" ? 0 : 1;
}

static int Passthrough(string dotnetVerb, string folder)
{
    System.Diagnostics.ProcessStartInfo start = new("dotnet")
    {
        WorkingDirectory = folder,
        UseShellExecute = false,
    };

    foreach (string part in dotnetVerb.Split(' '))
        start.ArgumentList.Add(part);

    using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(start);

    if (process is null)
        return 1;

    process.WaitForExit();
    return process.ExitCode;
}
