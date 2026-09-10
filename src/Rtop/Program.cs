using Rtop;
using Rtop.Config;
using Rtop.Core;
using Rtop.Tui;

var options = CommandLine.Parse(args);

if (options.Error is not null)
{
    Console.Error.WriteLine($"rtop: {options.Error}");
    Console.Error.WriteLine("Try `rtop --help`.");
    return 2;
}

if (options.ShowHelp)
{
    Console.WriteLine(CommandLine.Help);
    return 0;
}

if (options.ShowVersion)
{
    Console.WriteLine(typeof(CommandLine).Assembly.GetName().Version?.ToString(3) ?? "unknown");
    return 0;
}

var config = RtopConfig.Load();

if (options.Refresh is { } refresh)
{
    config.RefreshSeconds = refresh;
}

if (options.WriteConfig)
{
    config.Save();
    Console.WriteLine(RtopConfig.Path);
    return 0;
}

using var interrupt = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    interrupt.Cancel();
};

if (options.Json)
{
    var snapshot = await new Discovery(config).TakeAsync(interrupt.Token);
    Console.WriteLine(JsonOutput.Render(snapshot));
    return 0;
}

if (Console.IsOutputRedirected)
{
    Console.Error.WriteLine("rtop: stdout is not a terminal, so there is no screen to draw on.");
    Console.Error.WriteLine("Use `rtop --json` to get the same data as JSON.");
    return 2;
}

using var dashboard = new Dashboard(config, showLog: !options.NoLog);
await dashboard.RunAsync(interrupt.Token);
return 0;

namespace Rtop
{
    internal sealed record CommandLineOptions
    {
        public bool Json { get; init; }
        public bool ShowHelp { get; init; }
        public bool ShowVersion { get; init; }
        public bool WriteConfig { get; init; }
        public double? Refresh { get; init; }
        public bool NoLog { get; init; }
        public string? Error { get; init; }
    }

    internal static class CommandLine
    {
        public const string Help = """
            rtop - what .NET is running, and out of which worktree.

            Scans the process table for .NET applications, traces each back to the git worktree its
            binary was built in, and shows the ports it is listening on and the log it is writing.
            Nothing needs registering: if it is running, it shows up.

            USAGE
              rtop                 interactive dashboard
              rtop --json          print one snapshot as JSON and exit
              rtop --write-config  write the default config file and print its path

            OPTIONS
              --json             machine-readable output instead of the dashboard
              --refresh <secs>   how often to rescan, 0.5 to 60 (default 3, or the config)
              --no-log           list only: drop the log pane, and stop reading logs at all
              --write-config     create ~/.config/rtop/config.json
              -h, --help         this text
              -v, --version      version number

            KEYS (interactive)
              up / down, k / j   move the selection
              PgUp / PgDn        scroll the log; Home and End jump to either end
              o                  open the selected process's port in a browser
              s                  SIGTERM the runner behind the selected process (asks first)
              a                  show every worktree, or only the ones running something
              t                  switch the log between its file and Seq
              f                  hide Microsoft.* framework logging (Seq only)
              l                  cycle the level floor: all, warnings, errors (Seq only)
              r                  refresh now
              q                  quit

            CONFIG
              ~/.config/rtop/config.json is optional. It can name extra repositories to list even
              when nothing is running in them, change the refresh interval, and point rtop at a Seq
              server for processes whose output goes to a terminal and so cannot be tailed.
            """;

        public static CommandLineOptions Parse(string[] args)
        {
            var options = new CommandLineOptions();

            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];

                if (argument == "--refresh")
                {
                    // Out-of-range values are clamped rather than refused, exactly as the same
                    // setting is when it comes from the config file.
                    if (index + 1 >= args.Length || !double.TryParse(args[++index], out var seconds))
                    {
                        return options with { Error = "--refresh needs a number of seconds" };
                    }

                    options = options with { Refresh = Math.Clamp(seconds, 0.5, 60) };
                    continue;
                }

                options = argument switch
                {
                    "--json" => options with { Json = true },
                    "--no-log" => options with { NoLog = true },
                    "--write-config" => options with { WriteConfig = true },
                    "-h" or "--help" => options with { ShowHelp = true },
                    "-v" or "--version" => options with { ShowVersion = true },
                    _ => options with { Error = $"unknown option '{argument}'" },
                };
            }

            return options;
        }
    }
}
