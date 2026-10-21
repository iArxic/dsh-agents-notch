# Agents Notch — the engineering record

Every shape in this plugin was paid for with a measurement. This file is that record: the harness
facts the architecture follows from (§4), the data model (§5), the window's Win32 detail (§6), and
the traps (§9), each of which cost a day of the development this file came from.

[`CONTRACT.md`](CONTRACT.md) is the authoritative interface between the two halves. Where this
file and the contract disagree, **the contract wins** — this file explains *why* it says that.

> **On the path names below.** The notch was first built inside one person's harness home as
> `plugins/agent-overlay/`, and the history sections (§2, §9, §10) name the files as they were
> then: `lib/host-v9.js` is now `lib/host.js`, `lib/session-state-v6.js` is now `lib/state.js`,
> and `AgentOverlay.exe` is now `AgentsNotch.exe`. The pile of versioned file names in §10 is not
> clutter to tidy: a DSH plugin row is imported by URL with no cache-bust, so a behavioural fix
> lands as a **new file plus a new row**, never as an edit in place. The history is why that rule
> exists.

---

## 1. What it is

A small floating panel, always on top of everything, that shows what the agent sessions on this
machine are doing — which are **working**, which **need you**, which are **done** — and hides
itself while a full-screen app (a game) has the display.

The design came from a macOS notch app and was rebuilt around its bones: an **identity orb**
that carries the session, **prose task lines** instead of table rows, and **coloured chips**
instead of a status column. The locked design is `designs/V-states.png` (the board in all five
states). The human approved it: *"this design is PERFECT"*.

## 2. The development log

The notch was built over three days of iteration, and these entries are kept because they are *why*
the code looks the way it does: each one is something that was wrong, the measurement that proved
it, and the shape that replaced it. Read §9 for the same knowledge as a checklist instead of a
narrative.

**What P1 measured (2026-10-19):** the plugin row lands **live** — no harness restart — and
`GET /agent-overlay/state` answers **200** on the host's own port (62254 here), which confirms the
route is not cookie-gated. The state file at `~/.dsh/state/agent-overlay/state.json` is rewritten
every 500 ms with the sessions that earned a row; two were live while this was written, both
`working`, with their real activity (`pwsh`, `read`) and their identity hues.

**The 2026-10-20 pass — "if an agent finished right now he disappears completely off the notch":**

* **Root cause, proven from the harness source.** `turn/end` carries
  `reason: { kind: "completed" }` — a discriminated OBJECT (`TurnEndReasonMap`), built in the
  harness's own loop as `turnEnds = { kind: "completed" }`. The reducer tested it as a string, so
  the reason was always `""` and **every** turn end fell through to `idle`: the row was dropped in
  the same millisecond the agent finished. The unit tests passed because the fixtures fed it
  strings the harness never sends. Fixed in `lib/session-state-v3.js` (`reasonKind` accepts both
  shapes), and the fixtures now use the real shape.
* **A finished row no longer expires.** `doneTtlMinutes` is gone. The only thing that retires a
  `done` or `error` row is the human closing the session out — the sidebar's ✓ from
  `plugins/session-finished` — which the host asks for over HTTP on `ctx.webServer.port`. The
  human's own marks (`state/session-finished/manual.json`) are read as well, win in both
  directions, and keep the tick working if that plugin is not mounted.
* **Order changed to match:** attention → error → **working** → done. A row that never expires
  must not outrank live work, or a fortnight of unticked finished sessions pushes the working
  agents off a six-row board.
* **Latency:** an event now wakes the writer immediately (coalesced, 120 ms) and the 500 ms timer
  is only the heartbeat; the window polls at 150 ms instead of 300. Worst case went from ~800 ms
  to well under 250 ms for the one event this display exists for.
* **The tab says "finished".** With a finished row present it reads `2 working · 1 finished`
  instead of hiding it behind a working count, and a finished bot's green aura now actually
  reaches the eye (measured on a 4× zoom of the live tab).
* **The tab is sized by its own words** instead of a fixed 220 px, so a longer summary is not
  clipped and a short one is not a pill full of nothing.
* **Silence is now visible:** if the state file stops refreshing for 5 s the tab says
  `host offline` and the bots go, instead of showing yesterday's work as if it were live.

**The second 2026-10-20 pass — the bot itself:**

The rule the human settled on, after a design round with four alternatives (bot faces per row,
a crew strip, a column of finished bubbles, a grouped list — all rejected, the existing ✓/● lines
are what he wanted):

> **The ring means "settled". The halo means "state". Working gets no ring at all.**

* `working` → halo only, no ring. Nothing is asked of you, so nothing is drawn around it.
* `finished` → **green** ring + green halo.
* `needs you` → **orange** ring + amber halo, and the ring *pulses* — this is the one that has to
  pull his eye across the screen.
* `failed` → red ring + red halo.

The ring is two passes (a soft bloom and a crisp line) so it glows rather than reading as a neon
tube, and it sits outside the identity rim with air between them so "which session" and "what
state" never merge into one band of colour.

The eyes are now animated by state, built in `desktop/Program.cs` as a pose sampled per frame:

| state | motion |
|---|---|
| any | a slow wander under the pointer-following, and a blink every 1.9 s — every other one twice |
| finishes | shut → **`^ ^` squint** → settle into a half-lidded rest. Both eye-shape changes happen BEHIND a blink, or the shape pops |
| needs you | widen 34 %, the pair spreads 14 %, and the blink goes slow (wide eyes that never blink read as a frozen image) |
| failed | droops, tilts outward, looks down, stops blinking |

The squint is the one that matters and it is the one that was hard: a still cannot show easing, so
the design was verified as **animated GIFs rendered frame by frame from the same curves**
(`docs/ai/agent-overlay/round-01/anim/`). The first attempt was rejected — 25 fps made an 80 ms
blink two frames, the arc was drawn 2.4× wider than the bar so the shape *popped*, and the 700 ms
squint hold was a frozen still. All three are fixed, and the eye is now ONE continuous primitive:
constant thickness, a fixed centre, and a zero-bend arc identical to a closed bar.

**The third pass (2026-10-20) — the tab's one line of text.** The tab is 30 px tall and holds a
single 10 px line, and that line was one flat grey run: the digits, which are the point, weighed
exactly what the words labelling them weigh, and `needs you` was the same grey as `2 working`.

