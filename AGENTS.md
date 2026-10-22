# AGENTS.md — working in `dsh-agents-notch`

A DeepSeek Harness plugin that draws an always-on-top notch at the top of a Windows screen: one bot per
session, showing which are **working**, which **need you**, and which are **done**. Read
[README.md](README.md) for what it is, [docs/CONTRACT.md](docs/CONTRACT.md) for the state contract that
the two halves obey, and [docs/INTERNALS.md](docs/INTERNALS.md) for the engineering record behind the
decisions. This file is the operating manual: how to change something here, and which mistakes have
already been paid for.

Published as `dsh-agents-notch` on npm; the window binary is a GitHub release asset. Repo:
<https://github.com/iArxic/dsh-agents-notch>.

---

## 1. The three parts, and which one you are editing

| part | language | lives in | runs where |
|---|---|---|---|
| **host** (plugin) | JavaScript, ESM, Node ≥ 22 | `lib/*.js` | inside the harness (Electron) process |
| **window** | C# / WPF, `net9.0-windows` | `desktop/*.cs` | its own process, spawned by the host |
| **pane** (browser half) | hand-written client bundle | `lib/client.js` | the app's renderer, in a Settings page |

The host writes `state.json`; the window reads it and draws. The host never draws, and the window never
decides what is true — that split is the whole architecture, and every "the notch shows something wrong"
question is answered by looking at `state.json` first.

```
lib/host-0.1.6.js   the entry the plugin row mounts (see the cache trap, §5.1)
lib/host.js         re-export shim: the stable import path for tests and readers
lib/state.js        pure reducer: events -> session records -> the published rows (schema 3)
lib/settings-store.js  the three settings layers, one clamp (defaults < row config < pane file)
lib/fetch-exe.js    downloads the release asset on first use, verifies the sibling .sha256
lib/exe.js          where the window binary is looked for, and the "no window" fix text
lib/client.js       Settings -> Agents Notch
desktop/Program.cs  the window: placement, drawing, animation, tray, --shot/--freeze
desktop/Settings.cs parsing and clamping of the settings the window receives
desktop/fixtures/*.json  state files a still can be rendered from, one per state
tools/publish.ps1          single-file window build + .sha256
tools/install-window.ps1   install the window from a release URL, a path, or dist/
tools/publish-release.ps1  push, tag, GitHub release + assets, npm publish
tools/design-banner*.ps1   the old banner and its variants (GDI+, no browser)
tools/readme-media.py      every README picture: stills (`shots`), the demo film (`record`), layout (`art`)
```

## 2. The loop

```powershell
# tests: one node process per file; there is no harness in them and they never touch the network
foreach ($s in 'manifest','state','host','fetch-exe','settings-store') { node "test\$s.test.mjs" }

dotnet build desktop\AgentsNotch.csproj -c Release          # 0 errors before anything else

# a still of one state, at one moment of its animation, from a fixture
desktop\bin\Release\net9.0-windows\AgentsNotch.exe --state desktop\fixtures\working.json --open `
    --shot out.png --freeze 600
