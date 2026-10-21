# Agent overlay for Windows — plan

A Grok-Bot-style always-on-top status pill for DeepSeek Harness: which sessions are
**working**, which **need you**, which are **done**, on top of every window except a
full-screen game.

Status: **plan only — nothing built.** Everything below marked *measured* was read out of
this machine's installed harness or run here; everything marked *predicted* is still to be
verified in phase 0.

---

## 1. What the reference app does, and what has to change on Windows

The screenshot is a macOS notch app: a pill hangs off the top-centre notch, expands into a
small board of task rows with a state glyph per row, and shows a three-word status
(`Idle / Working / Sleep`) at the top.

Windows has **no notch and no reserved top strip** — the top-centre of the screen is ordinary
window space. So the Windows version is a *floating* pill: frameless, transparent,
always-on-top, parked at the top-centre of a monitor, expanding downward on hover (or on an
attention event). The state vocabulary maps like this:

| Grok-style state | Here |
|---|---|
| `Working` | a session whose agent is in a turn (`agents.get(id).status === "running"`), incl. tool calls |
| `Needs you` | a pending approval (`approval/asked` with no `approval/decided`) or a pending `ask_user_question`, or a turn that ended `blocked` |
| `Done` | last turn ended `completed` and the human has not looked at that session since |
| `Error` | last turn ended `error` / `max-tokens`, or `interrupted` |
| `Idle` / `Sleep` | live session with no turn running; *sleep* = nothing has happened for a while → the pill dims to a sliver |

## 2. What this harness can and cannot do (measured)

These four facts decide the architecture; all were verified here today.

1. **A DSH plugin cannot create an OS window.** The Electron shell's `main.js` has **zero**
   occurrences of `alwaysOnTop` / `topmost` / `setShape` / `WS_EX`, and it has no IPC channel
   that would let anything set window bounds or z-order. Plugins do not even run inside
   Electron: the shell spawns the host with `ELECTRON_RUN_AS_NODE: "1"`, so `require("electron")`
   in a plugin is not the API. → *the window must be a separate process.*
2. **A plugin *can* spawn that process.** The packaged plugin `anneheartrecord/dsh-desk-pet`
   does exactly this (a Python/Tk window launched from a Cordis host plugin), and this machine
   already ships a profile plugin that drives a PowerShell + P/Invoke helper
   (`dsh-computer-use-win` → `scripts/windows-uia.ps1`). Two working precedents.
3. **The host plugin can see the truth, cheaply.** `session/event` (subscribed with
   `{ global: true }`) delivers every session's events, and the full vocabulary is known:
   `turn/start`, `turn/end` (reasons `completed | aborted | blocked | error | max-tokens |
   interrupted | forked`), `approval/asked`, `approval/decided`, `step/start|end`,
   `tool/call`, `tool/result`, `session/title`, `working-directory/change`, `subagent/*`,
   `team/*`, `goal/change`, `job/registered|settled`. Plus `ctx.agents.list()` →
   `[{ id, status: "idle"|"running", inbox: { nextTurn, nextStep } }]`, `ctx.sessionQuery
   .listSessions()` → `{ header, live, persisted }`, and `sessionQuery.readTitleSnapshots(ids)`.
   This is *better* than the desk-pet's heuristic (it scrapes session-log tails for keywords).
4. **The overlay cannot use the harness API, but can read a plugin's route.** The server is
   launched with `--port 0`, and `/`, `/connect`, `/api/*` and the websocket mux require a signed
   cookie issued from `/connect?token=…` once per launch — an external process cannot obtain one.
   Routes a plugin registers are **not** behind that gate (measured: two of this machine's
   plugins answered `200` unauthenticated). → *a plugin is the bridge: it owns the facts and
   serves them on its own route; the port is discovered from a file it writes.*

## 3. Architecture

Three pieces, each with one job. This is the same shape as the proven desk-pet plugin, but
inverted: **the plugin is the authority** and the window is a dumb renderer.

