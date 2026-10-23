# Changelog

## 0.1.8 — the bots get faces, and the tab speaks up

**One list instead of two.** The board listed every session twice — task lines on the left, project
chips on the right — and the chips' column truncated the titles beside them. The front session is now
the headline, and every other session is one row: a small bot in its colours, its title, what it is doing
in a word, and its age. The board is as tall as its rows, and it is one surface with the tab, flaring out
of the top of the screen like a real notch.

**The headline says what the bot is doing.** `activity` and `steps` were parsed and never drawn. A working
session now shows its tool and step with the context bar; a waiting one shows its approval reason in full
with `Open ↗`; a finished or failed one shows how it ended with `✕ close`.

**The tab grows into an alert.** When a session starts waiting on you, or fails, the tab opens for six
seconds into an island with the bot, the reason and an Open button; a finish gets a two-second notice.
Only fresh events are announced, so a respawned window does not repeat old news. While any session runs a
tool, a shimmer runs along the tab's lower edge, and the tab keeps its counts next to the urgent word.

**Sixteen faces.** Thinking, focused, determined, tired, confused and dizzy (tool errors in a row),
pleading and peeking (needs you), surprised, ^^, starry, wink, love
(you just approved it), ✕ ✕, and asleep when the host goes quiet — with motion for the transitions. The
state badge is gone: the rim is the session, the ring is the state, and a working bot wears a spinner.

**Open takes you to the session, and a question says what it asks.** Open, the island's button and a
double-click on a row switch the app to that session (through the plugin's in-app half and the app's own
`openSession`), and bring the app forward — the `dsh://open` link alone could not, on Windows.
A question from `ask_user_question` shows the question's own text instead of "has a question for you".

**Every session, and a calmer surface.** The board lists every live session and scrolls past
`maxRows` (now "how many are visible"). The working spinners turn together and slower. The tool light
traces the tab's rounded lower edge and always finishes its sweep. The surprised "o" face is gone.

**Schema 4.** Three facts for the faces, all optional to a schema-3 reader: `activity.kind: "thinking"`,
`errorStreak` (a streak that the next clean result resets — never shown as a number), and `approvedAt`
(only an *allowed* approval sets it).

## 0.1.7 — the notch stops lying when it cannot see the host

**A window with no readable state file says so.** It used to start from the demo board, so a wrong
`--state`, a host that died before its first write or a deleted state dir drew three invented working
sessions and four bots — and nothing ever replaced them, because the stale path waits for a previous
state that never arrives. The empty board is now built the same way every other one is, with
`wallMs: 0`: the tab reads `host offline`, the sub line says `no state file`, and there is not a bot or
a row on it. `--demo` is the only producer of the fabricated sheet.

**A replayed session keeps the time it actually finished.** `applyEvent` stamped every event with its
arrival clock and ignored the envelope's own `time`, so the log replay the sweep does at mount published
yesterday's finish as `since: <boot>`: the row said "finished 0s ago", took the big bot as the newest
change, and got a fresh 15 minutes under `retire: "timer"` on every restart.

**The ✕ reaches the route again, and the fallback works on a custom `DSH_HOME`.** The payload stopped
publishing `finishedUrl` while the window kept reading it, so every dismiss took the fallback — which
hardcoded `%USERPROFILE%\.dsh` and silently no-oped on any machine whose home is elsewhere. The field is
published when the host knows its port (`null` when it does not), and a route that refuses falls back
instead of doing nothing.

**The pane is an override layer, not a copy of the row.** One slider click used to write all nine
settings, freezing the row's other values into the pane's file — after which an edit to
`cordis.patch.yml` silently stopped taking effect. Only the keys the pane changed are stored now, and a
value that does not parse is refused instead of being stored as its fallback. A write that fails, or a
Reset that cannot clear the file, answers 500 and is reported; it used to throw out of the HTTP callback
and take the harness process with it.

**Two smaller honesty fixes.** `contextBudget: true` published a budget of one token (YAML spells `yes`
as a boolean; `Number(true)` is 1), and the auto-fetch shared a single 120 s deadline between the 65 MB
download and its `.sha256` — on a slow line the hash request aborted on the first tick and the exe
installed unverified. Each request has its own deadline now, and a hash that cannot be fetched refuses
the install instead of passing silently.

**The settings door keeps its own lock.** Plugin routes are not cookie-gated, so `POST /agents-notch/settings`
now requires `content-type: application/json` and refuses another page's `Origin`/cross-site
`Sec-Fetch-Site` — a `text/plain` body is a "simple request" no browser preflights.

**A waiting row says why it waits.** `approval.reason` was parsed and discarded, so the row led with the
tool's name (`pwsh` for every approval). It is the row's tail now — `wants to delete 14 files under sr…`
— capped so the title stays readable.

