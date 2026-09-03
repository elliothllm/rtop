namespace Rtop.Core;

public sealed record GitWorktree(string Path, string? Branch, bool IsMain);

/// <summary>Reads worktrees straight from git, so nothing here needs to know how they were made.</summary>
public static class GitWorktrees
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>The worktree root containing this path, or null if it is not inside a repository.</summary>
    public static string? FindRoot(string startingPath)
    {
        var directory = Directory.Exists(startingPath)
            ? new DirectoryInfo(startingPath)
            : new FileInfo(startingPath).Directory;

        while (directory is not null)
        {
            // A linked worktree has a .git file pointing at the common directory, not a .git dir.
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public static async Task<IReadOnlyList<GitWorktree>> ListAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var output = await Shell.TryRunAsync(
            "/usr/bin/git", ["worktree", "list", "--porcelain"], repositoryPath, Timeout, cancellationToken);

        if (output is null)
        {
            return [];
        }

        var worktrees = new List<GitWorktree>();
        string? path = null;
        string? branch = null;

        // Records are separated by blank lines: `worktree <path>`, then HEAD, branch, and flags.
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = line["worktree ".Length..].TrimEnd();
                branch = null;
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                branch = ShortBranch(line["branch ".Length..].TrimEnd());
            }
            else if (line.StartsWith("detached", StringComparison.Ordinal))
            {
                branch = "detached";
            }
            else if (line.Length == 0 && path is not null)
            {
                worktrees.Add(new GitWorktree(path, branch, worktrees.Count == 0));
                path = null;
            }
        }

        if (path is not null)
        {
            worktrees.Add(new GitWorktree(path, branch, worktrees.Count == 0));
        }

        return worktrees;
    }

    /// <summary>The main worktree, which is the first entry git prints.</summary>
    public static async Task<string?> MainWorktreeAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        (await ListAsync(repositoryPath, cancellationToken)).FirstOrDefault()?.Path;

    private static string ShortBranch(string reference) =>
        reference.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? reference["refs/heads/".Length..]
            : reference;
}