Six settings were rendered (A–F: weight, tone, split, glyph, hero, badge) and then, on the human's
direction ("i love B... when its a failed state it should be colorful with bold too just like C...
i like E's done checkmark"), six more built on that recipe — **the winner was G1**:

> **B's weight everywhere, C's colour only where something is wrong, E's tick for finished.**

* the **count is 11 px SemiBold in `#EDEDF0`**, the word labelling it is 10 px in `#84848B`, and the
  `·` between the two halves is `#4A4A52` — the number is what you see first;
* **`finished` is the tick** (`\uE73E`, 9 px, `#7ED7A2`) followed by a bold count: `2 working · ✓ 4`.
  It is the same glyph this design already uses for "settled", and it is ~40 px narrower than the
  word;
* **an urgent line takes its alarm colour, in full**: `needs you` in `#F0C384`, `N failed` in
  `#EE9292`, 10 px SemiBold. Nothing else in the tab is ever coloured, so the colour means one thing;
* `idle` stays `#C9C9CE`, and a finished-only line is just `✓ N`.

It is `BoardData.TabRuns` (a list of `TabRun`), drawn by `Draw.RunLine` — a horizontal StackPanel
whose runs are reconciled in place, because a single TextBlock cannot carry two weights and four
colours, and the wording changes whenever a session starts or finishes. `LayoutTab` measures the
runs and positions the line by its own height, so its optical centre stays at y≈13.7 whatever the
tallest run is. Verified on the real window against four fixtures — `2 working · ✓ 4`, `needs you`,
`2 failed`, `✓ 4` — with the actual pixel colours sampled out of the render (`238,146,146` for the
failed line, `237,237,240` for the counts, `240,195,132` for the amber), not eyeballed.

**The fourth pass (2026-10-20) — the board carries every row, and a row is a button.**

Two things the human hit in the same breath: the expanded board showed **3 of the 6 rows** it was
sent (so the sessions behind the tab's `+2` were visible nowhere), and clicking a session did
nothing. The first was an unexamined `Take(3)` — the rig had already shown five rows fit at the
card's 188 px and six at 198 (E5/E6, round 02) — so the card is now `CardH = 198` and draws every
row the host sends; the host's own `maxRows` (6) is the only ceiling.

The second is `Build(state, pinnedId)`: **clicking a row pins the orb to that session** — whatever
the recency and urgency rules would otherwise have chosen — and clicking it again, or the board's
own background, lets go. A pin is a question ("show me this one"), not a mode. The window is
`WS_EX_NOACTIVATE`, so the click does not take the focus off whatever the human is doing; the row
carries a hand cursor and a tooltip so it reads as clickable. "Open that session in the app" is a
different thing and still is not possible: it needs the client half (P2), which only loads on a
harness restart.

**A click cannot be simulated while another app owns the pointer.** Measured the same day: with
Minecraft in the foreground, `SendInput` at (825,120) reported success, `GetCursorPos` still
returned the screen centre, and `WindowFromPoint` named `javaw` — the game recentres a locked
cursor every frame, so every synthetic move is undone before it lands. The pin path was therefore
checked with `--pin 5`, which sets the same `_pinnedId` a click sets: the board log moved from
`title "You are the world teammate"` to `title "You are the ECONOMY agent"`, and the orb swapped a
red error ring for a green settled one with the pinned row's title under it.

**The fifth pass (2026-10-21) — taking a session off the list.**

While a row is selected, a settled row (`✓` done or red failed) shows a quiet ✕ at its right edge;
clicking it retires the row. Design round 07 put the ✕ in six places — trailing, dot-swap,
always-on, under-bar, circle, and a bottom bar — and **trailing** was the pick: the dot-swap reads
both ways (it makes a finished row look failed), and the labelled under-bar was the runner-up.

* **Only settled rows get one.** A `working` or `attention` row has nothing to retire — the host
  deliberately ignores the closed-out verdict for those — so a ✕ there would be a button that does
  nothing.
* **It is a retire, not a hide.** The ✕ makes the same mark the sidebar's ✓ makes, in the same
  store (`state/session-finished/manual.json`), so the app's tick and the overlay cannot disagree.
* **Two ways to make the mark, because the port belongs to the host.** `lib/host-v6.js` publishes
  `finishedUrl` (absolute: `http://127.0.0.1:<port>/session-finished/api/status`) in the state
  payload, and the window POSTs `{sessionId, state:"finished"}` — the route's own validated write.
  That is the path once v6 is loaded. Until then the window falls back to writing that same key into
  `manual.json` directly, which the host re-reads on every write cycle: the row leaves within a tick,
  while `plugins/session-finished` keeps its copy in memory until it next applies, so the app's own
  sidebar tick can lag behind.
* `--pin <n>` and `--dismiss <n>` set exactly the state a click sets, which is how all of this was
  verified with no working pointer.
* **A retirement is cleared the moment that session works again** (`host-v7`). Measured 2026-10-21,
  as the human's report *“as you finished i didnt get a finished at the top notch”*: a retired row
  whose session resumed came back while it worked — live work ignores the mark — and then
  **vanished** when it finished, because `snapshot` drops a settled row that is closed out. So the
  tab showed `1 working` with that row gone, instead of `1 working · ✓ 1`. Working again is not
  finished, so the host clears the mark through the documented route (`state: "none"` — the same
  write the ✕ makes) and both stores keep agreeing. The rule covers the sidebar's ✓ too, because it
  is the same file.

**A row repointed in the MACHINE-WIDE `cordis.patch.yml` did not re-apply.** Measured 2026-10-21:
`agent-overlay-host5` was pointed from `lib/host-v5.js` at a new `lib/host-v6.js` and its `pollMs`
changed 350 → 340. Four seconds later `state.json` still read `"plugin":"agent-overlay/host@5"` with
no `finishedUrl`, and `host-report.jsonl` had gained no `boot` line. The new file loads on the next
app start. The rule in §9 (“a config change re-applies the row”) was measured on the **profile**
layer and does not hold for the machine-wide one — so a host change is not a live change, which is
why the window carries the fallback above.

**The sixth pass (2026-10-21) — the tab's bots wear the state, and a row says “teammate”.**

Two reports, one round of design (round 08), two picks:

> *“the little bot icons should be updated to match the new current state of the expanded agent
> icon (finished etc.)”* → **M1, STATE RING.**
> *“i enjoy seeing subagents, i think you should like hint it somehow”* → **S6, COUNT.**

* **M1 — the settled bot gets a ring.** The big bot answers “is anything waiting for me?” with a
  ring (green finished, amber needs-you, red failed), and the tab's 16 px bots carried only an aura
  and the identity rim. A settled one now draws the same two-pass ring in its state colour, 4 px
  outside the rim (the big bot's own proportion, 0.28 of the face) so **the identity colour is still
  visible inside it** — checked on a 4× crop of the real window, where a coral rim sits inside a
  green ring — and the eye pose comes with it: `^ ^` when finished, narrowed and tilted when failed,
  wide when it wants you. A settled bot does not blink; its face *is* the state. Working bots are
  unchanged (rim + aura + blink).
* **The pitch paid for it**: 21 → 28 px, because the ring needs room outside the face, and each bot
  is placed by its 7 px padding rather than by its face. The tab is sized from its own content
  (`LayoutTab`), so it simply got wider; nothing else moved.
* **M1 as drawn was chosen over M2–M6 on the sheet's evidence**: a single ring that *replaces* the
  identity rim (M2) loses which session it is, the full mirror (M4) is busy at this size, and the
  badge dot / underline read but say less.
* **S6 — the parent counts its teammates.** A row that is a delegated session steps back to
  `Ink2`; the session that delegated it trails `· 3 teammates` in `#6E6E77` at 9.5 px, and the title
  gives way so the tail always fits. The count is a fact about that session, **not** a claim about
  grouping: rows are ordered by urgency, so a teammate's row is rarely next to its parent's (that is
  why the rail and the indent variants were rejected on the sheet — both group rows that are not
  adjacent).
* **Where the fact comes from**: `header.origin === "subagent"` and `header.parentSession` on
  `sessionQuery.listSessions()` — read out of two real session logs on this machine, 2026-10-21, and
  **nowhere else**: no session EVENT carries delegation. `lib/session-state-v4.js` copies both onto
  the record and into the row; `lib/host-v7.js` reads them off the header. The row's tail is
  `""` for a top-level session, so “no teammates” and “not a teammate” can never be confused.

**A window that could not be spawned was never retried.** Measured while rebuilding: the old exe was
killed to free the file, `dotnet build` spent a second between deleting the apphost and copying the
new one, and the host's spawn landed inside that second — `window-missing` in `host-report.jsonl`,
and no overlay on screen until the app restarted. `host-v7.js` now retries that case every 15 s
(a missing exe is a rebuild in progress, not a broken install).

**The seventh pass (2026-10-21) — the context under the bot, and three motions.**

Two picks from the round-09/10 sheets, in the human's words: *“i like R6 the most. showing context is
nice, but maybe it should show that stuff when its selected as the main agent instead… near the big
agent head, below it”*, then *“D1 with D6 (percentages)”*.

* **R6 — rows are silent by default.** A row says nothing while it is healthy. The one thing it may
  say, for a session and not a teammate, is how many sessions it delegated (`· 3 teammates`, dim). No
  context, no clock, no counters on a healthy row. (A tool that came back broken had a red tail here
  until 2026-10-21; it was removed outright — see the eighth pass.)
* **D1 + D6 — the context hangs under the front session's bot.** A 56 px bar with the count under
  it (`82k`). Past 80 % of the budget the bar and the count turn amber and the count becomes a
  percentage (`82%`): the number only changes shape when it is warning you. The bar's denominator is
  `BoardData.ContextBudget` (200k) — a bar needs one, a token count does not, which is why the
  readout shows a count.
* **Where the number comes from**: `assistant/message` → `data.usage.inputTokens`, the prompt size of
  that session's last answer — it is NOT summed, because a sum only grows and means nothing. The
  LAST message wins, so it tracks the context as it fills. (Both this and the count readout were
  replaced in the eighth pass — the section below is what shipped.)
* **Three motions, each of which settles to the still frame the sheets drew** — a ring that *draws
  itself* when a bot settles (360 ms, dashed stroke walked by `StrokeDashOffset`, so no geometry is
  rebuilt per frame), a bot that slides into the tab when a session appears (200 ms), and a retired
  row that folds up while the rows below close the gap (140 ms, started by the ✕ before its write
  goes out).
* **Each has a still-frame proof flag**: `--ring <0..1>`, `--arrive <0..1>`, `--fold <0..1>`. The
  screenshots in `docs/ai/agent-overlay/round-09/` are the live window at 45 %, 40 % and 50 % of the
  three motions, taken the same way every other state in this README was.

**The ninth pass (2026-10-21) — the frame source was the bug, not the drawing.**

Kuba: *"not sure why but it doesnt feel really smooth, its like its running at low fps (appear/disappear
animation) try to look at that"*. The overlay now measures itself, because "feels smooth" is not a
number: `--log` writes one line per second with the frame count, the gap between frames and the time
spent inside `Tick`.

* **Measured, before**: `fps=39 avgGap=25.5ms maxGap=40.4ms` with `maxWork=0.2ms`. The work was never
  the problem — a `DispatcherTimer` at 16 ms was delivering every **25–29 ms** (35–40 fps, worst gaps
  48 ms). It is serviced at `DispatcherPriority.Background`, so it queues behind layout and the render
  pass; at 16 ms it was already fighting this laptop's **144 Hz** panel (6.9 ms).
* **Now**: the motion is driven by `CompositionTarget.Rendering` — one step per compositor frame, with a
  `RenderingTime` guard because WPF can raise it twice for one frame. Measured after: **`fps=77
  avgGap=13.2ms maxGap=16.3ms`, `over25=0`** while the tab is all that is on screen.
* **The remaining ceiling is WPF's effects, and it is measured, not guessed**: with `--no-effects`
  (a diagnostic knob that strips every `Effect` and re-strips after each rebuild) the OPEN board runs
  at **143 fps / 7.0 ms** — the panel's full rate — while with the shadows and glows it runs at **~40
  fps**. So a card full of blurred shadows is ~18 ms of every frame. Replacing the card's shadow with a
  pre-rasterised `RenderTargetBitmap` image looks identical on screen but **breaks the `--shot` capture
  path** (the card vanishes from the still), and the tab's shadow cannot be a fixed bitmap at all
  because `LayoutTab` resizes the tab to its text — both were tried and reverted rather than shipping a
  broken evidence path. Doing this properly means pre-rasterising every glow into an image and fixing
  the capture path in the same change; it is the next piece of work, not a one-liner.
* **`--shot` had to be taught to pose the frame**: it renders the content tree 1400 ms after load, and a
  shot window no longer gets guaranteed compositor frames, so a round of stills came out tab-only (the
  card at opacity 0). `ArmShot` now calls `Tick()` once before rendering.
* Per-frame waste removed while measuring: `OrbView.Set` and `MiniBotView.Set` allocated two
  `RotateTransform`s each and a finished mini bot rebuilt two `PathGeometry`s **per frame, forever**;
  the big bot's rim glow changed its `BlurRadius` every frame, which re-renders the effect's surface —
  it now breathes through its opacity, which costs nothing. `ApplyProgress` allocated a
  `TransformGroup` per frame; the transforms are kept and mutated.
* Two diagnostic knobs, never used by the plugin's own spawn: `--fps N` (cap the motion's frame rate)
  and `--no-effects` (strip the glows), plus the refresh rate and render tier logged at startup.


**The eighth pass (2026-10-21) — the context was the wrong number, and the failure tail is gone.**

Two reports from the human, both about a figure that never stopped saying something:
*"im guessing it only shows output tokens? would be nice if it was full context instead. and always in
percentages"*, and *"the 1 tool failed stuff stays there forever maybe only make it stay there for a
moment after it happens?"*.

* **`usage.inputTokens` is NOT the prompt.** Measured over seven sessions on this machine (every usage
  record): `totalTokens === inputTokens + cacheReadTokens + outputTokens`, exactly, every time. So
  `inputTokens` is the slice of the prompt that MISSED the cache — 228 next to a 99,428-token prompt,
  3,182 next to 182,144 cached, 865 next to 114,432. The seventh pass stored that field, so on any
  cached step the readout collapsed to a few hundred tokens and read exactly like an output-token
  count. `session-state-v6.js` now stores the whole step: `fullContextTokens()` prefers
  `totalTokens`, falls back to `input + cacheRead + output` (one measured record carried the parts and
  no total), and answers NaN when there is no prompt at all.
* **A percentage, against a denominator the host publishes** — and a bare token count when the denominator is unknown, which is the `null` budget `CONTRACT.md` now mandates for a route nobody has measured. `contextBudget` = 1,000,000 (this
  route's `contextWindow`, `profiles/desktop`) and `contextHotAt` = 0.5, written into the state file
  beside the rows. 0.5 is not decoration: it is where DSH's compaction fires on this route
  (0.5 × 1 M ≈ 500 k), and compaction has actually run here — a live session's prompt peaked at
  510,917 tokens. Amber therefore means "this session is about to lose its history", which is the only
  thing a context readout is for.
* **An older host publishes neither**, and the window then keeps the old look — bar against 200 k,
  count as `82k` — instead of inventing a percentage out of a figure that is only the uncached slice.
* **The broken-tool tail was removed outright.** A first answer gave it a 30 s shelf life; that was
  still wrong, and Kuba said so: *"still shows 18 tools failed i dont think it expires just clear tools
  failed from it entirely"*. A per-turn count is not a moment in time — it survives the turn, and the
  row keeps saying it after the agent has worked around the mistake. `tool/result` is no longer read at
  all: a turn that really ended badly is an `error` row, with its own badge and ring, and nothing on
  this board counts anything else.
* **`--open` is the flag that photographs the expanded board.** Without it `--shot` renders the slim
  tab and no amount of fixture data helps — a round of shots was spent on that.
* **The readout moves out of the ring's way.** A settled session grows a ring that reaches ~27 px below
  the face at this bot size, and the readout sat 9 px under it — so on a finished session the green arc
  ran straight through the bar and the number (*"maybe move the context bar a little lower so that when
  its finished and has the bigger circle around it it doesnt collide"*). It is not parked low
  permanently, because that leaves it floating whenever there is no ring, which is most of the time:
  `OrbView.RingReach` measures the ring's own painted extent (diameter + stroke + blur, so the
  geometry has exactly one home) and the readout slides down by `RingReach × _ringAlpha` — the ring
  pushes it aside over the same 250 ms it fades in. Proof: `before-ctx-settled.png` /
  `after-ctx-settled.png` and the stacked pair beside them, in `docs/ai/agent-overlay/round-11/`.


## 3. The architecture — two parts, one job each

┌──────────────────────────── DSH host process ────────────────────────────┐
│ lib/host.js                                  (host plugin, the facts)    │
│   • session/event {global:true} → per-session state (lib/state.js)       │
│   • ctx.agents, sessionQuery, workspaceRegistry — what the GUI reads     │
│   • serves GET  /agents-notch/state          (ungated plugin route)      │
│   • writes state/agents-notch/state.json atomically, every pollMs        │
│   • spawns/supervises AgentsNotch.exe --state <path> --parent-pid <pid>  │
└──────────────────────────────────────────────────────────────────────────┘
        │ reads state.json (150 ms)                  ▲ writes quit.json
        ▼                                            │ (tray "Quit overlay")
┌─────────────────────────┐            ┌──────────────────────────────┐
│ AgentsNotch.exe         │            │ no browser half: the window  │
│ .NET 9 WPF, ~100 KB     │            │ IS the UI, and quit.json is  │
│ • the tab + the board   │            │ the only thing it says back  │
│ • QUNS/fullscreen hide  │            └──────────────────────────────┘
│ • tray, position, scale │
└─────────────────────────┘

The three-part sketch this replaces — `lib/index.js`, `lib/client.js`, a POST intent channel — was
the plan before the window existed. The shipped design has no client half and no intent channel;
see the contract's *Who writes what*. The window is not draped over the harness's DOM: it reads one
JSON file that the host writes, which is the only interface that survives a harness restart.

## 4. The four facts that decide it (all measured on this machine)

1. **A DSH plugin cannot create a window.** The Electron shell's `main.js` has zero occurrences
   of `alwaysOnTop` / `topmost` / `setShape`, no IPC channel that takes bounds or z-order, and
   plugins do not run inside Electron (the host is spawned with `ELECTRON_RUN_AS_NODE: "1"`).
   → the window must be a **separate process**.
2. **A plugin can spawn one.** Two working precedents on this box:
   `anneheartrecord/dsh-desk-pet` (a plugin launching a Python/Tk window) and
   `dsh-computer-use-win` (a plugin driving a PowerShell + P/Invoke helper).
3. **The host plugin sees the truth for free.** `session/event` (subscribe with
   `{ global: true }` or it silently sees nothing) plus `ctx.agents.list()`,
   `ctx.sessionQuery.listSessions()` and the `turnBoundary` / `userQuestions` projections.
4. **The overlay cannot use the harness API, but can read a plugin's route.** The server binds
   `--port 0` and `/`, `/connect`, `/api/*` and the WS mux are cookie-gated (measured 401);
   routes a plugin registers are **not** gated (measured 200). → a plugin owns the facts and
   serves them; the port is discovered from a file it writes.

## 5. The data model

`GET /agents-notch/state`, mirrored to `state/agents-notch/state.json` so a restart does not blank
the notch. **`docs/CONTRACT.md` is the authoritative field list** — this section records where each
fact comes from and why the board is shaped this way.

```jsonc
{
  "schema": 3, "plugin": "dsh-agents-notch/host", "wallMs": 0, "hostPid": 0,
  "retire": "route",                              // "timer" when no close-out verdict is arriving
  "context": { "budget": null, "hotAt": 0.5 },    // null: unknown → never a percentage
  "settings": { "scale": 1, "screen": "primary", "anchor": "top-center", "maxRows": 6,
                "hideFullscreen": true },
  "summary": { "status": "working", "working": 2, "attention": 1, "done": 1, "error": 0, "total": 4 },
  "sessions": [{ "id": "…", "title": "…", "project": "…", "cwd": "…", "status": "working",
                 "since": 0, "activity": { "kind": "tool", "label": "roblox_verify" },
                 "approval": null, "steps": 7, "identity": "teal", "origin": "",
                 "parentSession": "", "contextTokens": 412034 }]
}
```

Where each field comes from — nothing here is invented, these are the facts the GUI sidebar already
reads:

| field | source |
|---|---|
| `title` | `sessionQuery.readTitleSnapshots([id])`, kept fresh from `session/title` |
| `project` | `ctx.workspaceRegistry.list()` → `title` matched on `header.cwd`; else the cwd's last segment |
| `working` | `ctx.agents.get(id)?.status === "running"` **or** `turnBoundary.openTurnStartSeq !== null` |
| `activity` / `steps` | the last `tool/call` name; `steps` counts them within the turn |
| `approval` | an `approval/asked` with no paired `approval/decided` (always turn-enclosed) |
| attention | the `userQuestions` projection: `active.length > 0` |
| `error` / blocked | the last `turn/end` reason (`error`, `max-tokens`, `blocked`, `interrupted`) |
| `done` | the last `turn/end` reason `completed` |
| `origin` / `parentSession` | the same two header fields, copied verbatim onto the session's own row — this is what lets the board mark a teammate and count a parent's children |
| `identity` | a stable hash of the session id onto one of five hues (`identityOf` in `lib/state.js`) |
| `contextTokens` | `assistant/message` → `data.usage`, the LAST one and not a sum: `totalTokens` when present, else `input + cacheRead + output`. **Not `inputTokens`** — that is only the uncached slice of the prompt (measured: 228 of a 99,428-token prompt) |
| `context.budget` / `hotAt` | the plugin row's config, because the window size belongs to the model route and the session feed never carries it. `budget` is `null` unless configured — contract rule 1 |
| `retire` | the close-out probe's health: `plugins/session-finished` is asked over HTTP, and three consecutive failures with no answer publish `"timer"` |

Rules (as implemented in `lib/state.js`):

* A row exists only because something HAPPENED to the session: `working`, `attention`, `done`,
  `error`. `idle` never gets a row, and a session that merely exists never gets one either.
* `done` and `error` rows do not expire on a timer — **unless** `retire` is `"timer"`. Under
  `"route"` they stay until the close-out verdict says otherwise (the human's ✓ in the sidebar, or a
  close-out command's evidence). A `working`/`attention` row ignores that verdict: resuming a
  closed-out session puts it back on the board.
* Under `"timer"` a settled row ages out `finishedFallbackMs` (default 15 min) after `since` — and
  only settled rows: a working row is never retired by a clock.
* Order: attention → error → working → done, newest first within a band; at most `settings.maxRows`.
* Every field on a row is a property of the SESSION, never an event with a shelf life: no timer
  decides anything here, because a value that decays changes with nothing having happened, and a
  value that does not decay (the old `toolFailures` count) outlives the thing it reported. Both were
  tried and removed.
### How a settled row goes away

`retire` is `"route"` whenever the harness's own close-out verdict is available: `plugins/session-finished`
is asked over HTTP which sessions the human closed out, and because nothing on a row decays, that
answer — never elapsed time — is what removes one. That plugin is optional on a stranger's machine,
so the host counts *consecutive* failed probes: **three in a row** publish `retire: "timer"`, after
which settled (`done`/`error`) rows older than `finishedFallbackMs` (default **900 000 ms = 15 min**,
counted from the row's `since`, i.e. when it settled) are dropped *as if* closed out; **one**
successful probe publishes `"route"` again and zeroes the count — same process, no restart. Two
edges are load-bearing. A missing `webServer` port is **not** a failure (that is the host not being
ready yet, and counting it would publish `"timer"` for the first second of every boot and blink a
settled row off a board whose verdict in fact exists), and no probe is attempted while nothing is
settled, so a board with nothing to retire can never flip to `"timer"`. `working`/`attention` rows
are never retired by either source: a session you closed out and then resumed is working again.

The report events that make this auditable from `host-report.jsonl` alone: `finished-unavailable`
(first failure only), `finished-failed` (the network/JSON reason), `retire-fallback` (the transition
into `timer`, with `failures` and `fallbackMs`), `retire-route` (the transition back), and `boot`,
which carries the effective `settings` and `budget` so "why is the notch small / why is there no
percentage" is answerable without a debugger.
## 6. The window

**Shape (the human's call, 2026-10-19):** a **slim tab hangs from the top centre of the screen at
all times** and carries the summary (a state dot, the count, a chevron). The **board expands under
it on hover** with an animation and folds away when the pointer leaves. The board never opens over
a full-screen app. **The window is not draggable** — it was, and it got dragged by accident.

- `WindowStyle=None`, `AllowsTransparency=True`, `Background=Transparent`, `Topmost=True`,
  `ShowInTaskbar=False`, `ShowActivated=False`, `Focusable=False`.
- The window is 660×300 DIP — wider and taller than the 560×188 board so the card's drop shadow
  has room to fall. The tab is 220×30 at the top centre; the board sits 20 px below it, overlapping
  it by 10 px so the tab reads as a notch in the board's top edge.
- After `SourceInitialized`: `SetWindowLong(GWL_EXSTYLE, … | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
  | WS_EX_LAYERED)`, then `SetWindowPos(HWND_TOPMOST, …, SWP_NOACTIVATE)`; re-assert topmost
  about once a second (z-order is not a guarantee).
- **Focus theft is the failure that matters**: `ShowActivated=False` + `WS_EX_NOACTIVATE`, and
  never `Activate()` — a game must not lose focus when the pill appears.
- `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` before any window; place with the
  monitor's `workArea` (excludes the taskbar), not `bounds`.
- **Clicks pass through the empty parts of the window**: the root canvas has **no background**, so
  a transparent WPF window lets the click reach the app underneath. A `Window.Region` would clip
  the card's drop shadow away, so there is no region — the card's panel and the tab are the only
  hit-testable things. The folded card is `IsHitTestVisible = false` so it cannot eat clicks.
- The unfold: 160 ms cubic ease-out — the board fades in, slides down 12 px and grows from 94 %.
  A 250 ms delay before it folds, so the pointer can cross the tab's edge without flicker.
- **The tab shows the bots themselves**, not a status dot: up to four 16 px mini-bots, each wearing
  its **session's colour** as the rim, each with **two little eyes** that blink on its own phase,
  and each wrapped in a **status aura** — a soft halo in the session's *state* colour (blue working,
  amber needs-you, red failed, green done). The big bot has the same aura behind its face. The
  identity colour and the state colour never mix: rim = which session, aura + badge = what state.
- `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` so the pill stays out of Teams /
  Zoom / OBS captures — **off by default** (`--capture-exclude`), because it also hides the board
  from a screenshot, which is how it gets checked.

**Hiding ("unless I'm playing games")** — polled at 2 Hz:

1. `SHQueryUserNotificationState()` → hide on `1 NOT_PRESENT`, `2 BUSY`, `3 RUNNING_D3D_FULL_SCREEN`,
   `4 PRESENTATION_MODE`; show on `5`, `6`, `7`. **Modern games report `2`, not `3`** (Windows
   full-screen optimizations) and Microsoft defines `2` as "a full-screen application is
   running" — so `2` must hide.
2. Optional `fullscreenMode: "gamesOnly"`: `GetForegroundWindow` →
   `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` → compare with `MonitorFromWindow` +
   `GetMonitorInfo(rcMonitor)` within ±2 px. A maximised IDE fails that test; an F11 browser
   passes it.
3. A hook is **not** sufficient — Microsoft documents no notification when a full-screen app
   starts or stops.

**The bot** is one drawing function, `Orb(size, state, identity, blink, lookX, lookY, rim)`; the
render loop only moves the last four numbers — blink (140 ms, every 4–7 s), eye drift toward
the pointer (220 ms ease), breathe (3.2 s sine, ±1.5 %), rim glow breathing (2.4 s sine,
`.3` idle → `1` attention), attention flare (1.2 s), done settle (400 ms), and a bubble
sliding in for a new session (220 ms).

**Identity colour**: every session gets a stable hue derived from its id. The hue is the orb's
**rim**; the badge and the chips carry **state**. The two never mix — identity on the chips was
tried and reverted because it dims the state reading.

## 7. What is not built

| | |
|---|---|
| A browser half | clicking a row to open that session would need a client plugin plus a POST intent route. The window is not a client: it reads one JSON file and writes one. |
| Any other platform | the window is WPF. macOS and Linux have no build, and no partial one: the notch is a Win32 technique (topmost, no-activate, QUNS) all the way down. |
| A signed binary | the release is unsigned, so SmartScreen will warn the first time. Signing costs a certificate and there is none. |
| Autostart, capture exclusion, position memory | P4 in the original plan. Position and scale are config, not memory; the window deliberately remembers nothing on disk. |

## 8. Decisions taken (and why they are not up for grabs in a patch)

* **Placement: top centre, fixed.** It was draggable once and got dragged by accident. Move it with
  `anchor` and `screen`.
* **Hide rule: any full-screen app.** The board never opens over one, and the slim tab follows
  `hideFullscreen`. A game in front means the notch is not there.
* **Scope: every live session, capped by `maxRows`** — not "the open projects". A session earns a row
  by having something happen to it, and a project filter cannot know that.
* **The window remembers nothing.** Settings are the host's (the plugin row's config); a second
  source of truth on disk was tried and is what made "why did it move" unanswerable.
* **No row ever decays on a timer** unless the board publishes `retire: "timer"`, which it only does
  when the close-out verdict is genuinely unavailable. A field that changes with nothing having
  happened is the one thing this board must not have.

## 9. Traps — do not walk into these again

### Measured 2026-10-21 (shipping it as a package)

* **A single-file .NET release can die before `Main`, and there is nothing on screen to say so.** The
  first published build was 65 MB, one file, and it exited with `-2147450720`:
  `Failed to create directory [C:\Users\…\AppData\Local\Temp\.net\AgentsNotch\] for extracting bundled
  files. (error 5)`. Nothing was drawn and nothing was logged — from the outside, "the notch never
  appears". `PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract=true` (needed for WPF's
  `wpfgfx_cor3`/`PenImc_cor3`/`D3DCompiler` — without that flag the publish leaves FIVE DLLs beside the
  exe and the release is no longer one file) makes the app unpack itself into `%TEMP%\.net\<name>`, and
  `%TEMP%` is not reliably writable. Fix: the host spawns the window with
  `DOTNET_BUNDLE_EXTRACT_BASE_DIR=<stateDir>\bundle`, so the unpack location is ours. Verified: the
  directory appears under the state dir and the exe runs (exit 0).
* **A test that says "no window is installed" is a test that reads the developer's disk.** Two host
  tests legitimately assert `window-missing`, and they broke the moment this checkout contained a
  built window: the lookup walked past the deliberately-bogus `exePath` and found
  `desktop\bin\Release\…\AgentsNotch.exe` (and later the release under `dist\win-x64`), so instead of
  reporting the absence it spawned one. Fixed on both sides: `DSH_HOME` points at an empty temp dir
  and the host takes an `exeRoot` so the lookup can be pinned inside the test's own directory. The
  lesson is not "flake" — it is that a suite about a file lookup has to own the filesystem it looks at.
* **The window obeys its parent, in both directions.** Measured 2026-10-21 on the published binary:
  started with `--parent-pid 999999` (a pid that cannot exist) it draws, logs `watching parent pid
  999999`, and is gone **2.5 s** later with `parent … is gone — exiting`; started with a live parent
  and that parent killed, it is gone in **0.5 s**. The other half was measured by killing the window
  with the plugin mounted: `window-exit code 4294967295` and the host's supervisor re-spawned it
  **15.0 s** later, exactly the contract's retry.

* **Still unverified, and worth saying so:** the tray's *Quit overlay* has only been tested at the
  host end (a hand-written `quit.json` holds the spawn for an hour, covered by a unit test) — the
  click itself needs a human or UI automation. The fullscreen hide needs a full-screen app. Mixed-DPI
  and multi-monitor placement has only run on a single 1920×1080 display at 120 DPI, where
  `--probe-rect` reports what it should.
* **The bundle really does mount a package by name.** Installed from a local checkout with the app's
  plugin manager, the package's own `cordis.patch.yml` inserted its row, and the loader resolved the
  bare specifier `dsh-agents-notch` through the profile's `node_modules` — the row carries no path and
  no `file://` URL. One live apply produced `boot`, then `window-started` with
  `source: "installed"` pointing at the release under `state\agents-notch\window\0.1.0\`, and the route
  answered `200 application/json` on the harness's own port. Unmounting killed the window
  (`window-exit code: null` — the disposer, not a crash).
* **A "temporary" override row belongs in the MACHINE layer, and only there.** While testing, the
  bundle row was neutralised with a bare `- id: agents-notch` + `config: { spawnWindow: false }` in
  `$DSH_HOME/cordis.patch.yml`. Composition order is bundles → profile → machine-wide, so the bare row
  patched the bundle's own insert; putting it in a bundle layer would have raced the insert instead.
  It was removed afterwards by cutting exactly the lines it added, and `git diff` in `$DSH_HOME` says
  the file is byte-identical again — a machine-config edit left behind is how a "temporary" test
  becomes a permanent surprise.

### Measured on 2026-10-20

**The window's state-file read can break the host's atomic write, and the symptom is silence.**
The host publishes `state.json` by writing `state.json.tmp` and renaming over it — that is what
makes the window never read half a file. On Windows that rename **fails while anything holds the
target open without `FILE_SHARE_DELETE`**, and `File.OpenRead` does exactly that. Raising the
window's poll from 300 ms to 150 ms made the collision frequent enough to see: `write-failed
EPERM` in `host-report.jsonl` every few seconds, with the board silently running on stale data.
Two fixes, both needed: the reader opens with `FileShare.ReadWrite | FileShare.Delete`, and the
publisher retries the rename (4 × 4 ms) because the lock is also partly **external** — failures
continued every ~30 s with the reader fixed, which is something else on the machine touching the
path.

**A plugin reload leaves a session that is mid-turn invisible until its next event.** `records`
starts empty, a session only earns a row from a live event or a log replay, and `sweep()` replays
only for records with an empty *title* — so a session already inside a turn when the row reloaded
shows nothing. It is transient (the next `turn/start` or `turn/end` fixes it) and it does not
happen on a normal app start, where the plugin mounts before any turn begins. The clean fix is to
ask `ctx.get("agents").get(id).status === "running"` during the sweep; it is not implemented here
yet.

**`turn/end`'s `reason` is an OBJECT, and reading it as a string silently blanks the board.** The
harness types it as `TurnEndReason` = `TurnEndReasonMap[keyof TurnEndReasonMap]`:
`{ kind: 'completed' } | { kind: 'aborted', reason } | { kind: 'blocked' } | { kind: 'error', error }
| { kind: 'max-tokens' } | { kind: 'interrupted' } | { kind: 'forked' }`. Its loop builds
`turnEnds = { kind: "completed" }` and appends `{ turn, reason: turnEnds }`. So
`typeof data.reason === "string"` is **always false**, the reason is `""`, and every turn end lands
in the `else` branch → `idle` → no row. The failure is total and silent: the board looks healthy,
it just never shows anything finishing. `lib/session-state-v3.js`'s `reasonKind()` accepts both
shapes; **any test fixture for a session event must be copied from the harness, not invented** —
this bug survived 15 green tests because the fixtures were strings.

**A finished row must not expire on a timer.** Any `doneTtl`-style rule answers "has enough time
passed" when the question is "has the human seen it". The answer lives in `plugins/session-finished`
(the sidebar ✓), reachable two ways, and both are used here: its route
`GET /session-finished/api/status?ids=…` — on `ctx.webServer.port`, which exists and is the port
the app is already serving — and `state/session-finished/manual.json`, which is what the human's
tick actually writes and therefore still works when that plugin is not mounted. The manual mark
must win in BOTH directions (`finished` retires a row, `unfinished`/`none` brings it back), or
clearing a tick does nothing.

**Retiring a row for being closed out must not retire live work.** `working` and `attention` rows
ignore the verdict; otherwise a session you closed out and then resumed is invisible while it runs.

**A row that never expires changes the order.** `done` had to move below `working`: with no TTL,
history accumulates, and a six-row board ordered `…done…working` fills up with finished work and
pushes out the agents that are actually running.

**The idle tab drew its dot on top of its own label.** `Draw.Dot(6, …)` was placed at x=15 and the
label at x=17 — a 2 px gap less than the dot is wide. Measured on a 4× zoom of the live window,
not guessed.

**Measuring the visual is not optional, and a screenshot of a transparent window is not a
screenshot.** `PrintWindow` renders the transparent area black, which is fine for judging the
pill, but the pixel values are the only reliable way to check a 16 px aura: sampling the halo
around each bot gave `#24272F` (blue, working) and `#212A26` (green, finished) against the panel's
`#08080A`. The colour is there; at that size it is subtle, and no amount of staring at a scaled
screenshot replaces reading the pixels.

### Measured 2026-10-21

- **A `Canvas` clips its children to its own bounds.** The design rig draws the underline candidate
  as a bar 1.6 px BELOW the 16 px face, inside a canvas of exactly `size + pad*2`: at every zoom the
  bar simply was not in the render, and the variant looked like a do-nothing. The canvas has to be
  taller (`size + pad*2 + 4`) for anything drawn past the face. `RenderTargetBitmap` clips to the
  element it renders, so “it is drawn but invisible” and “it was never drawn” look identical.
- **A WPF `Border` that is 1 px wide with a 1 px border has no fill at all.** The tree rail in the
  teammate sheet was drawn as `Panel(1, h, …)` and was completely invisible; `BorderThickness` eats
  the whole width. Use a `Rectangle` for hairlines.
- **A missing exe is a rebuild, not a broken install.** `dotnet build` deletes the apphost and
  copies the new one a second later; a spawn inside that second logged `window-missing` and the
  board stayed dark until the app restarted, because nothing retried it. v7 retries every 15 s.
- **A retired row is not a muted session.** The mark that retires a settled row (`manual.json`)
  outlives the retirement: the row came back while the session worked and disappeared again the
  instant it finished, so the tab never said `✓ N` for it. Nothing in the *window* can fix that —
  the row is dropped by the reducer before it is drawn — which is why the rule lives in the host.
- **The tab's 16 px bots can carry two colours if the ring is given real space.** A state ring 3 px
  outside the rim visually swallowed the identity rim at 1×; at 4 px (the big bot's own 0.28 of the
  face) a 4× crop of the live window shows the coral rim clearly inside the green ring.

### Measured while building P0 (2026-10-19)

- **`SHQueryUserNotificationState` is not trustworthy on its own.** It answered **2 (BUSY)** on this
  machine with **nothing full-screen running** — the foreground app was OBS. Trusting 2 as "a
  full-screen app is running" hides the pill *always*. The rule now hides on **1 / 3 / 4** (locked,
  D3D full-screen, presenting) and on the **geometric** test — the foreground window's DWM frame
  covers its whole monitor (±2 px), which is what an F11 browser or an exclusive-fullscreen game
  actually looks like. 2 is ignored.
- **A WPF window needs an explicit `Show()`.** Setting `Application.MainWindow` and calling
  `Application.Run()` did **not** put a window on the screen (the HWND never appeared in
  `EnumWindows`, and no `Loaded` ever fired). `window.Show()` before `app.Run()` fixes it. The
  window was then visible on the first try.
- **`AttachConsole` eats a redirected stderr.** It replaces the standard handles, so a
  `Start-Process -RedirectStandardError` log file stays empty. The app logs to a file when given
  `--log <path>`, and only attaches to the parent console when stderr is *not* already redirected.
- **A GUI child inherits the tool's stdout pipe**, so the tool call blocks until the child exits
  (it was killed at a 180 s timeout). Always `Start-Process -RedirectStandardOutput/-RedirectStandardError`
  to files when launching a long-lived window from a session.
- **`UseWindowsForms` + `UseWPF` + implicit usings = 29 CS0104 errors.** The implicit usings pull in
  *both* `System.Drawing` and `System.Windows.Forms`, so every `Color`, `Rectangle`, `FontFamily` and
  `HorizontalAlignment` is ambiguous. Implicit usings are **off**; each file names its own.
- **`GetWindowLong` was implemented as `SetWindowLong(h, idx, 0)`** — it *sets* the style to zero and
  returns the old value. The window survived it, but the style was briefly wiped; use a real
  `GetWindowLongPtr` import.

- **A session's own title can be boilerplate.** The harness generates a title from the session's
  first message, and the first message of a fresh session is the *runtime context* — so the board
  showed a session named *"You are working on the"*. Both title paths (the `session/title` event and
  the human's first message) now refuse the known boilerplate. The session then shows its project.
- **An edited plugin module does NOT go live.** The loader imports a row by URL with no cache-bust,
  so a fix has to land under a **new file name** and a **new row id** (hence `lib/host-v2.js` and
  `lib/session-state-v2.js`, and a row id that changed with them). Each landed version carries a
  `"plugin": "agent-overlay/host@2"` marker in its payload, so a curl tells you which module
  answered.

### From earlier
- **The plugin-row cache key is the row's `name` URL, not its id.** A behavioural fix cannot be
  landed by editing the file a row already points at; rename the entry file (as
  `roblox-design` did with `lib/design.js`) or restart the app.
- **A new row must be wrapped in `insert:`**, or it is skipped with only a loader warning. A row
  mounts live; the *window* it spawns is what shows anything.
- **`spawn` reports failure asynchronously** — treat `error` and an immediate `exit` as failures
  and say why (pipe the child's stderr, as the desk-pet plugin does).
- **Two harness processes** (desktop + `dsh web`, or another profile) would both spawn an
  overlay and serve a feed on different ports. Guard with a named mutex in the exe; the newest
  `endpoint.json` wins.
- **Stale state must not look alive**: `wallMs` older than ~6 s or a dead `hostPid` → show
  "DSH not running" or hide; never the last happy frame. The port changes on every restart
  (`--port 0`), so `endpoint.json` must be re-read, never cached.
- **`listSessions()` is not failure-isolated** — one corrupt log throws for the whole corpus
  (measured: 204 of `session-finished`'s 210 read failures were one bad log). Wrap it, back off,
  and distinguish "nothing live" from "I don't know".
- **`live` is not busy.** A session can be attached and idle. Busy is only
  `agents.get(id).status`.
- **Never decode the `sessions/` directory slug, and never `zstdDecompressSync` a whole log** —
  the slug is not decodable (projects moved OneDrive → `E:\assets`, both spellings exist) and
  Node reads only the first zstd frame of a concatenated log. Neither is needed: the plugin
  reads through `sessionQuery`.
- **This is not Roblox work**: do not take `roblox_studio_hold`, and do not start a playtest.
- **WPF colours are `#AARRGGBB`**: `#FFFFFF14` is opaque yellow-white, not 8 % white; 8 % white
  is `#14FFFFFF`. This produced a khaki outline around every panel in the first render round.
- **Headless Edge cannot screenshot on this machine** (the session sandbox blocks it; it writes
  no file even to temp). The design renders therefore use **WPF + `RenderTargetBitmap`** — which
  is better anyway, because the mockups are then drawn by the stack the real window uses.

## 10. Files

```
package.json          the package; `dsh.bundle.patch` is what mounts it, and `main` is the host
cordis.patch.yml      the bundle patch layer: ONE row, naming the package (no path, no machine)
lib/host.js           the host half: the feed, the sweep, the tick gate, the route, spawn/supervise,
                       retry, atomic publish, quit marker, the retire fallback
lib/state.js          the reducer (pure): what earns a row, who delegated it, how full the context
                       is (the WHOLE step, not `usage.inputTokens`), and the settle/retire rules
lib/exe.js            where the window binary can be, in the one order that matters
test/                 39 tests, no harness needed (`node --test test/`)
desktop/              the WPF window
  AgentsNotch.csproj  net9.0-windows, WPF + WinForms (tray), implicit usings OFF
  app.manifest        per-monitor DPI awareness — the notch lands in the wrong place without it
  Settings.cs         the contract's `settings` block, clamped, applied live
  Program.cs          entry, window, board layout, motion, hide rule, tray, staleness, quit marker
  Draw.cs             the approved design as code + the live bot (OrbView) and MiniBotView
  Native.cs           Win32: topmost, no-activate, QUNS, fullscreen, cursor, monitor, DPI
  fixtures/           schema-3 state files: the tests' input and the contract's worked example
tools/
  publish.ps1         self-contained single-file build + SHA256 into dist/win-x64
  install-window.ps1  puts a release binary where the host looks for it
docs/
  CONTRACT.md         the frozen interface between the two halves
  INTERNALS.md        this file
  design/             the approved renders and the design language
```

One more thing that looks like clutter and is not: the development tree kept `lib/host-v2.js` …
`lib/host-v9.js` and `lib/session-state-v2.js` … `v6.js` side by side. A DSH plugin row is imported
by URL with no cache-bust, so a behavioural fix had to land as a NEW FILE plus a new row — editing
the file a live row already points at lands nothing until the app restarts. The public package has
one of each because it is installed fresh, not edited live.
