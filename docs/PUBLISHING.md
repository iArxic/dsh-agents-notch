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
   --title "Agents Notch 0.1.0" --notes-file notes.md     # this version's CHANGELOG section only
```

`--notes-file CHANGELOG.md` would paste every version's notes into one release. Cut the section out
first — `v0.1.7` and `v0.1.8` both carry only their own:

```powershell
node -e "const f=require('fs'),m=f.readFileSync('CHANGELOG.md','utf8').replace(/\r\n/g,'\n');const a=m.indexOf('## 0.1.8'),b=m.indexOf('## 0.1.7');f.writeFileSync('out/notes.md',m.slice(a,b).trim()+'\n')"
```

**In a sandboxed agent session, `tools/publish-release.ps1` cannot push or call the API.** Its `git
push` dies on `schannel: AcquireCredentialsHandle failed` (and the credential helper cannot spawn under
the sandbox: `couldn't create signal pipe`), and its `Invoke-RestMethod` fails with `The SSL connection
could not be established`. `gh` is unaffected — it carries its own TLS stack. So in that session, push
with git's OpenSSL backend plus an auth header from gh's token, then let `gh` cut the release:

```powershell
$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("x-access-token:$(& gh auth token)"))
git -c http.sslBackend=openssl -c "http.extraheader=Authorization: Basic $b64" push origin master
git -c http.sslBackend=openssl -c "http.extraheader=Authorization: Basic $b64" push origin v0.1.8
gh release create v0.1.8 dist/win-x64/... --notes-file out/notes.md
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

**A fresh release does not install through the harness yet.** The app installs plugins with pnpm under
`minimumReleaseAge`, so a version published minutes ago is skipped and an older one is installed
instead. Measured with 0.1.6: the plugin manager installed 0.1.1, the only version on the profile's
`minimumReleaseAgeExclude` list in `profiles/<profile>/pnpm-workspace.yaml`. To test a release the
same day, uninstall it in the app and install the exact `dsh-agents-notch@<version>`; asking for an
exact version is what adds it to that list. Otherwise wait until the version is old enough.

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
