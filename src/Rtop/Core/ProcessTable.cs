using System.Text.RegularExpressions;

namespace Rtop.Core;

/// <summary>
/// One `ps` snapshot, with the ancestry indexed so it can be walked without shelling out again.
/// </summary>
public sealed partial class ProcessTable
{
    private readonly Dictionary<int, ProcessEntry> _byPid;

    private ProcessTable(IEnumerable<ProcessEntry> entries)
    {
        _byPid = entries.ToDictionary(entry => entry.Pid);
    }

    public IReadOnlyCollection<ProcessEntry> All => _byPid.Values;

    [GeneratedRegex(@"^\s*(?<pid>\d+)\s+(?<ppid>\d+)\s+(?<elapsed>[\d\-:]+)\s+(?<command>\S.*)$")]
    private static partial Regex Line { get; }

    /// <summary>
    /// An apphost built into bin/, e.g. .../bin/Debug/net10.0/My.App. The name is not restricted to
    /// dot-free text: assembly names routinely contain dots, so managed files are excluded by
    /// extension afterwards instead.
    /// </summary>
    [GeneratedRegex(@"^(?<project>.+)/bin/(?<configuration>Debug|Release)/(?<tfm>[^/]+)/(?<name>[^/]+)$")]
    private static partial Regex AppHost { get; }

    private static readonly string[] NotAnAppHost = [".dll", ".exe", ".pdb", ".json", ".xml"];

    /// <summary>A framework-dependent launch: `dotnet .../bin/Debug/net10.0/MyApp.dll`.</summary>
    [GeneratedRegex(@"(?<path>/\S+/bin/(?:Debug|Release)/[^/\s]+/(?<name>[^/\s]+)\.dll)")]
    private static partial Regex AppDll { get; }

    public static async Task<ProcessTable> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var output = await Shell.TryRunAsync(
            "/bin/ps", ["-axww", "-o", "pid=,ppid=,etime=,command="], "/", TimeSpan.FromSeconds(15), cancellationToken);

        if (output is null)
        {
            return new ProcessTable([]);
        }

        return new ProcessTable(output.Split('\n').Select(Parse).OfType<ProcessEntry>());
    }

    public ProcessEntry? Get(int pid) => _byPid.GetValueOrDefault(pid);

    /// <summary>The process and then each of its ancestors, stopping at init.</summary>
    public IEnumerable<ProcessEntry> Ancestry(int pid)
    {
        var seen = new HashSet<int>();
        var current = Get(pid);

        while (current is not null && seen.Add(current.Pid) && current.Pid != 1)
        {
            yield return current;
            current = current.ParentPid <= 1 ? null : Get(current.ParentPid);
        }
    }

    /// <summary>
    /// The .NET applications running on this machine, ignoring the build and watch machinery
    /// (`dotnet watch`, `dotnet run`, MSBuild nodes, the compiler server) that surrounds them.
    /// Those are not what you want to look at; they are how the thing you want to look at is run.
    /// </summary>
    public IEnumerable<(ProcessEntry Entry, string Name, string ExecutablePath)> DotnetApplications()
    {
        foreach (var entry in _byPid.Values)
        {
            var apphost = AppHost.Match(entry.Executable);
            if (apphost.Success && !IsToolchain(entry.Executable) && IsAppHost(apphost.Groups["name"].Value))
            {
                yield return (entry, apphost.Groups["name"].Value, entry.Executable);
                continue;
            }

            // Only inspect arguments for a dll when the executable really is the dotnet host,
            // so that a `dotnet watch` command line mentioning a project does not count.
            if (!IsDotnetHost(entry.Executable) || IsToolchain(entry.Command))
            {
                continue;
            }

            var dll = AppDll.Match(entry.Command);
            if (dll.Success)
            {
                yield return (entry, dll.Groups["name"].Value, dll.Groups["path"].Value);
            }
        }
    }

    private static bool IsAppHost(string name) =>
        !NotAnAppHost.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static bool IsDotnetHost(string executable) =>
        executable.EndsWith("/dotnet", StringComparison.Ordinal) || executable == "dotnet";

    private static bool IsToolchain(string text) =>
        text.Contains("dotnet-watch.dll", StringComparison.Ordinal)
        || text.Contains("MSBuild.dll", StringComparison.Ordinal)
        || text.Contains("VBCSCompiler.dll", StringComparison.Ordinal)
        || text.Contains("/sdk/", StringComparison.Ordinal)
        || Regex.IsMatch(text, @"\bdotnet\s+(watch|run|build|test|restore|msbuild|pack|publish)\b");

    private static ProcessEntry? Parse(string line)
    {
        var match = Line.Match(line);
        if (!match.Success)
        {
            return null;
        }

        return new ProcessEntry(
            int.Parse(match.Groups["pid"].Value),
            int.Parse(match.Groups["ppid"].Value),
            ParseElapsed(match.Groups["elapsed"].Value),
            match.Groups["command"].Value.TrimEnd());
    }

    /// <summary>Turns ps's [[dd-]hh:]mm:ss elapsed column into a TimeSpan.</summary>
    internal static TimeSpan? ParseElapsed(string elapsed)
    {
        var days = 0;
        var value = elapsed;

        var dash = value.IndexOf('-');
        if (dash > 0)
        {
            if (!int.TryParse(value[..dash], out days))
            {
                return null;
            }

            value = value[(dash + 1)..];
        }

        var parts = value.Split(':');
        if (parts.Length is < 2 or > 3 || parts.Any(part => !int.TryParse(part, out _)))
        {
            return null;
        }

        var numbers = parts.Select(int.Parse).ToArray();
        var (hours, minutes, seconds) = numbers.Length == 3
            ? (numbers[0], numbers[1], numbers[2])
            : (0, numbers[0], numbers[1]);

        return new TimeSpan(days, hours, minutes, seconds);
    }
}
