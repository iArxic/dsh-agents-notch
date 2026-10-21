<#
.SYNOPSIS
  Publish this plugin: push the repository, tag the version, cut the GitHub release with the window
  binary, and publish the npm package. Idempotent — every step says what it found and skips cleanly.

.DESCRIPTION
  The three artefacts have to agree, or an install fails for a reason nobody can see:

    npm package 0.1.1  ->  tag v0.1.1  ->  release asset AgentsNotch.exe (+ .sha256)

  The host's auto-fetch derives its download URL as `v<package version>`, so a version bump with no
  release installs for nobody; and the asset must be named exactly `AgentsNotch.exe`, because that is
  the file the installer looks for.

  What needs a human, once:
    * the repository must exist (GitHub cannot create one from a push);
    * `gh` is not used and no token is stored here, so the RELEASE needs either
        - a token:  $env:GITHUB_TOKEN = 'ghp_…'   (classic, `repo` scope), or
        - the web UI, by hand: the script prints exactly what to upload;
    * `npm login` once (or `npm config set //registry.npmjs.org/:_authToken …`).

.EXAMPLE
  pwsh -File tools/publish-release.ps1 -DryRun        # show every step, change nothing
.EXAMPLE
  $env:GITHUB_TOKEN = 'ghp_…'; pwsh -File tools/publish-release.ps1
#>
[CmdletBinding()]
param(
	[string] $Token = $env:GITHUB_TOKEN,
	[string] $Remote = 'origin',
	[switch] $DryRun,
	[switch] $SkipNpm,
	[switch] $SkipRelease
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$manifest = Get-Content (Join-Path $root 'package.json') -Raw | ConvertFrom-Json
$version = $manifest.version
$tag = "v$version"
$repoUrl = $manifest.repository.url -replace '^git\+', ''
$slug = ($manifest.repository.url -replace '.*github\.com[/:]', '' -replace '\.git$', '')
$exe = Join-Path $root 'dist/win-x64/AgentsNotch.exe'
$hash = "$exe.sha256"

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Note($text) { Write-Host "   $text" }
function Do($block) { if ($DryRun) { Note "(dry run) $block" } else { & ([scriptblock]::Create($block)) } }

Step "package $($manifest.name) $version -> tag $tag -> release $slug"

# ---------------------------------------------------------------- preflight
$dirty = (git status --porcelain) -join "`n"
if ($dirty -ne '') { throw "the working tree is dirty — commit first:`n$dirty" }
$head = (git rev-parse --short HEAD).Trim()
Note "HEAD $head, tree clean"

if (-not (Test-Path $exe)) { throw "no $exe — run: npm run window:publish" }
$sha = ((Get-FileHash $exe -Algorithm SHA256).Hash).ToLowerInvariant()
Note "release asset $([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB, sha256 $($sha.Substring(0,12))…"

$remotes = git remote
if ($remotes -notcontains $Remote) {
	Step "remotes"
	Do "git remote add $Remote $repoUrl"
	Note "added $Remote -> $repoUrl"
} else {
	Note "remote $Remote already points at $(git remote get-url $Remote)"
}

# ---------------------------------------------------------------- push + tag
Step 'push the branch'
try { Do "git push $Remote HEAD" ; Note 'pushed' } catch { Note "push failed: $($_.Exception.Message)" }

Step "tag $tag"
if ((git tag --list $tag) -eq $tag) {
	Note "tag exists locally"
} else {
	Do "git tag -a $tag -m 'Agents Notch $version'"
	Note 'tagged'
}
try { Do "git push $Remote $tag"; Note 'tag pushed' } catch { Note "tag push failed: $($_.Exception.Message)" }

# ---------------------------------------------------------------- GitHub release
if (-not $SkipRelease) {
	Step "GitHub release $tag"
	if ([string]::IsNullOrWhiteSpace($Token)) {
		Note 'no $env:GITHUB_TOKEN — create it by hand:'
		Note "  1. https://github.com/$slug/releases/new?tag=$tag"
		Note "  2. Drag in BOTH files from dist\win-x64:  AgentsNotch.exe  and  AgentsNotch.exe.sha256"
		Note "  3. Publish. The asset name must stay AgentsNotch.exe."
	} else {
		$headers = @{ Authorization = "Bearer $Token"; 'User-Agent' = "agents-notch/$version"; Accept = 'application/vnd.github+json' }
		$api = "https://api.github.com/repos/$slug"
		if ($DryRun) {
			Note "(dry run) POST $api/releases  +  upload AgentsNotch.exe and .sha256"
		} else {
			$existing = $null
			try { $existing = Invoke-RestMethod -Uri "$api/releases/tags/$tag" -Headers $headers -TimeoutSec 20 } catch { $existing = $null }
			if ($existing) {
				Note "release already exists (id $($existing.id), $($existing.assets.Count) asset(s))"
			} else {
				$body = @{ tag_name = $tag; name = "Agents Notch $version"; body = (Get-Content (Join-Path $root 'CHANGELOG.md') -Raw); draft = $false; prerelease = $false } | ConvertTo-Json
				$existing = Invoke-RestMethod -Uri "$api/releases" -Method Post -Headers $headers -Body $body -ContentType 'application/json' -TimeoutSec 30
				Note "created release $($existing.html_url)"
			}
			foreach ($file in @($exe, $hash)) {
				$name = Split-Path $file -Leaf
				if ($existing.assets.name -contains $name) { Note "$name already uploaded"; continue }
				$uploadType = 'application/octet-stream'
				$uri = "https://uploads.github.com/repos/$slug/releases/$($existing.id)/assets?name=$name"
				$bytes = [System.IO.File]::ReadAllBytes($file)
				Invoke-RestMethod -Uri $uri -Method Post -Headers $headers -Body $bytes -ContentType $uploadType -TimeoutSec 600 | Out-Null
				Note "uploaded $name"
			}
		}
	}
}

# ---------------------------------------------------------------- npm
if (-not $SkipNpm) {
	Step "npm publish $($manifest.name)@$version"
	$who = $null
	try { $who = (npm whoami 2>$null | Out-String).Trim() } catch { $who = $null }
	if ([string]::IsNullOrWhiteSpace($who)) {
		Note 'not logged in — run `npm login` (opens the browser) and re-run this script.'
	} elseif ($DryRun) {
		Note "(dry run) would publish as $who"
	} else {
		npm publish --access public
		Note "published as $who"
	}
}

Step 'verify (do this after a real publish)'
Note "npm view $($manifest.name) version"
Note "pwsh -File tools/install-window.ps1 -Url https://github.com/$slug/releases/download/$tag/AgentsNotch.exe"
Note 'and on a machine that already has the plugin: remove the installed window and watch it re-fetch.'
