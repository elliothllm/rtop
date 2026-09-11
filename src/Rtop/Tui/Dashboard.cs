using System.Diagnostics;
using System.Text;
using Rtop.Config;
using Rtop.Core;
using Rtop.Logs;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Rtop.Tui;

/// <summary>
/// The interactive screen: a list of worktrees and the .NET processes running out of them, over a
/// log pane that follows whatever is selected.
///
/// With <paramref name="showLog"/> off the log pane is gone and no log is ever read — what is left
/// is the worktree list on its own, for a pane too small to have shown a log usefully anyway.
/// </summary>
public sealed class Dashboard(RtopConfig config, bool showLog = true) : IDisposable
{
    private sealed record Row(Worktree Worktree, DotnetProcess? Process)
    {
        public bool IsProcess => Process is not null;
    }

    /// <summary>Which pane the arrow keys are driving.</summary>
    private enum Pane
    {
        List,
        Log,
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

    private Pane _focus = Pane.List;

    /// <summary>The log line being read in full, or -1 when the log pane has no cursor.</summary>
    private int _logCursor = -1;

    /// <summary>Its text, so the cursor can follow that line as the tail scrolls underneath it.</summary>
    private string? _logCursorText;

    private int _logWindowStart;
    private int _logWindowEnd;

    private bool _dirty = true;
    private DateTimeOffset _lastDraw = DateTimeOffset.MinValue;

    /// <summary>
    /// The confirmation, written several times over from longest to shortest. The bar is one line
    /// and cannot wrap, so a narrow terminal gives up detail rather than the `y / n` on the end —
    /// a question you cannot see the answer to is worse than a vague one.
    /// </summary>
    private IReadOnlyList<string>? _confirmPrompt;

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
        if (!showLog)
        {
            return;
        }

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
            FocusList();
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

            Reanchor();
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

    /// <summary>
    /// The tail is a window that slides: when lines arrive at the bottom, everything already in it
    /// shifts up by as many. Follow the line being read by its text, so the cursor stays on the
    /// entry the reader put it on rather than on whatever now holds that index.
    /// </summary>
    private void Reanchor()
    {
        if (_logCursor < 0)
        {
            return;
        }

        if (_logLines.Count == 0)
        {
            FocusList();
            return;
        }

        if (_logCursorText is null || (_logCursor < _logLines.Count && _logLines[_logCursor] == _logCursorText))
        {
            _logCursor = Math.Min(_logCursor, _logLines.Count - 1);
            return;
        }

        for (var distance = 1; distance <= _logLines.Count; distance++)
        {
            foreach (var candidate in (int[])[_logCursor - distance, _logCursor + distance])
            {
                if (candidate >= 0 && candidate < _logLines.Count && _logLines[candidate] == _logCursorText)
                {
                    _logCursor = candidate;
                    return;
                }
            }
        }

        // The line has fallen out of the tail altogether; leave the cursor where it is.
        _logCursor = Math.Clamp(_logCursor, 0, _logLines.Count - 1);
        _logCursorText = _logLines[_logCursor];
    }

    private void FocusLog()
    {
        if (_logLines.Count == 0)
        {
            _status = "no log lines to read";
            return;
        }

        _focus = Pane.Log;

        if (_logCursor < 0)
        {
            SetLogCursor(Math.Max(0, (_logWindowEnd > 0 ? _logWindowEnd : _logLines.Count) - 1));
        }
    }

    private void FocusList()
    {
        _focus = Pane.List;
        _logCursor = -1;
        _logCursorText = null;
    }

    private void MoveLogCursor(int delta)
    {
        if (_logLines.Count == 0)
        {
            return;
        }

        SetLogCursor((_logCursor < 0 ? _logWindowEnd - 1 : _logCursor) + delta);
    }

