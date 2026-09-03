using System.Text.Json;
using System.Text.Json.Serialization;
using Rtop.Core;

namespace Rtop;

/// <summary>
/// The `--json` shape. Written by hand rather than serialising the models directly so the output
/// is a stable contract: the internal types can be refactored without breaking anyone's script.
/// </summary>
public static class JsonOutput
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Render(Snapshot snapshot) => JsonSerializer.Serialize(
        new
        {
            takenAt = snapshot.TakenAt,
            runningProcesses = snapshot.RunningProcessCount,
            runningWorktrees = snapshot.RunningWorktreeCount,
            warnings = snapshot.Warnings,
            worktrees = snapshot.Worktrees.Select(worktree => new
            {
                name = worktree.Name,
                path = worktree.Path,
                branch = worktree.Branch,
                repository = new
                {
                    name = worktree.RepositoryName,
                    path = worktree.RepositoryPath,
                },
                isMain = worktree.IsMain,
                isClaudeWorktree = worktree.IsClaudeWorktree,
                running = worktree.IsRunning,
                processes = worktree.Processes.Select(process => new
                {
                    pid = process.Pid,
                    name = process.Name,
                    executablePath = process.ExecutablePath,
                    command = process.Command,
                    uptimeSeconds = process.Elapsed is { } elapsed ? (long?)elapsed.TotalSeconds : null,
                    ports = process.Ports,
                    runnerPid = process.RunnerPid,
                    log = new
                    {
                        kind = process.Output.Kind.ToString().ToLowerInvariant(),
                        path = process.Output.Path,
                        ownerPid = process.Output.OwnerPid,
                    },
                }),
            }),
        },
        Options);
}
