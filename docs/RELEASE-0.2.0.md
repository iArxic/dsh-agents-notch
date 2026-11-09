# Release brief — 0.2.0

**Status: code complete, tests green, NOT published.** Nothing is on npm, no tag exists, no release
asset exists. This file is for whoever cuts the release; delete it once 0.2.0 ships and fold anything
still useful into `INTERNALS.md`.

0.2.0 carries **three** changes: the host stops stacking close-out probes (§1–§4), and the board stops
drawing delegated sessions as agents of their own (§2a). The second one changes the **window**, so the
release asset is not the 0.1.9 bytes any more.

Measured 2026-11-09 on the developer's machine (`WOLF`, DSH 0.2.2-rc.1, 666 session logs).

---

## 1. Why this release exists

0.1.9 crashed the harness it runs inside. Not "used a lot of memory" — it took the whole app down with
`FATAL ERROR: CALL_AND_RETRY_LAST Allocation failed - JavaScript heap out of memory` at the 4.3 GB
default heap ceiling.

The notch is not a memory hog by nature. It was the amplifier of a single slow HTTP route, and the
mechanism is worth understanding because it is a pattern, not a one-off:

1. The host asks a sibling plugin (`plugins/session-finished`) "are these sessions closed out?" once a
   second, over HTTP, while anything is settled on the board.
2. That route answers by walking **every session log on the machine**. On the measured corpus that is
   666 logs / 260 MB compressed / **1 010 MB decoded**, and `readSession(id)` materialises one whole
   transcript — the largest here decodes to 42 MB.
3. At 0.1.9 the notch had **no in-flight guard**. Its "do not ask twice" check (`host-0.1.9.js:477`)
   compared against the 1 s poll window, never against the request it had just made, and the request
   itself was aborted at 2 s. So a probe that took longer than 1 s had a second probe started on top of
   it, and a failure applied no backoff at all.
4. Each abandoned probe kept its corpus scan running server-side. Roughly 32 of them in flight was
   enough to account for the heap: 3.87 GB / ~120 MB per materialised 42 MB transcript.

The evidence, from the machine that crashed:

| Observation | Value |
|---|---|
| `finished-failed` lines in `host-report.jsonl` | **41 749** |
| Failure rate at the peak | ~8 per second |
| Same reason on every line | `The operation was aborted due to timeout` |
| Tightest observed gap between two failures | **12 ms** |
| Last GC before death | `Mark-Compact (reduce) 3867.9 → 3867.5 MB, last resort` |
| Reclaimed by that GC | **0.4 MB of 3.87 GB** — every object was live |

That last row is the proof it was retention, not churn: a GC that frees 0.4 MB is looking at live
objects, and live objects at that volume had to come from somewhere. They came from the scans.

## 2. What changed in the host

**Both halves changed in 0.2.0** — §2a is a window change, so `desktop/*.cs` is *not* untouched any
more, and the release must carry a **rebuilt** `.exe`. Under `desktop/` the edits are `Program.cs` (the
row's `×N`, the big bot's cut-out count, and the removal of the old `"· 3 teammates"` text tail) and the
csproj `<Version>` string, which `test/manifest.test.mjs` requires to match the package version. The
drawing code, the faces and the fixtures are otherwise as 0.1.9 shipped them.

