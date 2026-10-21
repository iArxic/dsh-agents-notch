# The contract between the two halves

**Frozen — schema 3.** The host half computes these facts; the window half draws them where the
human is looking. Neither half may change a field here without the other changing in the same
commit, and both are reviewed against this file. Read `docs/INTERNALS.md` for *why* any of it is
shaped this way; this file is only *what*.

## Who writes what

```
DSH host process                                  separate Windows process
┌───────────────────────────────────┐             ┌──────────────────────────┐
│ lib/host.js                       │ state.json  │ desktop/bin/AgentsNotch  │
│  • session/event {global: true}   │────────────▶│  • the tab + the board   │
│  • serves GET /agents-notch/state │  340 ms     │  • reads state.json      │
│  • spawns + supervises the exe    │◀────────────│  • writes quit.json      │
└───────────────────────────────────┘  quit.json  └──────────────────────────┘
```

- The **state file** is written by the host, read by the window. Written to a temp name and renamed
  into place, so a reader never sees half a file. Default path
  `$DSH_HOME/state/agents-notch/state.json` (`DSH_HOME` from the environment, else `~/.dsh`); the
  host passes it to the window as `--state <path>`, and the window reads **nothing else**.
- The window reports exactly one thing back: `quit.json`, next to the state file, `{"wallMs": <ms>}`
  written when the human picks *Quit overlay* from the tray. The host does not spawn a window again
  for **one hour** after a quit whose `wallMs` is newer than the host's boot. This exists because
  the host's supervisor otherwise resurrects a window the human just dismissed.
- Anything else the two need belongs in this file first.

## `state.json`, schema 3

```jsonc
{
  "schema": 3,
  "plugin": "dsh-agents-notch/host",
  "wallMs": 1790000000000,        // host clock at write time; the window's liveness heartbeat
  "hostPid": 4242,

  "retire": "route",              // "route" | "timer" — how a settled (done/error) row goes away

  "context": {
    "budget": 1000000,            // tokens the route's window holds, or NULL when the host does not know
    "hotAt": 0.5                  // fraction where the bar turns amber; the compaction trigger
  },

  "settings": {                   // the human's, from the plugin row's config; the window applies them
    "scale": 1.0,                 // 0.75 … 2.0, the size of the whole surface
    "screen": "primary",          // "primary" | "cursor" | "1" | "2" …  (1-based, as Windows numbers them)
    "anchor": "top-center",       // "top-center" | "top-left" | "top-right"
    "maxRows": 6,                 // 3 … 10, how many sessions the board may show
    "hideFullscreen": true        // hide under a full-screen app
  },

  "summary": { "status": "working", "working": 2, "attention": 1, "done": 1, "error": 0, "total": 4 },

  "sessions": [{
    "id": "session-db65f92b-…",
    "title": "Argon 0/4 on laptop — fixed",   // "" when the harness has not titled it yet
    "project": "blindKnifeArgon",             // workspace title, else the cwd's last segment
    "cwd": "C:\\Users\\kubav\\Projects\\blindKnifeArgon",
    "status": "working",                      // working | attention | done | error
    "since": 1790000000000,                   // when it entered `status`
    "activity": { "kind": "tool", "label": "roblox_verify" },   // null before the first tool
    "approval": { "toolName": "pwsh", "reason": "…" },          // null unless blocked on the human
    "steps": 7,                               // tool calls this turn
    "identity": "teal",                       // teal | violet | coral | pink | lime — the session's colour
    "origin": "subagent",                     // "subagent", or "" for one the human opened
    "parentSession": "session-db65f92b-…",    // "" for a top-level session
    "contextTokens": 412034                   // 0 until the session has answered once
  }]
}
```

### The five rules that make it honest

1. **A percentage needs a budget, and a budget is not a guess.** `context.budget` is `null` whenever
   the host cannot establish the route's real window. With `null` the window shows the token count
   (`412k`) and **no bar and no percentage** — a percentage against a guessed denominator is a lie
   that looks like information. `budget` never defaults to a plausible number.
2. **A row is a property of a session, never of an event.** Nothing on a row decays on a timer. A
   row exists because something happened to that session — it is working, it needs the human, it
   failed, or it finished and the human has not closed it out. `retire` says *how* a settled row is
   allowed to go away when the harness's own close-out verdict is unavailable (`"timer"`), and the
   README says so.
3. **`wallMs` is the heartbeat.** The window compares it to its own clock. More than **4 s** stale
   (12 missed writes) means the host is gone or wedged: the tab keeps its position but shows the
   unknown state — never the last summary as though it were current.
4. **`settings` belong to the host, not to a file the window invents.** The window applies what it
   is given, clamps it to the ranges above, and remembers nothing on disk.
5. **Nothing is invented to fill a field.** `""`/`0`/`null` all mean "not known yet", and the window
   draws nothing for them rather than a placeholder.

## Who owns which field

| field | written by | read by | note |
|---|---|---|---|
| `schema`, `plugin`, `wallMs`, `hostPid` | host | window | `wallMs` drives rule 3 |
| `retire` | host | window | the tab's tooltip names it when `"timer"` |
| `context.budget` | host (row config) | window | `null` = unknown, never guess |
| `context.hotAt` | host (row config) | window | ignored when `budget` is null |
| `settings.*` | host (row config) | window | window clamps, never persists |
| `summary` | host | window (tab) | `status` is the tab's dot |
| `sessions[].*` | host | window | one row each |
| `quit.json` | window | host | see above |

## Lifecycle

| event | host | window |
|---|---|---|
| harness boots, row applies | `apply()` writes state, spawns the exe if not already running | starts, polls `--state` |
| harness quits | child is killed with the parent (`--parent-pid` watch) | exits |
| the exe dies on its own | logs `window-exit`, respawns after 15 s, at most 3 tries in a row | — |
| the human quits from the tray | stops respawning for an hour, logs `window-quit` | writes `quit.json`, exits |
| the human is running a full-screen app | keeps writing state | hides itself, keeps polling |

## Rules of the road for both halves

- Node builtins only in `lib/` (`node:fs`, `node:path`, `node:http`, …). No dependencies: this has
  to install on a stranger's machine, and a plugin that pulls a tree is a plugin that breaks.
- No path in this package may name this machine. The exe is found relative to the package root.
- Tests are `node --test` files under `test/`, and they must pass with no harness running.
