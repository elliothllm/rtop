using Rtop.Config;

namespace Rtop.Core;

/// <summary>
/// Builds a snapshot of what is running, from the process table outwards.
///
/// The order matters: .NET applications are found first, each is traced back to the worktree its
/// binary was built in, and only then is git asked what other worktrees those repositories have.
/// Nothing has to be registered or configured for a process to appear.
/// </summary>
public sealed class Discovery(RtopConfig config)
{
    public async Task<Snapshot> TakeAsync(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var table = await ProcessTable.CaptureAsync(cancellationToken);

        if (table.All.Count == 0)
        {
            warnings.Add("Could not read the process table (ps returned nothing).");
        }

        var portsTask = Lsof.ListeningPortsAsync(cancellationToken);

        var candidates = table.DotnetApplications()
            .Select(candidate => new
            {
                candidate.Entry,
                candidate.Name,
                candidate.ExecutablePath,
                WorktreePath = GitWorktrees.FindRoot(candidate.ExecutablePath),
            })
            .Where(candidate => candidate.WorktreePath is not null)
            .ToList();

        // Standard output is resolved by walking each application's ancestry, so every ancestor
        // needs to be looked at too; batching them into one lsof call keeps that to a single spawn.
        var ancestry = candidates.ToDictionary(
            candidate => candidate.Entry.Pid,
            candidate => table.Ancestry(candidate.Entry.Pid).ToList());

        var ancestorPids = ancestry.Values.SelectMany(chain => chain.Select(entry => entry.Pid)).Distinct().ToList();
        var outputs = await Lsof.StandardOutputsAsync(ancestorPids, cancellationToken);
        var ports = await portsTask;

        var processes = candidates
            .Select(candidate => new DotnetProcess
            {
                Pid = candidate.Entry.Pid,
                Name = candidate.Name,
                ExecutablePath = candidate.ExecutablePath,
                WorktreePath = candidate.WorktreePath!,
                Command = candidate.Entry.Command,
                Elapsed = candidate.Entry.Elapsed,
                Ports = ports.GetValueOrDefault(candidate.Entry.Pid, []),
                RunnerPid = FindRunner(ancestry[candidate.Entry.Pid]),
                Output = ResolveOutput(ancestry[candidate.Entry.Pid], outputs),
            })
            .OrderBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var worktrees = await BuildWorktreesAsync(processes, warnings, cancellationToken);

        return new Snapshot
        {
            TakenAt = DateTimeOffset.Now,
            Worktrees = worktrees,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// The first ancestor whose stdout is a real file is where this process's console output ends
    /// up: a supervisor that redirected its child's output owns the file, and the child only sees
    /// the pipe. If nothing in the chain has one, the output belongs to a terminal and is gone.
    /// </summary>
    private static OutputTarget ResolveOutput(
        List<ProcessEntry> ancestry,
        IReadOnlyDictionary<int, OutputTarget> outputs)
    {
        OutputTarget? terminal = null;

        foreach (var entry in ancestry)
        {
            var target = outputs.GetValueOrDefault(entry.Pid, OutputTarget.Unknown);

            if (target.Kind == OutputKind.File)
            {
                return target;
            }

            terminal ??= target.Kind == OutputKind.Terminal ? target : null;
        }

        return terminal ?? OutputTarget.Unknown;
    }

    /// <summary>
    /// The watcher or task runner that owns this process — what you would have to signal to stop
    /// it for good, since killing the app alone just makes `dotnet watch` start it again.
    /// The walk stops at anything that is not a recognised runner, so it can never climb out into
    /// the shell or the terminal emulator that started the whole thing.
    /// </summary>
    private static int? FindRunner(List<ProcessEntry> ancestry)
    {
        int? runner = null;

        // ancestry[0] is the application itself; its ancestors follow.
        foreach (var entry in ancestry.Skip(1))
        {
            if (!IsRunner(entry.Command))
            {
                break;
            }

            runner = entry.Pid;
        }

        return runner;
    }

    private static readonly string[] RunnerCommands =
        ["dotnet", "node", "npm", "npx", "pnpm", "yarn", "task", "make", "just", "tilt", "foreman", "overmind"];

    private static bool IsRunner(string command)
    {
        var executable = command.Split(' ', 2)[0];
        var name = executable[(executable.LastIndexOf('/') + 1)..];
        return RunnerCommands.Contains(name, StringComparer.Ordinal);
    }

    private async Task<IReadOnlyList<Worktree>> BuildWorktreesAsync(
        IReadOnlyList<DotnetProcess> processes,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var byWorktree = processes
            .GroupBy(process => process.WorktreePath)
            .ToDictionary(group => group.Key, IReadOnlyList<DotnetProcess> (group) => [.. group]);

        // Repositories worth listing in full: those with something running, the one the command was
        // run from, and anything named in the config. Their idle worktrees are then shown too.
        var repositories = new HashSet<string>(StringComparer.Ordinal);

        foreach (var worktreePath in byWorktree.Keys)
        {
            var main = await GitWorktrees.MainWorktreeAsync(worktreePath, cancellationToken);
            if (main is not null)
            {
                repositories.Add(main);
            }
        }

        if (GitWorktrees.FindRoot(Directory.GetCurrentDirectory()) is { } current)
        {
            var main = await GitWorktrees.MainWorktreeAsync(current, cancellationToken);
            if (main is not null)
            {
                repositories.Add(main);
            }
        }

        foreach (var configured in config.Repositories)
        {
            var expanded = RtopConfig.Expand(configured);
            if (Directory.Exists(expanded))
            {
                var main = await GitWorktrees.MainWorktreeAsync(expanded, cancellationToken);
                if (main is not null)
                {
                    repositories.Add(main);
                }
            }
            else
            {
                warnings.Add($"Configured repository not found: {configured}");
            }
        }

        var worktrees = new List<Worktree>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var repository in repositories.OrderBy(path => path, StringComparer.Ordinal))
        {
            var repositoryName = Basename(repository);

            foreach (var entry in await GitWorktrees.ListAsync(repository, cancellationToken))
            {
                if (!claimed.Add(entry.Path))
                {
                    continue;
                }

                worktrees.Add(new Worktree
                {
                    Path = entry.Path,
                    Name = entry.IsMain ? repositoryName : Basename(entry.Path),
                    RepositoryPath = repository,
                    RepositoryName = repositoryName,
                    Branch = entry.Branch,
                    IsMain = entry.IsMain,
                    IsClaudeWorktree = entry.Path.Contains("/.claude/worktrees/", StringComparison.Ordinal),
                    Processes = byWorktree.GetValueOrDefault(entry.Path, []),
                });
            }
        }

        // A process can be running out of a directory git no longer lists — a worktree removed
        // while its server was still up. Keep it rather than silently dropping the process.
        foreach (var (path, running) in byWorktree.Where(pair => !claimed.Contains(pair.Key)))
        {
            worktrees.Add(new Worktree
            {
                Path = path,
                Name = Basename(path),
                RepositoryPath = path,
                RepositoryName = Basename(path),
                Branch = null,
                IsMain = false,
                IsClaudeWorktree = path.Contains("/.claude/worktrees/", StringComparison.Ordinal),
                Processes = running,
            });
        }

        return
        [
            .. worktrees
                .OrderByDescending(worktree => worktree.IsRunning)
                .ThenBy(worktree => worktree.RepositoryName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(worktree => worktree.IsMain)
                .ThenBy(worktree => worktree.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static string Basename(string path) =>
        path.TrimEnd('/').Split('/').LastOrDefault() ?? path;
}
