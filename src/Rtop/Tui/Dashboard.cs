using System.Diagnostics;
using Rtop.Config;
using Rtop.Core;
using Rtop.Logs;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Rtop.Tui;

/// <summary>
/// The interactive screen: a list of worktrees and the .NET processes running out of them, over a
/// log pane that follows whatever is selected.
/// </summary>
public sealed class Dashboard(RtopConfig config) : IDisposable
{
    private sealed record Row(Worktree Worktree, DotnetProcess? Process)
    {
        public bool IsProcess => Process is not null;
    }

    private readonly Discovery _discovery = new(config);
    private readonly SeqClient? _seq = config.Seq is null ? null : new SeqClient(config.Seq);

    /// <summary>
    /// Every pid seen for a given executable. A watcher restart changes the pid, and a Seq query
    /// pinned to only the current one would throw the log away on every code change.
    /// </summary>
    private readonly Dictionary<string, HashSet<int>> _pidHistory = [];

    private Snapshot _snapshot = new() { TakenAt = DateTimeOffset.Now, Worktrees = [] };
    private List<Row> _rows = [];
    private int _selected;
    private int _listOffset;
    private bool _runningOnly;
    private string? _status;

    private Task<Snapshot>? _refreshTask;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    private ILogSource? _logSource;
    private string _logKey = "";
    private IReadOnlyList<string> _logLines = [];
    private int _logScroll;
    private bool _preferSeq;
    private LogFilter _logFilter = LogFilter.Default;
    private Task<IReadOnlyList<string>>? _logTask;
    private DateTimeOffset _lastLogRead = DateTimeOffset.MinValue;

    private bool _dirty = true;
    private DateTimeOffset _lastDraw = DateTimeOffset.MinValue;
    private string? _confirmPrompt;
    private Action? _confirmAction;
    private bool _running = true;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        EnterAlternateScreen();

