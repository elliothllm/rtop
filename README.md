# rtop

A terminal dashboard that shows which .NET apps are running on your Mac, which git worktree each
one was built in, what ports they are listening on, and their live log output.

Nothing has to be registered, configured or instrumented. If a .NET app is running, it shows up.

```
rtop  2 processes in 1 worktree                                                               15:35:42
╭─worktrees  1-6 of 9────────────────────────────────────────────────────────────────────────────────╮
│ * checkout-totals [claude]  feature/checkout-totals                                                │
│     Shop.Api               pid 50663   5000,5001       6m       log                                │
│     Shop.Worker            pid 12008   -               35m      log                                │
│ - myapp [main]  main                                                                               │
│ - basket-rounding [claude]  fix/basket-rounding                                                    │
│ - myapp-wt1  spike/search-ranking                                                                  │
╰────────────────────────────────────────────────────────────────────────────────────────────────────╯
╭─log  Shop.Api  /Users/you/dev/myapp/.git/run-logs/…────────────────────────────────────────────────╮
│ [api] [15:30:15 INF] Now listening on: https://localhost:5001                                      │
│ [api] [15:30:20 WRN] The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-…│
╰────────────────────────────────────────────────────────────────────────────────────────────────────╯
↑↓ select  open  stop  all  toggle src  filter  level  PgUp/PgDn scroll  quit
```

It is built for working across several worktrees of the same repository at once, where two copies of
the same service can be running on different ports and it is not obvious which is which.

## Requirements

