using Rtop.Config;
using Rtop.Core;

namespace Rtop.Logs;

public enum LevelFloor
{
    Everything,
    Warnings,
    Errors,
}

public readonly record struct LogFilter(bool ApplicationOnly, LevelFloor Floor)
{
    public static LogFilter Default => new(ApplicationOnly: false, Floor: LevelFloor.Everything);
}

public interface ILogSource
{
    /// <summary>Shown in the log pane's header: where these lines are coming from.</summary>
    string Description { get; }

    /// <summary>Whether the filter keys do anything for this source.</summary>
    bool SupportsFiltering { get; }

    Task<IReadOnlyList<string>> ReadAsync(int lines, LogFilter filter, CancellationToken cancellationToken);
}

/// <summary>A process whose output was redirected to a file somewhere up its ancestry.</summary>
public sealed class FileLogSource(string path) : ILogSource
{
    public string Description => path;

    public bool SupportsFiltering => false;

    public Task<IReadOnlyList<string>> ReadAsync(int lines, LogFilter filter, CancellationToken cancellationToken) =>
        Task.FromResult(LogTail.Read(path, lines));
}

/// <summary>
/// A process whose output went to a terminal, with no log server configured. There is nothing to
/// read: the bytes were written to somebody's tty and were never stored anywhere.
/// </summary>
public sealed class UnreadableLogSource(OutputTarget target, bool seqConfigured) : ILogSource
{
    public string Description => target.Kind == OutputKind.Terminal
        ? $"output goes to {target.Path}"
        : "output destination unknown";

    public bool SupportsFiltering => false;

    public Task<IReadOnlyList<string>> ReadAsync(int lines, LogFilter filter, CancellationToken cancellationToken)
    {
        List<string> explanation = target.Kind == OutputKind.Terminal
            ?
            [
                $"This process writes to the terminal {target.Path}"
                + (target.OwnerPid is { } pid ? $" (via pid {pid})." : "."),
                "",
                "Its output belongs to that terminal, so there is no file to tail.",
            ]
            :
            [
                "Could not work out where this process writes its output.",
                "",
                "Nothing in its ancestry has stdout pointing at a file.",
            ];

        if (!seqConfigured)
        {
            explanation.AddRange(
            [
                "",
                "If your apps ship logs to Seq, add a `seq` block to the config and rtop will read",
                $"them from there instead: {RtopConfig.Path}",
            ]);
        }

        return Task.FromResult<IReadOnlyList<string>>(explanation);
    }
}

/// <summary>
/// Logs read back out of Seq, for a process whose console output is unreachable.
///
/// Events are matched on process id rather than on an application name, so that two worktrees
/// running the same service do not show each other's lines. A watcher restart gives the process a
/// new pid, so the pids seen for a given executable are accumulated over the session — otherwise
/// the pane would blank every time the code changed.
/// </summary>
public sealed class SeqLogSource(SeqClient client, Func<IReadOnlyCollection<int>> pids) : ILogSource
{
    public string Description => $"{client.Url}  ({Describe(pids())})";

    public bool SupportsFiltering => true;

    public async Task<IReadOnlyList<string>> ReadAsync(int lines, LogFilter filter, CancellationToken cancellationToken)
    {
        var processIds = pids();
        if (processIds.Count == 0)
        {
            return ["No process ids to query Seq with."];
        }

        try
        {
            var events = await client.QueryAsync(BuildFilter(processIds, filter), lines, cancellationToken);

            if (events.Count > 0)
            {
                return [.. events.AsEnumerable().Reverse().SelectMany(entry => entry.Render())];
            }

            return filter == LogFilter.Default
                ? ["Seq has no events for this process."]
                : ["Nothing matches the current filter. Press f / l to widen it."];
        }
        catch (SeqException exception)
        {
            return [exception.Message];
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return [$"Could not reach Seq at {client.Url}: {exception.Message}"];
        }
    }

    /// <summary>
    /// Seq's filter language. ProcessId is not a property Seq adds by itself — it comes from the
    /// application's own enrichment — so this only finds anything for apps that log it.
    /// </summary>
    private static string BuildFilter(IReadOnlyCollection<int> pids, LogFilter filter)
    {
        var clauses = new List<string>
        {
            $"({string.Join(" or ", pids.Select(pid => $"ProcessId = {pid}"))})",
        };

        // Framework chatter, chiefly EF Core logging every statement it runs, buries an
        // application's own lines several times over.
        if (filter.ApplicationOnly)
        {
            clauses.Add("SourceContext not like 'Microsoft.%'");
        }

        var floor = filter.Floor switch
        {
            LevelFloor.Warnings => "@Level in ['Warning', 'Error', 'Fatal']",
            LevelFloor.Errors => "@Level in ['Error', 'Fatal']",
            _ => null,
        };

        if (floor is not null)
        {
            clauses.Add(floor);
        }

        return string.Join(" and ", clauses);
    }

    private static string Describe(IReadOnlyCollection<int> pids) => pids.Count switch
    {
        0 => "no pids",
        1 => $"pid {pids.First()}",
        _ => $"pids {string.Join(", ", pids)}",
    };
}