| Change | Where | Why |
|---|---|---|
| One close-out probe at a time (`probeInFlight`) | `lib/host-0.2.0.js`, `refreshClosedOut` | The storm itself. A second probe cannot start while the first is in flight. |
| Exponential backoff after a failed probe (`probeRetryAt`, `probeBackoffMaxMs`, default ceiling 30 s) | `noteProbeFailure` / `noteProbeSuccess` | A route that never answers stops being asked. One success resets it, so a slow-but-working route recovers immediately. |
| One `tick()` and one `sweep()` at a time (`tickInFlight`, `sweepInFlight`) | `tick`, `sweep` | `setInterval` does not wait for an async callback, so a slow tick stacked. A sweep replays whole session logs, so a stacked sweep is expensive by itself. |
| Report lines throttled to one per event per 60 s (`REPEATED_REPORTS`, `REPORT_WINDOW_MS`) | `report()` | `report()` appends **synchronously**. The per-poll failure events are throttled; the state *transitions* (`finished-unavailable`, `retire-fallback`, `retire-route`) are deliberately not, so "something changed" still logs exactly once. |
| `listSessions()` reused for 5 s (`listingRefreshMs`) | `refreshAttached` | The listing is one call across every log on the machine; the set of live sessions does not change 340 ms apart. Status changes still reach the board immediately through the live `session/event` feed. |
| **The fold: a running delegated session is a count, not a row** (`subagents`) | `lib/state.js`, `snapshot()` | §2a. `summary` is built from the folded rows, so the tab cannot count an agent the board does not show. |
| **The count is drawn, and `0` draws nothing** | `desktop/Program.cs` | `×N` in the row's title line in the session's colour; a cut-out digit on the big bot. The old `"· 3 teammates"` tail is gone. |

Everything is a default on the row, so all of it can be re-tuned from `cordis.patch.yml` without a code
change: `probeBackoffMaxMs`, `listingRefreshMs`, and the pre-existing `pollMs` / `sweepMs` /
`finishedPollMs`.

## 2a. The other change: teammates are not agents

The board drew one row per session. While a team worked, that filled it with rows that all read
"You are the … teammate", and the sessions the human actually opened were pushed off the bottom.

A delegated session that is **running** is now a number on the session that delegated it — `×3` in the
row's title line, and a cut-out digit on the big bot's rim — and it gets no row. **Nothing is drawn for
`0`**: a session that delegated nothing is not a team of none.

| Keeps its own row | Why |
|---|---|
| a delegated session that is `attention` | it is blocked on the human; a number cannot be answered, and hiding a question is the one failure this board exists to prevent |
| a delegated session that `error`ed | the red badge is the board's own signal, and the tab must not count a failure whose row is nowhere |
| a delegated session whose parent is not published | folding into a row that does not exist would delete it |

A `done` child leaves the count and takes no row, so the number always means "running under this one
right now". A working grandchild counts toward the nearest ancestor that got a row.

**The folding is done by the HOST**, in `snapshot()`, not by the window: `summary` is computed from the
published rows, so the tab's counts and the rows on the board cannot disagree (contract rule 6). This is
state-file **schema 5** — one new row field, `subagents`.

The design was arrived at by rendering, not by describing. Nine treatments were photographed from a
fixture with a lead that has 1, 3, 7 and 0 children:

| treatment | verdict |
|---|---|
| child silhouettes behind the lead | **rejected** — at a 14 px bot the ghosts land *on* the face and read as a smudge |
| dots arced over the rim | **rejected** — fine at 3, unreadable at 1 (a speck) and colliding with the title at 6+ |
| a badge disc on the bot's shoulder | closest, but reads as a notification sticker borrowed from another app |
| a count in a right-hand column | legible, but costs title width and pulls the eye away from the title |
| **`×N` in the title line + a cut-out digit on the big bot** | **shipped** — the only option unambiguous at both scales |

The `×` is load-bearing: a bare trailing digit reads as part of the title ("Wrong direction at line 3"
would be a landmine). The rule that decided it is arithmetic — a 14 px bot has room for an 11 px disc,
and an 8 px digit inside that collides with the state ring, the aura, the title and the next row.

## 2b. And the bots' paint

The tab's bot is **16 px with a 1.7 px rim**. Every earlier bot was a white face inside a solid identity
rim, so while four sessions worked, the tab was four white dots with four thin rings — the identity colour
occupied about 6 % of the drawing.

Two rounds of "make it richer" failed for the same reason and it is arithmetic, not taste: **at 16 px a
gradient across 1.7 px is not there, and neither is a 9 % face tint.** Nine variants were photographed
before that sank in (`docs/design/icons/`). What survives that size is one large colour block, so:

