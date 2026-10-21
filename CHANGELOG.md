# Changelog

## 0.1.1 — the notch gets quieter

**Installing is one command now.** `dsh plugin --profile <p> add dsh-agents-notch` is the whole
install: the package is ~30 KB of JavaScript and the host fetches the matching 65 MB release asset
itself on first use — URL derived from `repository` and the version (never a constant), hash fetched
beside it and verified, written atomically, reported as `window-fetch` → `window-fetched` →
`window-started`. This is what every other DSH plugin does; a PowerShell step afterwards was the odd
one out. `autoDownload: false` disables it, `downloadUrl` points at a mirror, and
`tools/install-window.ps1` still installs by hand for an offline machine.
Four settings that change how it sits on the desktop, all from the plugin row's `config`, all clamped,
none remembered on disk:

| setting | default | what it does |
|---|---|---|
| `opacity` | `1` | 0.35–1: how much of the desktop it sits in front of |
| `motion` | `auto` | `auto` follows Windows' *reduce motion* setting; `on` forces the animation; `off` draws the settled pose of each state and nothing else — no blink, no breath, no pulse |
| `showProject` | `true` | the project name in the sub line (the colour chips stay) |
| `showDone` | `false`-able | `false` keeps finished sessions off the board and out of the count — applied by the HOST, so the tab cannot disagree with the rows |

`off` is not "animations disabled and everything looks broken": each state has a settled pose (the
rest face after finishing, the wide eyes of a session waiting on you, the frown of a failure), and
that is what a still notch draws.
## 0.1.0 — first release

The overlay, rebuilt as a package you can install.

**The window** (`desktop/`, WPF, .NET 9, Windows only)

- A slim always-visible tab with one bot per session, expanding on hover into a board of rows.
  Identity is the bot's colour, state is its badge and ring, and a working session deliberately has
  neither: working is the quiet default.
- Settings from the plugin row, clamped and applied live: `scale` (0.75–2), `anchor`
  (`top-center`/`top-left`/`top-right`), `screen` (`primary`/`cursor`/a display number), `maxRows`,
  `hideFullscreen`. The window remembers nothing on disk — one source of truth.
- A stale feed (>4 s) shows the unknown state: the tab says `host offline` and the sub line dates the
  last heartbeat, instead of presenting the last summary as current.
- The context readout is a percentage **only** when the route's window is known; otherwise it is a
  token count with no bar.
- Tray: hide/show, and *Quit overlay* writes `quit.json` so the supervisor stops resurrecting it for
  an hour.
- `--probe-rect` prints the resolved monitor, DIP and pixel rect, DPI and scale — placement that can
  be verified without a human looking at the screen.
- Measured behaviour change from the 0.0.x development tree: the motion is driven by
  `CompositionTarget.Rendering` instead of a 16 ms `DispatcherTimer`, which was delivering every
  25–29 ms on a 144 Hz panel (39 → 77 fps, worst frame gap 48 → 16 ms).

**The host** (`lib/`)

- Schema 3 state published atomically every `pollMs`: `context {budget, hotAt}`, `settings`,
  `retire`, `wallMs` (`docs/CONTRACT.md`).
- `context.budget` defaults to `null` and is never guessed.
- Close-out is optional: three failed probes of the `session-finished` route publish
  `retire: "timer"` and let settled rows age out after `finishedFallbackMs` (15 min); one success puts
  the verdict back.
- The window binary is found in five places in order (row config, `AGENTS_NOTCH_EXE`, installed
  release, `dotnet build`, `npm run window:publish`); when none exists the host keeps serving and logs
  one line a minute naming every path it tried and every way to fix it.
- Supervises the window: 15 s retry, three tries, parent-pid watch, and a clean kill when the plugin
  is unmounted.
- Spawns the window with `DOTNET_BUNDLE_EXTRACT_BASE_DIR` inside the state directory: a single-file
  .NET app cannot start at all when `%TEMP%` refuses its unpack directory (measured: error 5, no
  window, no message).
- 39 tests, no harness needed.

**Not in this release:** a browser half (click a row to open that session), macOS or Linux, a signed
binary, autostart.
