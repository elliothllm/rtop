using System.Text.Json.Serialization;

namespace Rtop.Core;

/// <summary>One row of `ps`, kept so the ancestry can be walked without shelling out again.</summary>
public sealed record ProcessEntry(int Pid, int ParentPid, TimeSpan? Elapsed, string Command)
{
    /// <summary>The executable, i.e. the command line up to the first unescaped space.</summary>
    public string Executable => Command.Split(' ', 2)[0];
}

/// <summary>Where a process's console output ends up.</summary>
public enum OutputKind
{
    /// <summary>Not determined yet, or lsof could not say.</summary>
    Unknown,

    /// <summary>Redirected to a regular file somewhere up the ancestry — tailable.</summary>
    File,

    /// <summary>Attached to a terminal. The output belongs to that terminal and cannot be read.</summary>
    Terminal,
}

public sealed record OutputTarget(OutputKind Kind, string? Path, int? OwnerPid)
{
    public static OutputTarget Unknown { get; } = new(OutputKind.Unknown, null, null);
}

/// <summary>A running .NET application, as opposed to the `dotnet watch` machinery around it.</summary>
public sealed record DotnetProcess
{
    public required int Pid { get; init; }
    public required string Name { get; init; }
    public required string ExecutablePath { get; init; }
    public required string WorktreePath { get; init; }
    public required string Command { get; init; }
    public TimeSpan? Elapsed { get; init; }
    public IReadOnlyList<int> Ports { get; init; } = [];

    /// <summary>The `dotnet watch` / task runner that owns this process, if it has one.</summary>
    public int? RunnerPid { get; init; }

    public OutputTarget Output { get; init; } = OutputTarget.Unknown;

    [JsonIgnore]
    public int? HttpPort => Ports.Count == 0 ? null : Ports.Min();
}

/// <summary>A git worktree, and whatever is running out of it.</summary>
public sealed record Worktree
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string RepositoryPath { get; init; }
    public required string RepositoryName { get; init; }
    public string? Branch { get; init; }
    public bool IsMain { get; init; }

    /// <summary>Created under `.claude/worktrees/`, i.e. by Claude Code rather than by hand.</summary>
    public bool IsClaudeWorktree { get; init; }

    public IReadOnlyList<DotnetProcess> Processes { get; init; } = [];

    [JsonIgnore]
    public bool IsRunning => Processes.Count > 0;
}

public sealed record Snapshot
{
    public required DateTimeOffset TakenAt { get; init; }
    public required IReadOnlyList<Worktree> Worktrees { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [JsonIgnore]
    public int RunningProcessCount => Worktrees.Sum(worktree => worktree.Processes.Count);

    [JsonIgnore]
    public int RunningWorktreeCount => Worktrees.Count(worktree => worktree.IsRunning);
}