```

`--freeze <ms>` pins the animation clock (0 = the transition frame, 400 ≈ the squint, 2600+ = settled);
`--open` draws the board instead of just the tab; `--probe-rect` prints the resolved placement as JSON;
`--log <file>` writes the window's own account of what it drew. **Always read the log** — the window
says `board drawn — tab "…" title "…" sub "…"` and, on a state change, `pose — front a|done -> b|done`.

`--record <dir>` writes one PNG per frame on a clock that advances one frame per frame written (a 2×
render of the board takes ~0.5 s, which on the real clock made a 2 fps film and starved the poll timer);
`--open-at`/`--close-at <ms>` unfold and fold the board on that clock, as a hover would.
`tools/readme-media.py` drives it and regenerates every picture in the README — Pillow only lays the
window's own renders onto a backdrop. Two things it learned: `since` must be an **integer** (the window
reads `1.79e12` written as a float as `0`, so every age says `0s` and the spotlight never fires), and an
open board pins its front row, so a story whose finish should reach the big bot has to finish the row
that is already in front.

Live work on the machine:

```powershell
# install/uninstall the bundle live (the app's own manager; the dsh CLI cannot write this profile)
#   plugin_manager install_bundle  target: C:\Users\kubav\Projects\dsh-agents-notch
#   plugin_manager remove_bundle   target: dsh-agents-notch
Get-Content $env:USERPROFILE\.dsh\state\agents-notch\host-report.jsonl -Tail 8   # one line per event
Invoke-WebRequest http://127.0.0.1:<webPort>/agents-notch/state                  # the published payload
Invoke-WebRequest http://127.0.0.1:<webPort>/agents-notch/settings               # the pane's door
```

The web port is the harness's own (`Current runtime context` names its GUI URL; the host publishes the
route on the same server). Time is measured, never assumed: `wallMs` in `state.json` is the heartbeat.

## 3. Rules this repo holds itself to

- **A percentage needs a budget.** `context.budget` is `null` when the route's window is unknown, and the
  window then shows tokens, not a fabricated percentage.
- **A time needs a timestamp.** "finished 2m ago" is computed from `since` + `wallMs`, and says `stale`
  when the heartbeat is old.
- **Nothing is invented to fill a field.** `""` / `0` / `null` mean "not known here", and nothing is
  drawn for them rather than a placeholder.
- **A setting that changes WHICH rows exist is applied by the host**, never by the window (`showDone`).
- **The pane writes `state/agents-notch/settings.json`, never the row.** A browser cannot safely
  round-trip `cordis.patch.yml`; the row stays the human's file.
- Every claim in a commit message or a reply is backed by a command you actually ran.

## 4. Release shape

Three artefacts have to agree or an install fails invisibly:

```
npm package 0.1.x  ->  git tag v0.1.x  ->  release asset AgentsNotch.exe + .sha256
```

The host's auto-fetch derives its URL as `v<package version>`, so bumping the package without cutting
the matching release installs for nobody. `tools/publish-release.ps1 -DryRun` prints every step;
`docs/PUBLISHING.md` is the runbook. **npm publish needs a human** (the account has 2FA — either an OTP
in the 30-second window or a granular token with "bypass 2FA").

## 5. Traps — each of these cost real time

### 5.1 The loader caches a module by its URL

Re-applying a plugin row serves the module the app already imported. Editing `lib/host.js` and
re-mounting changes **nothing** — measured twice, once as a settings route that answered `404` while the
code sat in the file. To land code without restarting the app: give the entry a **new file name** and
point `package.json`'s `main` at it (that is why the entry is `host-0.1.6.js`), keeping `lib/host.js` as
a re-export shim for the imports. A `?v=` query on a row's `name` makes the import fail outright.

### 5.2 A duplicate JSON key deletes a feature silently

`package.json` once had **two** `exports` blocks; JSON keeps the last, and the last had no `./client`, so
`dsh-client-modules` could not resolve the browser half: the package installed, the plugin mounted, the
host route answered, and Settings → Agents Notch simply never appeared. No error anywhere, and
`npm publish` reported success. `test/manifest.test.mjs` counts `"exports"` blocks in the raw text
(a JSON parser hides the bug by construction) and checks every promised file resolves.

### 5.3 Identity vs state — two different questions

The window keys its *transition* on the front session's `id|status` (so switching between two finished
sessions replays the finish motion) and its *pose* on the status alone. Keying both on the identity made
every pose branch compare `"session-a|done"` to `"done"` and match nothing: no state ring, no `^^` eyes,
no error — the face just fell back to the neutral idle pose. If a face animation "does not happen", check
that the branch is looking at `_data.FrontStatus`, not `FrontKey`.

### 5.4 Rendering a still has two preconditions

- **The named mutex must be free.** A second window exits with `another instance already owns the notch`
  and writes no PNG. Stop the live window first (`Get-Process AgentsNotch | Stop-Process -Force`); the
  host respawns it within ~15 s, so a render takes seconds or none.
- **The output path must be writable by the session.** Under a file sandbox the write fails with
  `Access to the path … is denied` inside the window's log, which reads like a rendering bug. Write stills
  into the workspace, then move them.

### 5.5 The single-file exe needs a writable unpack directory

A published `AgentsNotch.exe` extracts its native libraries **before `Main`**. With the default
`%TEMP%\.net\AgentsNotch`, this machine refused (error 5) and the process died with no output at all.
The host therefore spawns it with `DOTNET_BUNDLE_EXTRACT_BASE_DIR=<stateDir>\bundle`. Running the
published exe by hand without that variable reproduces the silent death.

### 5.6 `.git` in this folder has been damaged twice

Both times `HEAD`, `config` and `index` vanished while `objects/` and `refs/` survived, shortly after
plugin install/remove activity pointing at the directory. Recovery without data loss:

```powershell
"ref: refs/heads/master`n" | Set-Content .git\HEAD -NoNewline
# then a minimal [core] + [remote "origin"] config, then:
git reset            # rebuilds the index; add `git config core.autocrlf false` if everything looks modified
```

The remote is the safety net: `git fetch && git reset --hard origin/master`.

## 6. State of the work, and what is still unverified

Verified on this machine: 55 tests; the host fetching its own window from the release (hash checked);
`install-window.ps1` from a URL; both plugin install paths (registry and local link); the settings route
(GET/POST/clamp/reset/unknown-key); the window's placement, states and stills of every state.

**Not verified, do not claim it:** the tray *Quit overlay* click (host-side unit test only), fullscreen
hiding (needs a full-screen app), mixed-DPI / multi-monitor (this box is one 1920×1080 at 120 DPI), and
whether the Settings pane renders correctly in the app until a human reloads and looks.

## 7. Where to look first when something is wrong

| symptom | look at |
|---|---|
| no notch at all | `host-report.jsonl` — `window-missing`, `window-fetch*`, `window-exit` |
| a row shows the wrong state | `state.json` (`sessions[]`), then `lib/state.js` and the session log's events |
| the face does not animate / no ring | §5.3, then `--log` with `--freeze` |
| the pane is missing from Settings | §5.2, then `dsh.client`/`exports` in the installed `package.json` |
| a settings change does nothing | `GET /agents-notch/settings` — does `settings` differ from `stored`? |
| an install on a fresh machine fails | §4: package version, tag, and asset name must agree |
