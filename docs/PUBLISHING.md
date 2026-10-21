# Cutting a release

The build is finished; this is the checklist that turns it into something a stranger can install.
Nothing here is done yet — it is the order that avoids publishing something half-resolvable.

## 0. What already exists

- `npm run window:publish` → `dist/win-x64/AgentsNotch.exe` (65 MB, self-contained) + `.sha256`.
- `npm pack` → a 14-file tarball, no binary (`dist/` and `desktop/bin/` are gitignored).
- `tools/install-window.ps1` verifies the hash and installs into
  `$DSH_HOME/state/agents-notch/window/<version>/`.
- Both halves mount live from a local path **and** from a packed tarball; both were verified on
  DSH 0.2.1-rc.2.

## 1. Fill in the identity (before anything is published)

`package.json` has no `repository`, `homepage`, `bugs` or `author`, because the repository does not
exist yet. Add all four — npm uses `repository` to resolve the README's relative image paths, so
without it the package page shows broken pictures:

```json
"repository": { "type": "git", "url": "git+https://github.com/<owner>/dsh-agents-notch.git" },
"homepage": "https://github.com/<owner>/dsh-agents-notch#readme",
"bugs": { "url": "https://github.com/<owner>/dsh-agents-notch/issues" },
"author": "<owner>"
```

## 2. Create the repository and push

Everything below from here to the npm publish is one script, once the repo exists and one of the
credentials is in place:

```powershell
pwsh -File tools/publish-release.ps1 -DryRun      # every step, changes nothing
$env:GITHUB_TOKEN = 'ghp_…'                       # classic token, `repo` scope
pwsh -File tools/publish-release.ps1              # push, tag, release + assets, npm publish
```

It refuses to run on a dirty tree, refuses to guess the version (tag = `v<package version>`), skips
anything already done, and prints the by-hand Upload instruction when no token is available.

```powershell
git remote add origin https://github.com/<owner>/dsh-agents-notch.git
git push -u origin master
```

Tag the plugin with the GitHub topic `dsh-plugin` so the directories
([awesome-deepseek-harness](https://github.com/awesome-deepseekharness/awesome-deepseek-harness),
dshfind) can find it.

## 3. Cut the window release

```powershell
npm run window:publish                                   # fresh dist/win-x64 + hash
gh release create v0.1.0 dist/win-x64/AgentsNotch.exe `
   dist/win-x64/AgentsNotch.exe.sha256 `
   --title "Agents Notch 0.1.0" --notes-file CHANGELOG.md
```

**The tag must be `v<version>`, exactly the version in `package.json`.** The host's auto-fetch derives
its URL as `v<package version>`; a release tagged `v0.2` for package `0.1.2` installs for nobody and
falls back to the by-hand installer. If you bump the package without cutting a release, that is what
new installs will do — the fetch 404s, says so in `host-report.jsonl`, and prints the manual command.
The asset name must stay `AgentsNotch.exe`: that is the name `install-window.ps1` fetches and the
name the host looks for. The README's `-Url` example then works as written.

## 4. Publish the package

```powershell
npm publish --access public
```

Then re-read the npm page: the four images must render (they resolve through `repository`), and the
install block must match what `npm i` actually produced. If the images are broken, `repository` is
wrong — that is the cause, not the Markdown.

## 5. Verify the two install paths a stranger will take

```powershell
# a) the release asset, hash verified
pwsh -File tools\install-window.ps1 -Url https://github.com/<owner>/dsh-agents-notch/releases/download/v0.1.0/AgentsNotch.exe

# b) the registry package, in a scratch profile
dsh plugin --profile web add dsh-agents-notch
```

Both are one command each and both were exercised locally from the tarball and from
`dist/win-x64`; the `-Url` branch is the one that has never run against a real release, so run it
first.

## 6. Optional, but what "maintained" looks like from outside

- `.github/workflows/test.yml` running `npm test` (there is no CI today, so the README's test line is
  a promise rather than a check).
- A version badge and a CI badge, once there is something behind them. Today the README shows only
  static ones (`platform-Windows`, `license-MIT`) on purpose: a version badge pointing at a 404 is
  worse than no badge.
- Publishing from Actions with npm OIDC (`id-token: write`) gives SLSA provenance, which three of
  the comparable DSH plugins display.
- `dsh.compatibility.dshReleases` in `package.json` — the shape is a map of exact DSH versions to
  `"compatible"` (see `dsh-notify-me` and `dsh-context`). It is deliberately absent: it is a GATE,
  and declaring only the one version that has been tested would refuse installs on builds that very
  probably work. Add it — and widen it — only after testing those builds.