- **macOS.** Discovery is built on `ps` and `lsof`, and `o` shells out to `open`.
- **The .NET SDK on PATH** (.NET 10). Get it from [dot.net](https://dot.net). The tool is
  framework-dependent, so it uses the runtime you already have.

## Install

```bash
git clone https://github.com/elliothllm/rtop.git
cd rtop
./install.sh
```

Then run it from inside any git repository:

```bash
rtop
```

`install.sh` packs the project, installs it as a global .NET tool, makes sure your shell can find
it, and verifies the result. Re-running it is also how you update after changing the code.

| Task | Command |
| --- | --- |
| Install, or update after a code change | `./install.sh` |
| Install without creating a symlink or touching PATH | `./install.sh --no-link` |
| Install and append the tools dir to `~/.zshrc` | `./install.sh --add-to-path` |
| Uninstall | `./install.sh --uninstall` |
| Run without installing | `dotnet run --project src/Rtop -- --json` |

<details>
<summary>What install.sh does, and why</summary>

1. **Packs** `src/Rtop` to `artifacts/package` as a `.nupkg`.
2. **Uninstalls then installs** the global tool. Not `dotnet tool update` — the package version does
   not change between local builds, so an update would decide it already had the newest one and
   quietly keep the old binary. Uninstall-then-install always gives you the code you just built.
3. **Makes it reachable.** `dotnet tool install --global` puts a shim in `~/.dotnet/tools`, which is
   not on PATH by default on macOS. In order of preference the script:
   - does nothing, if that directory is already on your PATH;
   - otherwise symlinks the shim into the first directory that is **both writable and already on
     PATH** — `/opt/homebrew/bin`, `/usr/local/bin`, `~/.local/bin`, `~/bin`. This changes nothing
     about what your shell searches, so it cannot break anything else;
   - otherwise prints the one line to add to `~/.zshrc`, and only appends it for you if you pass
     `--add-to-path`. Editing your dotfiles is opt-in.
4. **Verifies** by running the shim and reporting where `rtop` resolves from.

Uninstall removes the global tool and any symlink the script created — it identifies its own by
checking the link points into `~/.dotnet/tools`, so it will not touch an unrelated `rtop` on your
system. It deliberately leaves `~/.config/rtop/config.json` alone; delete that yourself if you want
a clean slate.

</details>

<details>
<summary>If the shell cannot find it</summary>

```bash
ls -l ~/.dotnet/tools/rtop     # is the shim there at all?
~/.dotnet/tools/rtop --version # does it run when called directly?
echo $PATH | tr ':' '\n'       # is ~/.dotnet/tools among these?
```

If the shim runs directly but `rtop` does not resolve, it is purely a PATH problem — re-run
`./install.sh --add-to-path`, then open a new shell. If you install into a fresh shell and the old
one still cannot see it, that shell captured PATH before the change; open a new one.

</details>

## Keys

| | |
| --- | --- |
| `↑` `↓` / `k` `j` | move the selection; the log pane follows it |
| `PgUp` `PgDn` | scroll the log — `Home` and `End` jump to either end |
| `o` | open the selected process's port in a browser |
| `s` | SIGTERM the runner behind the selected process (asks first) |
| `a` | show every worktree, or only the ones running something |
| `t` | switch the log between its file and Seq |
| `f` | hide `Microsoft.*` framework logging (Seq only) |
| `l` | cycle the level floor: all, warnings, errors (Seq only) |
| `r` | refresh now |
| `q` | quit |

**`o`** probes the port with a TLS handshake before opening it, because a dev server commonly
listens on an https and an http port with nothing in the number to tell them apart.

**`s`** signals the *runner*, not the application: killing the app alone just makes `dotnet watch`
start it again. The runner is found by walking up the ancestry while each parent is a recognised
dev runner (`dotnet`, `node`, `npm`, `task`, `make`, …), and the walk stops the moment it is not —
so it can never climb out into your shell or terminal emulator. The confirmation names the exact
pid and command before anything is sent.

## How it finds things

Everything comes from the process table outwards, which is what makes it work on any repository:

1. **Applications** — every process whose executable is a built apphost (`…/bin/Debug/net10.0/My.App`)
   or a `dotnet …/bin/Debug/net10.0/My.App.dll` launch. The `dotnet watch` / `dotnet run` / MSBuild
   machinery around them is filtered out: that is *how* an app runs, not a thing you want listed.
2. **Worktrees** — walk up from the executable until a `.git` appears. That is the worktree the
   binary was built in. Then `git worktree list --porcelain` fills in every *other* worktree of the
   same repository, with its branch, so idle ones are listed too. Anything under
   `.claude/worktrees/` is tagged `[claude]`.
3. **Ports** — one `lsof -nP -iTCP -sTCP:LISTEN` for the whole machine, indexed by pid.
4. **Logs** — see below.

The repository you run `rtop` in is always listed, whether or not anything is running in it.

### Finding a log with no configuration

A dev server's console output usually is not written anywhere you can read: the app's stdout is a
pipe to `dotnet watch`, whose stdout is a pipe to whatever started *it*. So rtop walks the process
ancestry and takes **the first ancestor whose stdout is a real file**. That is where the output
actually lands, and it works without knowing anything about the project:

```
Shop.Api              stdout -> PIPE
dotnet run            stdout -> PIPE
dotnet-watch.dll      stdout -> PIPE
dotnet watch run      stdout -> PIPE
task run-api          stdout -> /…/logs/wt-b7a49f6d49.log   <- tail this
```

If nothing in the chain has one, the output belongs to somebody's terminal and is genuinely
unrecoverable — rtop says so, and names the tty, rather than showing an empty pane.

For that case there is an **optional** [Seq](https://datalust.co/seq) fallback. Configure a `seq`
block and rtop will query it instead, matching on `ProcessId` so two worktrees running the same
service do not show each other's lines. It accumulates the pids it has seen for an executable, so a
watcher restart does not blank the pane. This only finds anything if your app enriches its logs with
`ProcessId`.

## `--json`

The same snapshot, for when you just want the data:

```bash
rtop --json | jq '.worktrees[] | select(.running) | .processes[] | {name, pid, ports}'
```

```json
{
  "takenAt": "2026-09-02T15:20:32+01:00",
  "runningProcesses": 2,
  "runningWorktrees": 1,
  "warnings": [],
  "worktrees": [
    {
      "name": "checkout-totals",
      "path": "/Users/you/dev/myapp/.claude/worktrees/checkout-totals",
      "branch": "feature/checkout-totals",
      "repository": { "name": "myapp", "path": "/Users/you/dev/myapp" },
      "isMain": false,
      "isClaudeWorktree": true,
      "running": true,
      "processes": [
        {
          "pid": 66421,
          "name": "Shop.Api",
          "executablePath": "/Users/…/bin/Debug/net10.0/Shop.Api",
          "command": "/Users/…/bin/Debug/net10.0/Shop.Api",
          "uptimeSeconds": 7239,
          "ports": [5000, 5001],
          "runnerPid": 55470,
          "log": { "kind": "file", "path": "/Users/…/logs/wt-b7a49f6d49.log", "ownerPid": 55494 }
        }
      ]
    }
  ]
}
```

`log.kind` is `file` (tailable), `terminal` (output belongs to a tty, path names it) or `unknown`.
The shape is written by hand rather than serialised from the internal types, so it is a stable
contract to script against.

Running `rtop` with stdout redirected exits 2 and points at `--json` rather than drawing to a pipe.

## Configuration

`~/.config/rtop/config.json` is optional — `rtop --write-config` creates it.

```json
{
  "repositories": [],
  "refreshSeconds": 3,
  "logTailLines": 2000,
  "seq": null
}
```

- **`repositories`** — extra repositories whose worktrees should always be listed, even with
  nothing running. Anything with a running process is included regardless, as is the repository you
  are standing in, so this is usually left empty. `~` is expanded.
- **`seq`** — `{ "url": "http://localhost:5341", "username": "…", "password": "…" }`, or an
  `apiKey` instead of the login, or blank credentials for an unauthenticated Seq.

## Layout

```
src/Rtop/
  Program.cs            argument parsing and the two modes
  JsonOutput.cs         the --json contract
  Core/
    ProcessTable.cs     one ps snapshot, ancestry indexed, .NET apps identified
    Lsof.cs             listening ports and stdout targets, batched across pids
    GitWorktrees.cs     git worktree list, and finding the root above a path
    Discovery.cs        assembles the snapshot
    UrlProbe.cs         TLS handshake to pick http:// or https://
  Logs/                 file tail vs. Seq query behind one interface
  Tui/Dashboard.cs      the interactive screen
```

## Notes

- **Polling, not watching.** A process that starts shows up within `refreshSeconds`.
- **The screen is redrawn on change, or four times a second at most** — painting a full screen at
  the input polling rate is a lot of bytes to push down an ssh connection for no benefit.
- Column widths are measured in terminal cells rather than characters, so a log line containing an
  emoji (`dotnet watch ⌚`) does not overflow the panel and wrap.

## License

[MIT](LICENSE).