```
┌──────────────────────────── DSH host process ────────────────────────────┐
│ plugins/agent-overlay/lib/index.js   (host plugin)                        │
│   • session/event {global:true} + agent/status → per-session state        │
│   • ctx.agents.list(), turnBoundary & userQuestions projections,          │
│     sessionQuery (titles, live, corpus) — the same facts the GUI uses     │
│   • serves GET /agent-overlay/state            (ungated plugin route)     │
│   • serves POST /agent-overlay/{focus,dismiss,config}                     │
│   • writes state/agent-overlay/endpoint.json {port,pid} (port is 0 = random)│
│   • spawns/supervises AgentOverlay.exe with --parent-pid                  │
└──────────────────────────────────────────────────────────────────────────┘
        │ GET /agent-overlay/state (250 ms poll or SSE)   ▲ POST intent
        ▼                                                 │
┌─────────────────────────┐          ┌───────────────────┴─────────────────┐
│ AgentOverlay.exe        │          │ lib/client.js (client plugin)        │
│ .NET 9 WPF, ~15 MB      │          │ • ctx.uiWorkspace.openSession(id)    │
│ • pill + expanded board │          │ • settings UI + "Open overlay"       │
│ • QUNS/fullscreen hide  │          │ • reports the GUI's active session   │
│ • tray icon, position   │          └──────────────────────────────────────┘
│ • raises DSH on click   │
└─────────────────────────┘
```

**The feed is an HTTP route, not a file.** Measured: routes a plugin registers through
`ctx.webServer.register` are **not** behind the cookie gate (the gate is only `/api/*` and
`/connect`), so `/session-finished/api/status` and `/roblox-workstation/summary` both answered
`200` with no credentials. The server is launched with `--port 0`, so the port is random per
boot — hence `endpoint.json` beside the state file: the overlay reads it, watches it for
changes, and reconnects on a new port after a restart. A file-only fallback (last state) keeps
the pill from going blank during a restart.

**Why not the alternatives**

| Option | Verdict |
|---|---|
| Patch the Electron shell | Not viable: `app.asar` under the install dir, replaced by every update; no plugin hook. |
| Separate Electron app | Works, but ~150 MB runtime + its own updater for a 40-line pill; still needs the same Win32 detection. |
| Tauri/WebView2 | 3–10 MB and HTML/CSS, but needs a Rust toolchain and code-signing/AV friction. |
| In-page pill in the DSH window | Zero new process, but invisible when the window is behind another app and gone when hidden to tray — fails the entire premise. |
| **.NET 9 WPF exe** | *Measured:* `dotnet` 9.0.101 is installed and a hand-written `<UseWPF>true</UseWPF>` project builds offline in 23 s. Native P/Invoke for every Win32 call, small, no runtime install. |

## 4. Data model — `GET /agent-overlay/state`

Served by the host plugin (ungated, ~4 Hz on change and at least once per second), mirrored to
`state/agent-overlay/state.json` atomically (tmp + rename) so a restart does not blank the pill.
`state/agent-overlay/endpoint.json` carries `{ port, pid, wallMs }` for discovery.

```jsonc
{
  "schema": 1,
  "wallMs": 1789999999999,          // staleness + heartbeat
  "hostPid": 4242,
  "hostBusy": true,                  // any agent running, or inbox/jobs active
  "sessions": [
    {
      "id": "session-db65f92b-…",
      "title": "Argon 0/4 on laptop — fixed",
      "project": "my-game",   // basename of cwd
      "cwd": "C:\\\\code\\\\my-game",
      "status": "working",            // working|attention|done|error|idle
      "since": 1789999990000,          // when this status began (for the elapsed timer)
      "activity": { "kind": "tool", "label": "roblox_verify" },
      "turn": 12, "step": 3,
      "approval": { "toolName": "pwsh", "reason": "commits to git" },
      "subagents": 2,                  // live child sessions, parentSession === id
      "team": { "members": 4, "running": 1 },
      "goal": { "active": true, "label": "ship the overlay" },
      "jobs": 1,
      "finished": false,               // from session-finished state/manual.json
      "parentId": null                 // subagents are nested, not listed
    }
  ]
}
```