        try
        {
            await AnsiConsole.Live(new Text(""))
                .AutoClear(false)
                .Overflow(VerticalOverflow.Crop)
                .StartAsync(async context =>
                {
                    while (_running && !cancellationToken.IsCancellationRequested)
                    {
                        DrainKeys();
                        PumpRefresh(cancellationToken);
                        PumpLog(cancellationToken);

                        // Redrawing paints the whole screen, so do it only when something changed
                        // or a few times a second — not on every 40ms tick.
                        var frame = Frame();
                        var now = DateTimeOffset.Now;
                        if (_dirty || now - _lastDraw > TimeSpan.FromMilliseconds(400))
                        {
                            _dirty = false;
                            _lastDraw = now;
                            context.UpdateTarget(frame);
                            context.Refresh();
                        }

                        await Task.Delay(40, CancellationToken.None);
                    }
                });
        }
        finally
        {
            LeaveAlternateScreen();
        }
    }

    // --- Data ---------------------------------------------------------------

    private void PumpRefresh(CancellationToken cancellationToken)
    {
        if (_refreshTask is { IsCompleted: true } finished)
        {
            _refreshTask = null;

            if (finished is { IsCompletedSuccessfully: true, Result: var snapshot })
            {
                Apply(snapshot);
            }
            else if (finished.Exception?.GetBaseException() is { } error)
            {
                _status = $"refresh failed: {error.Message}";
            }
        }

        if (_refreshTask is null && DateTimeOffset.Now - _lastRefresh > TimeSpan.FromSeconds(config.RefreshSeconds))
        {
            _lastRefresh = DateTimeOffset.Now;
            _refreshTask = _discovery.TakeAsync(cancellationToken);
        }
    }

    private void Apply(Snapshot snapshot)
    {
        _snapshot = snapshot;

        foreach (var process in snapshot.Worktrees.SelectMany(worktree => worktree.Processes))
        {
            if (!_pidHistory.TryGetValue(process.ExecutablePath, out var pids))
            {
                _pidHistory[process.ExecutablePath] = pids = [];
            }

            pids.Add(process.Pid);
        }

        // Hold the selection on the same thing across refreshes rather than on an index that now
        // points at a different row.
        var previous = CurrentRow();
        var first = _rows.Count == 0;
        Rebuild();

        if (first)
        {
            // Open on a running process if there is one: that is what the log pane is for.
            var running = _rows.FindIndex(row => row.IsProcess);
            _selected = running >= 0 ? running : 0;
        }

        if (previous is not null)
        {
            var index = _rows.FindIndex(row =>
                row.Process?.ExecutablePath == previous.Process?.ExecutablePath
                && row.Worktree.Path == previous.Worktree.Path);

            if (index >= 0)
            {
                _selected = index;
            }
        }

        Clamp();
    }

    private void Rebuild()
    {
        _rows = [];

        foreach (var worktree in _snapshot.Worktrees)
        {
            if (_runningOnly && !worktree.IsRunning)
            {
                continue;
            }

            _rows.Add(new Row(worktree, null));

            foreach (var process in worktree.Processes)
            {
                _rows.Add(new Row(worktree, process));
            }
        }
    }

    private Row? CurrentRow() => _selected >= 0 && _selected < _rows.Count ? _rows[_selected] : null;

    private void Clamp()
    {
        _selected = _rows.Count == 0 ? 0 : Math.Clamp(_selected, 0, _rows.Count - 1);
    }

    // --- Logs ---------------------------------------------------------------

    private void PumpLog(CancellationToken cancellationToken)
    {
        var row = CurrentRow();
        var key = row?.Process is { } process
            ? $"{(_preferSeq ? "seq" : "auto")}:{process.ExecutablePath}"
            : $"worktree:{row?.Worktree.Path}";

        if (key != _logKey)
        {
            _logKey = key;
            _logSource = row?.Process is null ? null : BuildSource(row.Process);
            _logLines = [];
            _logScroll = 0;
            _lastLogRead = DateTimeOffset.MinValue;
            _logTask = null;
        }

        if (_logTask is { IsCompleted: true } finished)
        {
            _logTask = null;

            if (finished.IsCompletedSuccessfully)
            {
                _logLines = finished.Result;
            }
            else if (finished.Exception?.GetBaseException() is { } error)
            {
                _logLines = [$"Reading the log failed: {error.Message}"];
            }
        }

        if (_logSource is not null && _logTask is null
            && DateTimeOffset.Now - _lastLogRead > TimeSpan.FromSeconds(1))
        {
            _lastLogRead = DateTimeOffset.Now;
            _logTask = _logSource.ReadAsync(config.LogTailLines, _logFilter, cancellationToken);
        }
    }

    private ILogSource BuildSource(DotnetProcess process)
    {
        var pids = _pidHistory.GetValueOrDefault(process.ExecutablePath, [process.Pid]);

        if (_preferSeq && _seq is not null)
        {
            return new SeqLogSource(_seq, () => pids);
        }

        if (process.Output is { Kind: OutputKind.File, Path: { } path })
        {
            return new FileLogSource(path);
        }

        return _seq is not null
            ? new SeqLogSource(_seq, () => pids)
            : new UnreadableLogSource(process.Output, seqConfigured: false);
    }

    // --- Input --------------------------------------------------------------

    private void DrainKeys()
    {
        while (Console.KeyAvailable)
        {
            HandleKey(Console.ReadKey(intercept: true));
        }
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        _dirty = true;

        if (_confirmPrompt is not null)
        {
            if (key.Key is ConsoleKey.Y)
            {
                _confirmAction?.Invoke();
            }

            _confirmPrompt = null;
            _confirmAction = null;
            return;
        }

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                Move(-1);
                return;

            case ConsoleKey.DownArrow:
                Move(1);
                return;

            case ConsoleKey.PageUp:
                _logScroll += Math.Max(1, LogHeight() - 1);
                return;

            case ConsoleKey.PageDown:
                _logScroll = Math.Max(0, _logScroll - Math.Max(1, LogHeight() - 1));
                return;

            case ConsoleKey.Home:
                _logScroll = Math.Max(0, _logLines.Count - LogHeight());
                return;

            case ConsoleKey.End:
                _logScroll = 0;
                return;

            case ConsoleKey.Escape:
                _running = false;
                return;
        }

        switch (char.ToLowerInvariant(key.KeyChar))
        {
            case 'k':
                Move(-1);
                break;

            case 'j':
                Move(1);
                break;

            case 'q':
                _running = false;
                break;

            case 'a':
                _runningOnly = !_runningOnly;
                Rebuild();
                Clamp();
                _status = _runningOnly ? "showing running only" : "showing all worktrees";
                break;

            case 'r':
                _lastRefresh = DateTimeOffset.MinValue;
                _status = "refreshing";
                break;

            case 'f':
                _logFilter = _logFilter with { ApplicationOnly = !_logFilter.ApplicationOnly };
                _lastLogRead = DateTimeOffset.MinValue;
                break;

            case 'l':
                _logFilter = _logFilter with
                {
                    Floor = _logFilter.Floor switch
                    {
                        LevelFloor.Everything => LevelFloor.Warnings,
                        LevelFloor.Warnings => LevelFloor.Errors,
                        _ => LevelFloor.Everything,
                    },
                };
                _lastLogRead = DateTimeOffset.MinValue;
                break;

            case 't':
                if (_seq is null)
                {
                    _status = "no seq configured";
                }
                else
                {
                    _preferSeq = !_preferSeq;
                    _status = _preferSeq ? "log source: seq" : "log source: file";
                }

                break;

            case 'o':
                Open();
                break;

            case 's':
                AskToStop();
                break;
        }
    }

    private void Move(int delta)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        _selected = Math.Clamp(_selected + delta, 0, _rows.Count - 1);
    }

    private void Open()
    {
        if (CurrentRow()?.Process is not { } process || process.HttpPort is not { } port)
        {
            _status = "nothing to open: no listening port";
            return;
        }

        _status = $"opening port {port}";

        _ = Task.Run(async () =>
        {
            var url = await UrlProbe.UrlForAsync(port);
            try
            {
                Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { url }, UseShellExecute = false });
                _status = $"opened {url}";
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _status = $"could not open {url}";
            }
        });
    }

    private void AskToStop()
    {
        if (CurrentRow()?.Process is not { } process)
        {
            return;
        }

        // Signalling the application alone achieves nothing when a watcher owns it: the watcher
        // simply starts it again. The runner is what has to go.
        var target = process.RunnerPid ?? process.Pid;
        var what = process.RunnerPid is null
            ? $"{process.Name} (pid {process.Pid})"
            : $"the runner of {process.Name} (pid {target})";

        _confirmPrompt = $"SIGTERM {what}?  y / n";
        _confirmAction = () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("/bin/kill")
                {
                    ArgumentList = { "-TERM", target.ToString() },
                    UseShellExecute = false,
                });
                _status = $"sent SIGTERM to {target}";
                _lastRefresh = DateTimeOffset.MinValue;
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _status = $"could not signal {target}: {exception.Message}";
            }
        };
    }

    // --- Rendering ----------------------------------------------------------

    private int Width => Math.Max(40, AnsiConsole.Profile.Width);

    private int Height => Math.Max(12, AnsiConsole.Profile.Height);

    private int ListHeight() => Math.Clamp(_rows.Count, 3, Math.Max(3, (Height - 6) / 2));

    private int LogHeight() => Math.Max(3, Height - 7 - ListHeight());

    /// <summary>
    /// The whole screen, built to exactly one row less than the terminal. Filling every row would
    /// make the trailing newline scroll the display, and the header would be the line lost.
    /// </summary>
    private IRenderable Frame()
    {
        var listHeight = ListHeight();
        var logHeight = Math.Max(3, Height - 7 - listHeight);

        return new Rows(Header(), ListPanel(listHeight), LogPanel(logHeight), Footer());
    }


    private IRenderable Header()
    {
        var processes = _snapshot.RunningProcessCount;
        var worktrees = _snapshot.RunningWorktreeCount;
        var summary = processes == 0
            ? "[grey]nothing running[/]"
            : $"[green]{processes}[/] process{(processes == 1 ? "" : "es")} in [green]{worktrees}[/] worktree{(worktrees == 1 ? "" : "s")}";

        var right = $"[grey]{_snapshot.TakenAt:HH:mm:ss}[/]";
        var left = $"[bold]rtop[/]  {summary}";

        return new Markup(Justify(left, right, Width));
    }

    private IRenderable ListPanel(int height)
    {
        var inner = Width - 4;

        if (_rows.Count == 0)
        {
            var message = _runningOnly
                ? "Nothing running. Press [bold]a[/] to show idle worktrees too."
                : "No worktrees found. Run rtop from inside a git repository, or add one to the config.";

            return Framed(new Rows(PadTo([new Markup($"[grey]{message}[/]")], height)), "worktrees");
        }

        // Keep the selected row inside the window.
        _listOffset = Math.Clamp(_listOffset, Math.Max(0, _selected - height + 1), Math.Max(0, _selected));
        _listOffset = Math.Min(_listOffset, Math.Max(0, _rows.Count - height));

        var lines = new List<IRenderable>();

        for (var index = _listOffset; index < Math.Min(_rows.Count, _listOffset + height); index++)
        {
            lines.Add(new Markup(RenderRow(_rows[index], index == _selected, inner)));
        }

        var scrollHint = _rows.Count > height ? $"  [grey]{_listOffset + 1}-{Math.Min(_rows.Count, _listOffset + height)} of {_rows.Count}[/]" : "";
        return Framed(new Rows(PadTo(lines, height)), $"worktrees{scrollHint}");
    }

    private string RenderRow(Row row, bool selected, int width)
    {
        var text = row.Process is { } process ? ProcessLine(process, row.Worktree) : WorktreeLine(row.Worktree);
        var padded = Pad(text, width);

        return selected
            ? $"[black on steelblue1]{Markup.Escape(padded)}[/]"
            : Colourise(row, padded);
    }

    private static string WorktreeLine(Worktree worktree)
    {
        var dot = worktree.IsRunning ? "*" : "-";
        var tag = worktree.IsClaudeWorktree ? " [claude]" : worktree.IsMain ? " [main]" : "";
        var branch = worktree.Branch is null ? "" : $"  {worktree.Branch}";

        return $"{dot} {worktree.Name}{tag}{branch}";
    }

    private static string ProcessLine(DotnetProcess process, Worktree worktree)
    {
        var ports = process.Ports.Count == 0 ? "-" : string.Join(",", process.Ports);
        var log = process.Output.Kind switch
        {
            OutputKind.File => "log",
            OutputKind.Terminal => "tty",
            _ => "?",
        };

        return "    "
             + Fit(process.Name, 26)
             + Fit($"pid {process.Pid}", 12)
             + Fit(ports, 16)
             + Fit(Uptime(process.Elapsed), 9)
             + log;
    }

    /// <summary>
    /// Colour is applied after padding so the whole row highlights, which means the text has to be
    /// escaped and then wrapped rather than built with markup in the first place.
    /// </summary>
    private static string Colourise(Row row, string padded)
    {
        var escaped = Markup.Escape(padded);

        if (row.Process is { } process)
        {
            return process.Ports.Count > 0 ? $"[white]{escaped}[/]" : $"[grey85]{escaped}[/]";
        }

        return row.Worktree.IsRunning ? $"[bold green]{escaped}[/]" : $"[grey50]{escaped}[/]";
    }

    private IRenderable LogPanel(int height)
    {
        var width = Width - 4;
        var row = CurrentRow();

        if (row is null)
        {
            return Framed(new Rows(PadTo([new Markup("[grey]Nothing selected.[/]")], height)), "log");
        }

        if (row.Process is null)
        {
            var message = row.Worktree.IsRunning
                ? "Select a process below this worktree to tail its log."
                : $"Nothing is running in {Markup.Escape(row.Worktree.Path)}.";

            return Framed(new Rows(PadTo([new Markup($"[grey]{message}[/]")], height)), "log");
        }

        var total = _logLines.Count;
        var end = Math.Max(0, total - _logScroll);
        var start = Math.Max(0, end - height);
        var window = PadTo(
            [.. _logLines.Skip(start).Take(end - start).Select(line => new Markup(LogLine(line, width)))],
            height);

        var header = $"log  {Markup.Escape(row.Process.Name)}";
        if (_logSource is not null)
        {
            var room = Math.Max(10, width - Cells(row.Process.Name) - 40);
            header += $"  [grey]{Markup.Escape(Fit(_logSource.Description, room, pad: false))}[/]";
        }

        if (_logScroll > 0)
        {
            header += $"  [yellow]scrolled +{_logScroll}[/]";
        }

        if (_logSource?.SupportsFiltering == true)
        {
            var floor = _logFilter.Floor switch
            {
                LevelFloor.Warnings => "warn+",
                LevelFloor.Errors => "errors",
                _ => "all",
            };
            header += $"  [grey]{(_logFilter.ApplicationOnly ? "app-only" : "everything")} / {floor}[/]";
        }

        return Framed(new Rows(window), header);
    }

    private static string LogLine(string line, int width)
    {
        var text = Markup.Escape(Fit(line, width, pad: false));

        if (line.Contains(" ERR", StringComparison.Ordinal) || line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("fail", StringComparison.OrdinalIgnoreCase) || line.Contains("Exception", StringComparison.Ordinal))
        {
            return $"[red]{text}[/]";
        }

        if (line.Contains(" WRN", StringComparison.Ordinal) || line.Contains("warn", StringComparison.OrdinalIgnoreCase))
        {
            return $"[yellow]{text}[/]";
        }

        return text;
    }

    private IRenderable Footer()
    {
        if (_confirmPrompt is not null)
        {
            return new Markup($"[black on yellow] {Markup.Escape(Pad(_confirmPrompt, Width - 2))} [/]");
        }

        var keys = "[grey]↑↓[/] select  [grey]o[/]pen  [grey]s[/]top  [grey]a[/]ll  [grey]t[/]oggle src  "
                 + "[grey]f[/]ilter  [grey]l[/]evel  [grey]PgUp/PgDn[/] scroll  [grey]q[/]uit";

        return new Markup(Justify(keys, _status is null ? "" : $"[grey]{Markup.Escape(_status)}[/]", Width));
    }

    /// <summary>Blank rows to the requested height, so a panel is always the size it was given.</summary>
    private static List<IRenderable> PadTo(List<IRenderable> lines, int height)
    {
        while (lines.Count < height)
        {
            lines.Add(new Text(""));
        }

        return lines;
    }

    private static IRenderable Framed(IRenderable content, string header) =>
        new Panel(content)
            .Header($" {header} ")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey30)
            .Expand();

    // --- Text helpers -------------------------------------------------------

    /// <summary>Width in terminal cells, which is not the character count for emoji and CJK.</summary>
    private static int Cells(string text) => new Segment(text).CellCount();

    private static string Fit(string text, int width, bool pad = true)
    {
        if (width <= 0)
        {
            return "";
        }

        if (Cells(text) > width)
        {
            var kept = new System.Text.StringBuilder();
            var used = 0;

            foreach (var rune in text.EnumerateRunes())
            {
                var size = Cells(rune.ToString());
                if (used + size > width - 1)
                {
                    break;
                }

                kept.Append(rune);
                used += size;
            }

            return kept.Append('…').ToString().PadRight(width - used - 1 + kept.Length);
        }

        return pad ? text + new string(' ', width - Cells(text)) : text;
    }

    private static string Pad(string text, int width) => Fit(text, Math.Max(1, width));

    /// <summary>Left and right fragments on one line; both may contain markup, so measure plainly.</summary>
    private static string Justify(string left, string right, int width)
    {
        var gap = Math.Max(1, width - Plain(left).Length - Plain(right).Length);
        return left + new string(' ', gap) + right;
    }

    private static string Plain(string markup) =>
        System.Text.RegularExpressions.Regex.Replace(markup, @"\[[^\]]*\]", "");

    private static string Uptime(TimeSpan? elapsed) => elapsed switch
    {
        null => "-",
        { TotalHours: >= 24 } value => $"{(int)value.TotalDays}d {value.Hours}h",
        { TotalMinutes: >= 60 } value => $"{(int)value.TotalHours}h {value.Minutes}m",
        var value => $"{(int)value!.Value.TotalMinutes}m",
    };

    private static void EnterAlternateScreen() => Console.Write("\u001b[?1049h\u001b[?25l");

    private static void LeaveAlternateScreen() => Console.Write("\u001b[?25h\u001b[?1049l");

    public void Dispose() => _seq?.Dispose();
}