| bot | face | rim |
|---|---|---|
| small (tab 16 px, rows 14 px) | 34 % white + the session's hue | the same colour, 45 % white — an edge, not a ring |
| big (60 px) | 91 % white + a hint of the hue, plus a sheen | a gradient, light from the upper left |

iArxic's rule, which settled it: *"we don't need the ring to be visible if the ring color is the same as
the head."* A bot is one coloured object with an edge; the ring was never the thing being drawn. The big
bot keeps the light because it is big enough to show it — **shade what is large, colour what is small.**

**The judging method is part of the fix.** A 4× still flatters a gradient and cannot answer "does this
read small", which is why three rounds of variants looked identical to the person actually using the
notch. Every comparison here is a 4× still **downscaled to true size**, shown beside the same pixels at
3–4× nearest-neighbour (`real-*.png`, `compare-*.png`, `ladder/`, `factorial-*.png`).

## 3. The other half, and why this release is safe without it

The real cost was on the *server* side of that route, in `$DSH_HOME/plugins/session-finished` (a
different repository — not shipped here). It was fixed the same day: the status route no longer awaits
an unbounded corpus listing, and one request decodes at most 2 logs inside a 1.2 s budget. That change
took the route from **>90 s to 5 ms** on the steady-state path.

**0.2.0 does not depend on that fix.** The notch's own guard and backoff are what stop the storm; if the
sibling plugin is still slow, 0.2.0 answers by waiting and backing off instead of piling up. Verified in
that state: with only the notch fix applied and the route still slow, the process stayed flat rather than
climbing.

## 4. Measured result

Same machine, same day, before and after:

| | 0.1.9 | 0.2.0 |
|---|---|---|
| Harness host CPU | 179–215 % of one core, sustained | **~35 %** |
| Harness host working set | 3–4.4 GB, rising to OOM | **~640 MB, flat** |
| Notch `finished-failed` events | ~8 per second | **0 in 15 minutes** |
| System RAM free | 3.6 GB (87.8 % used) | **8.4 GB** |

With the plugin disabled entirely the host sat at ~15 %, which is the floor this change gets close to.

## 5. Release checklist — three artefacts that must agree

`AGENTS.md` §4 is the rule: **npm package version → git tag → release asset**. For 0.2.0 the trap is
the middle one.

```powershell
# tests first: all five suites, one node process each
foreach ($s in 'manifest','state','host','fetch-exe','settings-store') { node "test\$s.test.mjs" }

dotnet build desktop\AgentsNotch.csproj -c Release        # must still be 0 errors
tools\publish-release.ps1 -DryRun                          # prints every step first
```

**The asset is now mandatory, not a temptation.** `releaseAssetUrl()` derives its URL from the package
version, so 0.2.0 makes the host look for `…/releases/download/v0.2.0/AgentsNotch.exe`. Because §2a
changed the window, the 0.1.9 bytes would draw **no count at all** — the old drawing has no `×N` in it —
so publishing the package against the old asset ships a window that silently ignores schema 5. Cut a
v0.2.0 release **with** the newly built `.exe` and its `.sha256`, or do not publish the package. (On the
developer's machine `state\agents-notch\window\0.2.0\` holds a copy of the 0.1.9 binary for local
convenience — that is exactly the case this paragraph forbids shipping.)

**Two gates that cannot be automated:** `npm publish` needs a human (the account has 2FA — an OTP inside
the 30-second window, or a granular token with bypass-2FA), and the release must be cut on the tag that
matches the version. `tools/publish-release.ps1` refuses a version/tag mismatch on purpose.

`test/manifest.test.mjs` enforces that `desktop/AgentsNotch.csproj` carries the same `<Version>` as
`package.json` — both are already `0.2.0`, which is why an otherwise host-only change touched the csproj.

## 6. Verified on this machine

- All five suites: manifest 5, state 43, host 19, fetch-exe 3, settings-store 5 = **75 tests, 0 fail**.
  The eight new state tests are the fold: a running child becomes a count and no row; `0` for a session
  that delegated nothing; an `attention` child, an `error` child, an orphan and a corrupt parent cycle
  each keep a row; a finished child leaves the count; a grandchild counts toward the lead.