Rules the host enforces:

- **Only top-level sessions** (`parentId === null`) become rows; child sessions are counted
  into `subagents`. Team members are counted separately.
- **Hidden by default**: any session with `finished: true` (reuses the human's own `/finish`
  marks from `state/session-finished/manual.json`, so the overlay and the sidebar agree), any
  session not live and untouched for `staleAfterHours` (default 12 h), and any session whose
  workspace is in `hiddenProjects`.
- **Ordering**: attention → error → done → working → idle; within a group, most recent event
  first. The list the pill draws is the same list, truncated to `maxRows` (default 6).
- **"Done" clears** when the session is opened in the GUI (the client plugin POSTs an ack) or
  after `doneTtlMinutes` (default 30).

**Where each field comes from** (nothing here is invented — these are the same facts the GUI
sidebar reads):

| Field | Source |
|---|---|
| `title` | `sessionQuery.readTitleSnapshots([id])`, kept fresh from `session/title` |
| `project` | `ctx.workspaceRegistry.list()` → `title` matched on `header.cwd`; fallback = last path segment |
| `working` | `ctx.agents.get(id)?.status === "running"` **or** the `turnBoundary` projection's `openTurnStartSeq !== null` |
| `activity` | last `tool/call` name + its `step`; ticks on `step/start` |
| `approval` | an `approval/asked` with no paired `approval/decided` (always turn-enclosed) |
| questions | the `userQuestions` projection: `active.length > 0` |
| `error` / blocked | last `turn/end` reason (`error`, `max-tokens`, `blocked`, `interrupted`) |
| `done` | last `turn/end` reason `completed` |
| `since` / `updatedAt` | turn start time; `updatedAt` from `sessionListMetadata.lastPromptAt` (human prompts only) |
| `subagents` | sessions with `header.origin === "subagent"` and `parentSession === id` |
| `jobs` | `ctx.get("jobs").list(id)` where status is `running`/`stopping`|
| `finished` | `state/session-finished/manual.json` + the close-out convention (there is **no** session-finished event) |

## 5. The window — `plugins/agent-overlay/desktop/`

### Look