    private void SetLogCursor(int index)
    {
        var total = _logLines.Count;

        if (total == 0)
        {
            return;
        }

        _logCursor = Math.Clamp(index, 0, total - 1);
        _logCursorText = _logLines[_logCursor];

        // Move the window with the cursor in one step, from where the last frame actually landed,
        // rather than nudging it a row at a time until the cursor comes back into view.
        var end = _logWindowEnd > 0 ? _logWindowEnd : total;

        if (_logCursor >= end)
        {
            end = _logCursor + 1;
        }
        else if (_logCursor < _logWindowStart)
        {
            end -= _logWindowStart - _logCursor;
        }

        _logScroll = Math.Clamp(total - end, 0, Math.Max(0, total - 1));
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

        var page = Math.Max(1, (showLog ? LogHeight() : ListHeight()) - 1);

        switch (key.Key)
        {
            case ConsoleKey.Tab:
                if (!showLog)
                {
                    return;
                }

                if (_focus == Pane.Log)
                {
                    FocusList();
                }
                else
                {
                    FocusLog();
                }

                return;

            case ConsoleKey.Enter:
                if (showLog)
                {
                    FocusLog();
                }

                return;

            case ConsoleKey.UpArrow:
                Up(1);
                return;

            case ConsoleKey.DownArrow:
                Down(1);
                return;

            case ConsoleKey.PageUp:
                Up(page);
                return;

            case ConsoleKey.PageDown:
                Down(page);
                return;

            case ConsoleKey.Home:
                if (!showLog)
                {
                    _selected = 0;
                }
                else if (_focus == Pane.Log)
                {
                    SetLogCursor(0);
                }
                else
                {
                    _logScroll = Math.Max(0, _logLines.Count - LogHeight());
                }

                return;

            case ConsoleKey.End:
                if (!showLog)
                {
                    _selected = Math.Max(0, _rows.Count - 1);
                }
                else if (_focus == Pane.Log)
                {
                    SetLogCursor(_logLines.Count - 1);
                }
                else
                {
                    _logScroll = 0;
                }

                return;

            case ConsoleKey.Escape:
                if (_focus == Pane.Log)
                {
                    FocusList();
                }
                else
                {
                    _running = false;
                }

                return;
        }

        switch (char.ToLowerInvariant(key.KeyChar))
        {
            case 'k':
                Up(1);
                break;

            case 'j':
                Down(1);
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
                if (!showLog)
                {
                    break;
                }

                _logFilter = _logFilter with { ApplicationOnly = !_logFilter.ApplicationOnly };
                _lastLogRead = DateTimeOffset.MinValue;
                break;

            case 'l':
                if (!showLog)
                {
                    break;
                }

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
                if (!showLog)
                {
                    break;
                }

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

    /// <summary>
    /// Up and down drive whichever pane has focus: the list selection, or the log cursor once the
    /// log pane has been stepped into.
    /// </summary>
    private void Up(int lines)
    {
        if (_focus == Pane.Log)
        {
            MoveLogCursor(-lines);
        }
        else if (lines == 1 || !showLog)
        {
            Move(-lines);
        }
        else
        {
            _logScroll = Math.Clamp(_logScroll + lines, 0, Math.Max(0, _logLines.Count - 1));
        }
    }

    private void Down(int lines)
    {
        if (_focus == Pane.Log)
        {
            MoveLogCursor(lines);
        }
        else if (lines == 1 || !showLog)
        {
            Move(lines);
        }
        else
        {
            _logScroll = Math.Max(0, _logScroll - lines);
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

        // What goes first as the bar narrows: the word SIGTERM, then the pid, then the name. What
        // is left at the end still says that something is about to be stopped and how to say yes.
        _confirmPrompt = process.RunnerPid is null
            ?
            [
                $"SIGTERM {process.Name} (pid {target})?  y / n",
                $"Stop {process.Name} ({target})?  y / n",
                $"Stop process {target}?  y / n",
                "Stop process?  y/n",
                "Stop? y/n",
            ]
            :
            [
                $"SIGTERM the runner of {process.Name} (pid {target})?  y / n",
                $"Stop the runner of {process.Name} ({target})?  y / n",
                $"Stop runner {target}?  y / n",
                "Stop runner?  y/n",
                "Stop? y/n",
            ];
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

    private int Width => Math.Max(20, AnsiConsole.Profile.Width);

    private int Height => Math.Max(12, AnsiConsole.Profile.Height);

    /// <summary>Rows for the two panes to divide: what the header, footer and borders leave over.</summary>
    private int Available => Math.Max(2, Height - (showLog ? 7 : 5));

    private int ListHeight() =>
        showLog ? Math.Clamp(Math.Max(_rows.Count, 3), 1, Math.Max(1, Available / 2)) : Available;

    private int LogHeight() => Math.Max(1, Available - ListHeight());

    /// <summary>
    /// The whole screen, built to exactly one row less than the terminal. Filling every row would
    /// make the trailing newline scroll the display, and the header would be the line lost.
    /// </summary>
    private IRenderable Frame() =>
        showLog
            ? new Rows(Header(), ListPanel(ListHeight()), LogPanel(LogHeight()), Footer())
            : new Rows(Header(), ListPanel(ListHeight()), Footer());

    private IRenderable Header()
    {
        var processes = _snapshot.RunningProcessCount;
        var worktrees = _snapshot.RunningWorktreeCount;

        var summary = processes == 0
            ? "[grey]nothing running[/]"
            : Width < 44
                ? $"[green]{processes}[/] in [green]{worktrees}[/]"
                : $"[green]{processes}[/] process{(processes == 1 ? "" : "es")} in [green]{worktrees}[/] worktree{(worktrees == 1 ? "" : "s")}";

        var left = $"[bold]rtop[/]  {summary}";
        var clock = $"{_snapshot.TakenAt:HH:mm:ss}";
        var right = Plain(left).Length + 1 + clock.Length <= Width ? $"[grey]{clock}[/]" : "";

        return new Markup(Justify(left, right, Width));
    }

    private IRenderable ListPanel(int height)
    {
        var inner = Width - 4;
        var focused = _focus == Pane.List;

        if (_rows.Count == 0)
        {
            var message = _runningOnly
                ? "Nothing running. Press a to show idle worktrees too."
                : "No worktrees found. Run rtop from inside a git repository, or add one to the config.";

            return Framed(new Rows(PadTo([Note(message, inner)], height)), "worktrees", focused);
        }

        // Keep the selected row inside the window.
        _listOffset = Math.Clamp(_listOffset, Math.Max(0, _selected - height + 1), Math.Max(0, _selected));
        _listOffset = Math.Min(_listOffset, Math.Max(0, _rows.Count - height));

        // One column layout for the whole table, so the columns still line up with each other once
        // some of them have been shed.
        var columns = ProcessColumns.For(
            inner,
            _rows.Select(row => row.Process is { } process ? Cells(PortsText(process)) + 2 : 0)
                .DefaultIfEmpty(8)
                .Max());

        var lines = new List<IRenderable>();

        for (var index = _listOffset; index < Math.Min(_rows.Count, _listOffset + height); index++)
        {
            lines.Add(new Markup(RenderRow(_rows[index], index == _selected, inner, columns)));
        }

        var scrollHint = _rows.Count > height
            ? $"  [grey]{_listOffset + 1}-{Math.Min(_rows.Count, _listOffset + height)} of {_rows.Count}[/]"
            : "";

        return Framed(new Rows(PadTo(lines, height)), $"worktrees{scrollHint}", focused);
    }

    private string RenderRow(Row row, bool selected, int width, ProcessColumns columns)
    {
        var text = row.Process is { } process ? ProcessLine(process, columns) : WorktreeLine(row.Worktree, width);
        var padded = Pad(text, width);

        if (!selected)
        {
            return Colourise(row, padded);
        }

        // Dim the bar while the log pane has the arrow keys, so it is never ambiguous which pane
        // the next keypress is going to move.
        return _focus == Pane.List
            ? $"[black on steelblue1]{Markup.Escape(padded)}[/]"
            : $"[black on grey58]{Markup.Escape(padded)}[/]";
    }

    /// <summary>
    /// The worktree's name is its identity and the branch is context, so a narrow terminal gives up
    /// the branch before it gives up the end of the name.
    /// </summary>
    private static string WorktreeLine(Worktree worktree, int width)
    {
        var dot = worktree.IsRunning ? "*" : "-";
        var tag = worktree.IsClaudeWorktree ? " [claude]" : worktree.IsMain ? " [main]" : "";
        var name = Fit(worktree.Name, Math.Max(4, width - 2 - Cells(tag)), pad: false);
        var head = $"{dot} {name}{tag}";

        if (worktree.Branch is null)
        {
            return head;
        }

        var room = width - Cells(head) - 2;
        return room < 6 ? head : $"{head}  {Fit(worktree.Branch, room, pad: false)}";
    }

    private static string ProcessLine(DotnetProcess process, ProcessColumns columns)
    {
        var kind = process.Output.Kind switch
        {
            OutputKind.File => "log",
            OutputKind.Terminal => "tty",
            _ => "?",
        };

        return new string(' ', columns.Indent)
             + Fit(process.Name, columns.Name)
             + Fit($"pid {process.Pid}", columns.Pid)
             + Fit(PortsText(process), columns.Ports)
             + Fit(Uptime(process.Elapsed), columns.Uptime)
             + Fit(kind, columns.Kind, pad: false);
    }

    private static string PortsText(DotnetProcess process) =>
        process.Ports.Count == 0 ? "-" : string.Join(",", process.Ports);

    /// <summary>
    /// How wide each column of a process row is at the current terminal width, where zero means the
    /// column is not drawn at all. The ports are the one thing the row exists to answer — which
    /// copy of the service is on which port — so every other column goes before they are touched.
    /// </summary>
    private readonly record struct ProcessColumns(int Indent, int Name, int Pid, int Ports, int Uptime, int Kind)
    {
        public static ProcessColumns For(int width, int portsNeeded)
        {
            var indent = 4;
            var name = 26;
            var pid = 12;
            var ports = Math.Clamp(portsNeeded, 8, 22);
            var uptime = 9;
            var kind = 3;

            int Over() => indent + name + pid + ports + uptime + kind - width;

            // Shed in order of how little each is worth once the space is not there.
            if (Over() > 0)
            {
                kind = 0;
            }

            if (Over() > 0)
            {
                uptime = 0;
            }

            if (Over() > 0)
            {
                name = Math.Max(14, name - Over());
            }

            if (Over() > 0)
            {
                pid = 0;
            }

            if (Over() > 0)
            {
                indent = 2;
            }

            if (Over() > 0)
            {
                name = Math.Max(4, name - Over());
            }

            // Only now, with nothing else left to give, may the ports themselves be truncated.
            if (Over() > 0)
            {
                ports = Math.Max(4, ports - Over());
            }

            // Dropping a column usually overshoots; hand what that freed back to the name.
            name = Math.Clamp(name - Over(), 4, 26);

            return new ProcessColumns(indent, name, pid, ports, uptime, kind);
        }
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
        var focused = _focus == Pane.Log;

        if (row is null)
        {
            return Framed(new Rows(PadTo([Note("Nothing selected.", width)], height)), "log", focused);
        }

        if (row.Process is null)
        {
            var message = row.Worktree.IsRunning
                ? "Select a process below this worktree to tail its log."
                : $"Nothing is running in {row.Worktree.Path}.";

            return Framed(new Rows(PadTo([Note(message, width)], height)), "log", focused);
        }

        return Framed(new Rows(LogWindow(width, height)), LogHeader(row.Process.Name, width), focused);
    }

    /// <summary>
    /// The pane's title, assembled from whatever fits: the process name always, then each note in
    /// turn, where a note takes only what the notes after it have not already claimed.
    /// </summary>
    private string LogHeader(string name, int width)
    {
        // Only the source description reads sensibly cut short; a counter that says "line 1…" is
        // worse than no counter, so the rest are all or nothing.
        List<(string Text, string Colour, bool Truncatable)> notes = [];

        if (_logSource is not null)
        {
            notes.Add((_logSource.Description, "grey", true));
        }

        if (_focus == Pane.Log)
        {
            notes.Add(($"line {_logCursor + 1} of {_logLines.Count}", "steelblue1", false));
        }
        else if (_logScroll > 0)
        {
            notes.Add(($"scrolled +{_logScroll}", "yellow", false));
        }

        if (_logSource?.SupportsFiltering == true)
        {
            var floor = _logFilter.Floor switch
            {
                LevelFloor.Warnings => "warn+",
                LevelFloor.Errors => "errors",
                _ => "all",
            };

            notes.Add(($"{(_logFilter.ApplicationOnly ? "app-only" : "everything")} / {floor}", "grey", false));
        }

        var header = $"log  {Markup.Escape(name)}";
        var used = 5 + Cells(name);

        for (var index = 0; index < notes.Count; index++)
        {
            var reserve = notes.Skip(index + 1).Sum(note => 2 + Cells(note.Text));
            var room = width - used - 2 - reserve;
            var note = notes[index];

            if (room < (note.Truncatable ? 6 : Cells(note.Text)))
            {
                continue;
            }

            var text = note.Truncatable ? Fit(note.Text, room, pad: false) : note.Text;
            header += $"  [{note.Colour}]{Markup.Escape(text)}[/]";
            used += 2 + Cells(text);
        }

        return header;
    }

    /// <summary>
    /// The visible tail. Every line is one row except the one being read, which is wrapped over as
    /// many rows as its text needs — so the window has to be filled upwards from the bottom rather
    /// than sliced out of the list by index.
    /// </summary>
    private List<IRenderable> LogWindow(int width, int height)
    {
        var total = _logLines.Count;

        if (total == 0)
        {
            _logWindowStart = 0;
            _logWindowEnd = 0;
            return PadTo([], height);
        }

        var wrapRows = Math.Clamp(height - 1, 1, 12);
        var end = Math.Clamp(total - _logScroll, 1, total);

        if (_logCursor >= end)
        {
            end = _logCursor + 1;
        }

        var rows = new List<IRenderable>();
        var start = end;

        // Wrapping can push the line being read off the top of the window, or leave only its last
        // few rows showing. Either way, give the bottom row back and fill again until the whole of
        // it is on screen.
        for (var attempt = 0; attempt <= wrapRows; attempt++)
        {
            rows.Clear();
            start = end;
            var clipped = false;

            while (start > 0 && rows.Count < height)
            {
                var entry = Entry(start - 1, width, wrapRows);
                var room = height - rows.Count;

                if (entry.Count > room)
                {
                    clipped |= start - 1 == _logCursor;
                    rows.InsertRange(0, entry.Skip(entry.Count - room));
                }
                else
                {
                    rows.InsertRange(0, entry);
                }

                start--;
            }

            if (_logCursor < 0 || (_logCursor >= start && !clipped) || end <= _logCursor + 1)
            {
                break;
            }

            end--;
        }

        _logWindowStart = start;
        _logWindowEnd = end;
        _logScroll = Math.Max(0, total - end);

        return PadTo(rows, height);
    }

    private List<IRenderable> Entry(int index, int width, int maxRows)
    {
        var line = _logLines[index];

        if (index != _logCursor)
        {
            return [new Markup(LogLine(line, width))];
        }

        // The line being read is the one place the whole text is shown. Highlighting every row of
        // it, padded out to the full width, keeps the block reading as a single entry.
        return
        [
            .. Wrap(line, width, maxRows)
                .Select(part => (IRenderable)new Markup($"[black on steelblue1]{Markup.Escape(Pad(part, width))}[/]")),
        ];
    }

    private static string LogLine(string line, int width)
    {
        // A renderable with no text at all occupies no row, which would silently shorten the pane
        // by one for every blank line in the log — and a tail always ends with one.
        var text = line.Length == 0 ? " " : Markup.Escape(Fit(line, width, pad: false));

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
        if (_confirmPrompt is { Count: > 0 } prompts)
        {
            var bar = Width - 2;
            var prompt = prompts.FirstOrDefault(text => Cells(text) <= bar) ?? prompts[^1];

            return new Markup($"[black on yellow] {Markup.Escape(Pad(prompt, bar))} [/]");
        }

        var status = _status is null ? "" : Fit(_status, Math.Max(8, Width / 2), pad: false);
        var room = Width - (status.Length == 0 ? 0 : status.Length + 2);

        // The text, how much of it is the key, and how readily it is dropped when the terminal is
        // too narrow to advertise everything. Every key still works whether or not it is listed.
        (string Text, int Key, int Drop)[] keys = _focus == Pane.Log
            ?
            [
                ("↑↓ line", 2, 0),
                ("esc back", 3, 1),
                ("Home/End ends", 8, 5),
                ("open", 1, 3),
                ("stop", 1, 4),
                ("quit", 1, 2),
            ]
            : showLog
            ?
            [
                ("↑↓ select", 2, 0),
                ("tab read log", 3, 2),
                ("open", 1, 3),
                ("stop", 1, 4),
                ("all", 1, 5),
                ("toggle src", 1, 8),
                ("filter", 1, 7),
                ("level", 1, 6),
                ("PgUp/PgDn scroll", 9, 9),
                ("quit", 1, 1),
            ]
            :
            [
                ("↑↓ select", 2, 0),
                ("open", 1, 2),
                ("stop", 1, 3),
                ("all", 1, 4),
                ("PgUp/PgDn page", 9, 5),
                ("quit", 1, 1),
            ];

        var shown = new HashSet<int>();
        var budget = room;

        foreach (var index in Enumerable.Range(0, keys.Length).OrderBy(index => keys[index].Drop))
        {
            var cost = keys[index].Text.Length + (shown.Count == 0 ? 0 : 2);

            if (cost <= budget)
            {
                budget -= cost;
                shown.Add(index);
            }
        }

        var line = string.Join("  ", Enumerable.Range(0, keys.Length)
            .Where(shown.Contains)
            .Select(index => $"[grey]{keys[index].Text[..keys[index].Key]}[/]{keys[index].Text[keys[index].Key..]}"));

        return new Markup(Justify(line, status.Length == 0 ? "" : $"[grey]{Markup.Escape(status)}[/]", Width));
    }

    /// <summary>
    /// A line of explanation inside a panel, cut to the width rather than wrapped: a wrapped line
    /// would make the panel taller than the height it was drawn to.
    /// </summary>
    private static IRenderable Note(string text, int width) =>
        new Markup($"[grey]{Markup.Escape(Fit(text, width, pad: false))}[/]");

    /// <summary>
    /// Blank rows to the requested height, so a panel is always the size it was given. The blank is
    /// a space rather than an empty string: an empty renderable takes up no row at all.
    /// </summary>
    private static List<IRenderable> PadTo(List<IRenderable> lines, int height)
    {
        while (lines.Count < height)
        {
            lines.Add(new Text(" "));
        }

        return lines;
    }

    private static IRenderable Framed(IRenderable content, string header, bool focused) =>
        new Panel(content)
            .Header($" {header} ")
            .Border(BoxBorder.Rounded)
            .BorderColor(focused ? Color.SteelBlue : Color.Grey30)
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
            var kept = new StringBuilder();
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

            kept.Append('…');
            used++;

            return pad ? kept.Append(' ', width - used).ToString() : kept.ToString();
        }

        return pad ? text + new string(' ', width - Cells(text)) : text;
    }

    private static string Pad(string text, int width) => Fit(text, Math.Max(1, width));

    /// <summary>
    /// One line of text broken over as many rows as it needs, with continuations indented so the
    /// block reads as one entry. Anything past maxRows is dropped, with an ellipsis to say so.
    /// </summary>
    private static List<string> Wrap(string text, int width, int maxRows)
    {
        if (width <= 2)
        {
            return [Fit(text, Math.Max(1, width), pad: false)];
        }

        const string indent = "  ";

        var rows = new List<string>();
        var current = new StringBuilder();
        var used = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var size = Cells(rune.ToString());

            if (used + size > width)
            {
                rows.Add(current.ToString());
                current.Clear().Append(indent);
                used = indent.Length;
            }

            current.Append(rune);
            used += size;
        }

        rows.Add(current.ToString());

        if (rows.Count > maxRows)
        {
            rows.RemoveRange(maxRows, rows.Count - maxRows);
            rows[^1] = Fit(rows[^1] + "…", width, pad: false);
        }

        return rows;
    }

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
