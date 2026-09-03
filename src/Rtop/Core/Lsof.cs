namespace Rtop.Core;

/// <summary>
/// The two things only lsof can tell us: which ports a process is listening on, and where its
/// standard output actually goes. Every call here is batched across pids — lsof is slow to start,
/// and this runs on a refresh loop.
/// </summary>
public static class Lsof
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Listening TCP ports per pid, for every process on the machine.</summary>
    public static async Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> ListeningPortsAsync(
        CancellationToken cancellationToken = default)
    {
        // -F pn asks for machine-readable output: one field per line, `p` pid then `n` name.
        var output = await Run(["-nP", "-iTCP", "-sTCP:LISTEN", "-F", "pn"], cancellationToken);
        var ports = new Dictionary<int, SortedSet<int>>();

        foreach (var (pid, field, value) in Fields(output))
        {
            if (field == 'n' && TryParsePort(value, out var port))
            {
                (ports.TryGetValue(pid, out var set) ? set : ports[pid] = []).Add(port);
            }
        }

        return ports.ToDictionary(pair => pair.Key, IReadOnlyList<int> (pair) => [.. pair.Value]);
    }

    /// <summary>
    /// Where each process's stdout points: a file path, a terminal, or unknown. Only descriptor 1
    /// is asked for, since stderr almost always follows it.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, OutputTarget>> StandardOutputsAsync(
        IReadOnlyCollection<int> pids,
        CancellationToken cancellationToken = default)
    {
        if (pids.Count == 0)
        {
            return new Dictionary<int, OutputTarget>();
        }

        // -F ftn gives the descriptor, its type, and its name.
        var output = await Run(["-a", "-d", "1", "-p", string.Join(',', pids), "-F", "ftn"], cancellationToken);
        var targets = new Dictionary<int, OutputTarget>();
        var type = new Dictionary<int, string>();

        foreach (var (pid, field, value) in Fields(output))
        {
            switch (field)
            {
                case 't':
                    type[pid] = value;
                    break;

                case 'n' when !targets.ContainsKey(pid):
                    targets[pid] = type.GetValueOrDefault(pid) switch
                    {
                        "REG" => new OutputTarget(OutputKind.File, value, pid),
                        "CHR" or "PTY" => new OutputTarget(OutputKind.Terminal, value, pid),
                        _ => OutputTarget.Unknown,
                    };
                    break;
            }
        }

        return targets;
    }

    /// <summary>Working directory per pid, used to tell which worktree owns a runner process.</summary>
    public static async Task<IReadOnlyDictionary<int, string>> WorkingDirectoriesAsync(
        IReadOnlyCollection<int> pids,
        CancellationToken cancellationToken = default)
    {
        if (pids.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        var output = await Run(["-a", "-d", "cwd", "-p", string.Join(',', pids), "-F", "n"], cancellationToken);
        var directories = new Dictionary<int, string>();

        foreach (var (pid, field, value) in Fields(output))
        {
            if (field == 'n')
            {
                directories.TryAdd(pid, value);
            }
        }

        return directories;
    }

    private static async Task<string> Run(string[] arguments, CancellationToken cancellationToken)
    {
        // lsof exits non-zero when some of the pids have gone, which is normal on a refresh loop,
        // so take whatever it printed rather than treating that as failure.
        try
        {
            var result = await Shell.RunAsync("/usr/sbin/lsof", arguments, "/", Timeout, cancellationToken);
            return result.StandardOutput;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return "";
        }
    }

    /// <summary>
    /// Walks lsof's -F output, which is a flat stream of one-letter fields where a `p` line starts
    /// a new process block and everything after belongs to it.
    /// </summary>
    private static IEnumerable<(int Pid, char Field, string Value)> Fields(string output)
    {
        var pid = 0;

        foreach (var line in output.Split('\n'))
        {
            if (line.Length < 2)
            {
                continue;
            }

            if (line[0] == 'p')
            {
                _ = int.TryParse(line[1..], out pid);
            }
            else if (pid != 0)
            {
                yield return (pid, line[0], line[1..]);
            }
        }
    }

    /// <summary>lsof writes listening sockets as `*:3000`, `127.0.0.1:3000` or `[::1]:3000`.</summary>
    private static bool TryParsePort(string name, out int port)
    {
        port = 0;
        var colon = name.LastIndexOf(':');
        return colon >= 0 && int.TryParse(name[(colon + 1)..], out port);
    }
}