Rendered, not described: [`designs/`](designs/README.md) holds the locked design and its
variations, shot in WPF at 2× over a simulated light desktop — the
[board in every state](designs/V-states.png) (the approved one), the
[board on a desktop](designs/V1-board.png), the [grid](designs/V2-grid.png) and
[dense](designs/V3-dense.png) variants, the [stack at every count](designs/V-stack.png),
the [bot's motion frames](designs/V-motion.png) and the
[identity palette](designs/V-identity.png), plus the [tab](designs/V-tabs.png).

**The identity area counts running sessions.** The front orb is the session on screen; each
smaller orb behind it is another session that is running right now (2–3 → one or two bubbles,
4 → three, 5+ → two and a `+N`). This is the one element that answers "how much is in flight"
without reading a list — and it is why the board still works when the window is only 560 px wide.

**Every session has its own colour, and the bot wears it.** A stable hue per session (derived
from its id) is the orb's *rim* — a 2.5 px contour with a soft glow — so the stack reads as
several different sessions rather than three copies of one, and you can tell whose bot you are
looking at without reading a word. State stays a separate signal so the two never lie to each
other: the rim says *which session*, the badge and the chips say *what it is doing*. Identity on
the chips was tried and reverted — it dims the state reading, which is the chips' whole job.

**The bot is alive.** One `Orb(size, state, identity, blink, lookX, lookY, rim)` drives the
drawing; the render loop only moves those last four numbers:

| what | timing | rule |
|---|---|---|
| blink | 140 ms, every 4–7 s | eyes squash to a line and back; sometimes a double blink |
| drift | 220 ms ease | the eyes follow the pointer, 2–3 px, clamped to the face |
| breathe | 3.2 s sine | the orb scales ±1.5 % |
| rim | 2.4 s sine | the identity glow breathes between `.3` (idle) and `1` (attention) |
| attention | 1.2 s | the rim goes to `1` and the badge pulses once |
| done | 400 ms | the badge settles to green, the orb relaxes to `rim .45` |
| new session | 220 ms | a new bubble slides in behind the stack |

The motion frames in [`V-motion.png`](designs/V-motion.png) are rendered by that same
function, so a still and the live window cannot drift apart.

```
        ▁▁▁▁▁▁▁  ← notch tab: 4 dots, black, sticks to the top edge
   ╭──────────────────────────────────────────────╮
   │  ◕   Argon 0/4 on laptop — fixed    ● uiAnim  │   ◕ identity orb
   │      my-game · 12m          ● swords  │   task lines (current
   │  ◉ Verify Argon 0/4 on the laptop   ◐ .dsh    │    one lit)
   │  ○ Write the overlay plan           ○ knife   │   chips: one per other session
   ╰──────────────────────────────────────────────╯
```

The rules that make it read as one object rather than a dashboard:

- **An identity orb, not a header.** 72 px (64 when stacked), white radial gradient, two
  rounded eyes, a 21 px state badge at its lower-right, and the session's identity colour as a
  2.5 px rim with a soft glow. The session title sits beside it; there is no title bar, no
  "AGENTS" eyebrow, no column of timestamps, no separators.
- **Prose, not rows.** The current session's three task lines are plain text with tiny state
  glyphs; the active one is the only bright text. A session's state is a colour, never a word
  in a column.
- **Chips for everyone else.** `fill = tone @ 7 %`, a 6 px dot, the project name in the tone.
  No border, no badge, no count.
- **Material, not a box.** `#161618` at 98.5 %, 26 px radius, a *catch-light* — a 1 px white
  gradient along the top edge that fades at the corners — and one soft shadow
  (blur 90 / depth 30 / 34 %). A border instead of a catch-light, or a tight shadow, reads as
  a web card on a light desktop. (Measured: this pass produced a khaki 1 px outline because
  `#FFFFFF14` is `#AARRGGBB` — opaque yellow-white — not 8 % white. Use `#14FFFFFF`.)
- **Colour is the whole vocabulary:** working `#6AA8FF`, needs you `#FFC15E`, done
  `#4ADE80`, failed `#FF6B6B`, idle `#8E8E93`.
- Motion: the tab's dots breathe while working; a chip that *becomes* attention pulses once;
  the card grows out of the tab in ~160 ms.
- Hover expands after ~120 ms; leaving collapses after ~400 ms (so a stray cursor does not
  flicker it).

### Window technique (from the Win32 research)

- `WindowStyle=None`, `AllowsTransparency=True`, `Background=Transparent`, `Topmost=True`,
  `ShowInTaskbar=False`, `ShowActivated=False`, `Focusable=False`.
- After `SourceInitialized`: `SetWindowLong(GWL_EXSTYLE, … | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
  | WS_EX_LAYERED)` and `SetWindowPos(HWND_TOPMOST, …, SWP_NOACTIVATE)`; re-assert topmost
  about once a second (z-order is not a guarantee).
- **Focus theft is the failure that matters**: `ShowActivated=False` + `WS_EX_NOACTIVATE`, and
  never `Activate()` — a game must not lose focus when the pill appears.
- `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` before any window; place with
  `workArea` of the chosen monitor (excludes the taskbar), not `bounds`.
- No `WS_EX_TRANSPARENT` on the card itself (its rows must be clickable). Hover is detected by
  polling `GetCursorPos` against `GetWindowRect` at ~30 Hz while collapsed, so no mouse hooks.
- `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` (Win10 2004+) so the pill does not
  appear in Teams/Zoom/OBS captures — *predicted* to work; verify in phase 0.

### Hiding rules ("unless I'm playing games")

Polled at 2 Hz in the same window:

1. `SHQueryUserNotificationState()` → hide on `1 NOT_PRESENT`, `2 BUSY`, `3 RUNNING_D3D_FULL_SCREEN`,
   `4 PRESENTATION_MODE`; show on `5`, `6`, `7`. **Modern games report `2`, not `3`** (Windows
   full-screen optimizations), and Microsoft's own definition of 2 is "a full-screen
   application is running" — so 2 must hide.
2. Optional disambiguation, config `fullscreenMode: "any" | "gamesOnly"`: resolve
   `GetForegroundWindow` → `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` → compare with
   `MonitorFromWindow` + `GetMonitorInfo(rcMonitor)` within ±2 px. A maximised IDE fails this
   (its extended frame stops at the work area), a browser F11 covers the monitor and passes;
   `gamesOnly` would then keep the pill up over a full-screen video.
3. Tray menu "Pause overlay for 30 min / until I say so", and `Esc`-less: the pill is never
   click-through, so it can always collapse by hovering away.

## 6. Interaction

| Gesture | Action |
|---|---|
| Hover pill | expand; hover out | collapse |
| Click a session row | `POST /agent-overlay/focus {sessionId}`; raise DSH (`dsh://open`, else restore the window by title); the client plugin sees the pending intent, calls `ctx.uiWorkspace.openSession(id)` and acks → row goes idle |
| Click the amber count | expand filtered to attention rows |
| Middle-click a row / ✕ on hover | dismiss ("done") until the session changes again |
| Right-click the pill / tray | menu: position (top-centre, top-right, follow monitor), size, opacity, hide rules, "Open DSH", "Quit overlay" |
| Drag the pill | move it; persists `right/bottom`-style anchor in `state/agent-overlay/prefs.json` |

The intent hop through the client plugin is what makes "click → that exact session" possible;
there is no deep link for a session id — the shell only handles `dsh://open` (focus the window).

## 7. Files

```
plugins/agent-overlay/
  package.json                 name + cordis metadata
  lib/index.js                 host: wiring, routes, spawn/supervise, endpoint.json
  lib/state.js                 pure reducer over events + projections (tested without the harness)
  lib/collect.js               the polling side: agents.list(), sessionQuery, workspace titles
  lib/client.js                client: intent poller, openSession, settings panel
  desktop/AgentOverlay.csproj  net9.0-windows, UseWPF, no NuGet
  desktop/App.xaml(.cs)        tray + single instance mutex
  desktop/OverlayWindow.xaml   the card/pill, DataTemplates per status
  desktop/Win32.cs             P/Invoke: topmost, QUNS, DWM bounds, monitor, capture-exclusion
  desktop/StateFeed.cs         endpoint discovery + HTTP poll (SSE upgrade later), file fallback
  desktop/build.ps1            dotnet build -c Release → bin/AgentOverlay.exe
  test/state.test.mjs          reducer: turn/approval/error/done/hidden cases
  README.md                    install, config keys, the traps below
~/.dsh/cordis.patch.yml        one `insert:` row (id: agent-overlay, config: …)
docs/ai/agent-overlay/         (moved here: this plan + the renders live in plugins/agent-overlay/)
```

## 8. Build order, with the evidence each phase must produce

| Phase | Work | Proof it is done |
|---|---|---|
| **P0 — spike** (½ session) | WPF window: transparent rounded pill top-centre, topmost, no focus theft, QUNS hide, tray, drag; static fake data | a screenshot with a game (or a full-screen video) in front showing the pill hidden, and one with it visible; `GetForegroundWindow` of the game still focused after the pill appeared |
| **P1 — state machine** (host) | `lib/state.js` + `lib/index.js` + `lib/collect.js`, the route, endpoint.json, spawn/supervise | `node --test plugins/agent-overlay/test/*.test.mjs` green; `GET /agent-overlay/state` curled from outside the app shows a real session flipping working→done→attention; a *second* harness process does not spawn a second window |
| **P2 — client half** | intent route, poller, `openSession`, settings, row for the overlay | clicking a row in the (stub) pill switches the GUI to that session |
| **P3 — real pill** | real rows, colours, motion, done/ack, dismissal | screenshot of all five statuses simultaneously, produced by driving the harness (`ask_user_question` for attention, a long `pwsh` for working) |
| **P4 — polish** | tray menu, position memory, capture exclusion, autostart, config docs | restart the app: overlay reappears at the remembered spot; hidden while a game runs; absent from a screen capture |

Config keys (all in the row's `config:`): `enabled`, `maxRows`, `staleAfterHours`,
`doneTtlMinutes`, `hiddenProjects`, `fullscreenMode`, `excludeFromCapture`, `autostart`,
`opacity`, `anchor`.

## 9. Traps this must not walk into

- **The plugin-row cache key is the row's `name` URL, not its id.** A behavioural fix cannot be
  landed by editing the file a row already points at; rename the entry file (as
  `roblox-design` did with `lib/design.js`) or restart the app.
- **A new row must be wrapped in `insert:`**, or it is silently skipped with only a loader
  warning. A row appears live; the *window process* it spawns is what actually shows anything.
- **`spawn` reports failure asynchronously** — treat `error` and an immediate `exit` as
  failures and say why (the desk-pet plugin pipes the child's stderr for exactly this reason).
- **Two harness processes** (desktop + a `dsh web` window, or another profile) would both spawn
  an overlay and both serve a feed on different ports. Guard with a named mutex in the exe, and
  let the *newest* `endpoint.json` win; the older host must not fight it.
- **Stale state must not look alive**: `wallMs` age > 6 s or a dead `hostPid` → the pill shows
  "DSH not running" / hides, never the last happy frame. The port **changes on every restart**
  (`--port 0`), so `endpoint.json` must be re-read, not cached.
- **`listSessions()` is not failure-isolated** — one corrupt log throws for the whole corpus
  (measured: 204 of `session-finished`'s 210 read failures were one bad log). Wrap it, back off,
  and distinguish "nothing live" from "I don't know", or the pill will empty itself on a bad day.
- **`live` is not busy.** A session can be attached and idle, or persisted and still "recent".
  Busy is only `agents.get(id).status`.
- **There is no "session finished" event.** `/finish` is a plugin convention; reuse
  `session-finished`'s marks rather than inventing a second definition.
- **Never decode the `sessions/` directory slug or `zstdDecompressSync` a whole log** — the slug
  is not reversible (projects moved OneDrive → `E:\assets`, both spellings exist), and Node
  decodes only the first zstd frame of a concatenated log. Neither is needed: the plugin reads
  through `sessionQuery`.
- **Do not hold Studio's playtest slot** — this work is not Roblox work; no `roblox_studio_hold`
  is needed and no playtest may be started for it.

## 10. Decisions I need from you

1. **Placement**: top-centre notch (closest to the reference, may cover a maximised app's tab
   strip) vs draggable-anywhere with top-right default vs dock near the taskbar? *Recommend:
   top-centre + draggable, remembered.*
2. **Hide rule**: hide over *any* full-screen app (games, F11 video, movie players — calm, but
   disappears during a full-screen video) vs games only (needs a process/foreground heuristic)?
   *Recommend: any full-screen app, with a tray "pause" toggle.*
3. **How much to show**: only sessions from the projects you are actively in (the workspace
   list), or every live session on the machine (several projects + ad-hoc `dsh` windows)?
   *Recommend: every live session, grouped by project, capped at 6 rows.*