- The schema test asserts the row's **exact** key set, so the payload cannot grow a field the contract
  does not name — `subagents` is in it, and `subagentsWaiting` (tried and dropped) is not.
- 0.2.0 installed into the live profile (`state\agents-notch\` byte-identical to the checkout) and the
  row mounted; the board published, the window spawned, and **zero `finished-failed` lines** for 15
  minutes afterwards — against ~8/second before.
- `retire` returned to `"route"` (the verdict is reachable again) and rows kept publishing.
- The exponential backoff was never exercised on the live machine after the server-side fix landed,
  because nothing failed. It is covered by the host test for the `retire: "timer"` transition, not by a
  live outage.
- The count was verified **by rendering**: nine treatments photographed from
  `desktop/fixtures/teams.json` (a lead with 1, 3, 7 and 0 children) at 4× device scale, the window's
  own `--shot` reading a schema-5 payload. The shipped one is `docs/design/teammates/10-shipped-crop.png`.
- **The published binary itself runs and draws the release.** `tools/publish.ps1` produced
  `dist/win-x64/AgentsNotch.exe` (65.1 MB, self-contained, one file, no strays), `FileVersion 0.2.0.0`,
  `ProductVersion 0.2.0+<commit>`, and a `.sha256` that matches the bytes. Run from `dist/` against
  `teams.json` it rendered `docs/design/icons/released-exe.png` — tinted bot faces, the `×3` / `×7`
  counts, the cut-out on the big bot — so the shipping artefact is the code that was reviewed, not just
  a debug build of it.
- One measurement worth keeping: the single-file exe **could not create**
  `%TEMP%\an-bundle-verify\` from inside this session ("Failed to create directory … for extracting
  bundled files", no window at all). Pointing `DOTNET_BUNDLE_EXTRACT_BASE_DIR` at a directory under the
  workspace made it run immediately. The host already spawns it under `<stateDir>\bundle` (§5.5 of
  `AGENTS.md`); this is the same trap reached by hand, and it fails *before* `Main`, so there is no log
  line to read.

## 7. Not verified — do not claim it

- **The fold against a real team.** The folding is tested at the reducer and end-to-end through
  `apply()`, and the *drawing* was verified from a fixture — but no live board has yet carried a session
  with a real delegated child at the moment this was written. Watch for a lead that shows `×N` with the
  wrong N, and for a child that should have surfaced (waiting on you) and did not.
- **A delegating session that is itself idle.** If the lead's record has no events and its children are
  working, the children surface individually rather than folding — the safe direction, and deliberate
  (`rootOf` returns `""`), but not something a human has looked at yet.
- **A live failure after the fix.** The path that matters (a probe that times out repeatedly) was
  measured at 0.1.9 and is prevented by construction at 0.2.0, but it has not been reproduced against
  0.2.0 with the route deliberately broken. Watch `host-report.jsonl` for `finished-failed` and
  `retire-fallback` on the first slow machine.
- **The 12-second windows.** The 5 s listing reuse and 30 s backoff ceiling were chosen from the tick
  period and the caller's 2 s timeout, not from a long soak.
- **Multi-monitor / mixed DPI, tray quit, fullscreen hiding** — unchanged from 0.1.9's "not verified"
  list in `AGENTS.md` §6.

## 8. Rollback

Both halves changed, so a rollback is the host module **and** the window:

```powershell
# 0.1.9 is intact in the checkout and on npm
git checkout v0.1.9 -- lib/host.js package.json desktop/AgentsNotch.csproj
```

The running app caches a module by its URL (`AGENTS.md` §5.1), so a rolled-back entry needs the app
restarted before it takes effect — `lib/host-0.1.9.js` deliberately still exists in the tree for this.
A 0.1.9 host publishes schema 4, which a 0.2.0 **window** still draws correctly: it sees subagent rows
(it dims them), ignores `subagents`, and shows no count. The halves may be one schema apart by design;
what must not be mixed is a 0.1.9 *window* with a 0.2.0 host, because that window draws no count at all.