**Full-screen hiding is per monitor, and the desktop is not a full-screen app.** The shell window's DWM
bounds are exactly the monitor rect, so pressing Win+D or clicking the desktop hid the notch; cloaked and
zero-area windows are skipped too, and a game on display 1 no longer hides the notch on display 2.

The window also reads its style bits with a real `GetWindowLongPtrW` instead of zeroing `GWL_EXSTYLE` to
learn it, which briefly stripped `WS_EX_LAYERED` from the live window.

## 0.1.6 — double-click to open the app, and a notch that comes back

The README opens with a hero, an animation of the real motion and a sheet of the four states, all drawn by the
window itself: `--record <dir>` writes the motion frame by frame on its own clock, `--open-at` /
`--close-at` unfold the board on a schedule, and `tools/readme-media.py` lays the renders out. The
reference sections (every key, the manual install) fold away, and a short 中文 section closes it.

The demo is an animated WebP (full colour, 849 KB at 1100 px); `dist/media/demo.gif` is built for places that
only take a GIF. A GIF has 256 colours a frame, and the wallpaper's gradient spent them on banding.

`npm test` runs again on Node 25, which no longer accepts a directory for `node --test`: the script
passes a glob instead.

**Double-click to open the app.** Double-clicking the tab or a row on the board brings DeepSeek
Harness to the front through its own `dsh://open` link, and the tray menu has *Open DeepSeek Harness*.
A single click still pins a row. The app registers no link that opens one session, so the double-clicked
row stays pinned on the bot instead.

**The notch comes back.** The supervisor used to give up for good after three quick deaths in a row,
which left no notch until the harness restarted. Measured here: a morning of killing the window to
render stills tripped it. It now keeps retrying every 5 minutes after the quick tries, and says
`window-giveup` once per streak. A window that dies of an exception appends the exception to
`window-crash.log`, and the host quotes it in `window-exit` as `crash`. Until now the only evidence
was an exit code.

**Clicking a session no longer folds the board away.** Pinning a session whose board has a different
shape (one with a context count and one without) rebuilds the board, and the rebuilt board inherited
the old one's cached "hit-testable" flag and transforms. It stayed invisible to the mouse while open,
so the pointer counted as having left and the board folded under the cursor. A rebuild now resets both.

## 0.1.5 — the finished face is the `^^`, with its ring

Two symptoms, one bug: when the transition key became `id|status`, the pose branches kept comparing it
to a bare state name, so **every** settled state matched nothing and the big bot fell back to the neutral
idle face — a finished session showed half-open eyes and no state ring, at any moment.

Fixed by keeping the two questions apart: `FrontKey` (`id|status`) decides a TRANSITION, `FrontStatus`
decides the POSE. A finished session now rests in the `^^` face the design calls for, and the ring is
seeded on the first painted frame instead of fading up from nothing (a still, and the first 250 ms, used
to look ringless).

Found by rendering the same fixture from the old and the new binary and comparing the two pictures.

## 0.1.4 — the pane's browser half actually reaches the app

0.1.3 declared `exports` twice. JSON keeps the LAST block, and the second one had no `./client`, so
`dsh-client-modules` could not resolve the browser half: the package installed, the plugin mounted, the
host route answered — and Settings → Agents Notch simply never appeared. No error anywhere. npm packed
it happily and `npm publish` reported success.

Fixed, and now guarded by `test/manifest.test.mjs`, which counts `"exports"` blocks **textually** (a
JSON parser hides the mistake by design) and checks that `./client`, `main`, the bundle patch, the
installer and every promised file resolve, and that the version is a plain x.y.z because
`tools/publish-release.ps1` derives the release tag from it.

## 0.1.3 — a Settings pane

Settings → **Agents Notch** now exists, with every knob the row config has: size, anchor, display, rows,
opacity, motion, the three text switches, and a Reset. It writes
`state/agents-notch/settings.json`, clamped by the same rules as the YAML and beating the row; the
window applies a change on its next tick, so nothing restarts.

Why a file instead of the row itself: a browser pane cannot safely round-trip `cordis.patch.yml`, a
file other sessions also edit, and one bad indent there takes the whole machine's configuration down.
The pane owns one small JSON file beside the state; the row stays the human's file.

Also here: the transition that drives the face motion is keyed on the front session's `id|status`
rather than its status alone, so switching between two finished sessions replays the finish motion
instead of nothing — and the transition is logged (`pose — front a|done -> b|done`).

## 0.1.2 — the finish animation plays when you switch to a finished session

The motion keys on the front session's identity (`id|status`), not on its status alone. Switching
from one finished session to another is a transition the window now sees, so the finish motion runs
again instead of nothing happening — `pose — front session-a|done -> session-b|done` in the window
log, which is new too: "the animation did not play" is otherwise unanswerable from outside the
process.

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
