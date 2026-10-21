<#
.SYNOPSIS
  Install the Agents Notch window binary into the one place the host looks for it.

.DESCRIPTION
  The plugin on npm is JavaScript; the notch is a native Windows executable that is published as a
  release asset, because a 70 MB binary does not belong in an npm tarball. This script puts that
  binary where the host expects it:

      $DSH_HOME\state\agents-notch\window\<version>\AgentsNotch.exe

  The host looks there BEFORE its own development build (see lib/exe.js), so installing here also
  overrides a stale `desktop/bin/Release` checkout.

  Sources, in the order they are tried:
    -FromPath <dir-or-exe>   a directory holding AgentsNotch.exe, or the exe itself
    -Url <url>               an .exe or a .zip from a release
    (neither given)          ..\dist\win-x64 next to this package, i.e. a `npm run window:publish`
                             in a source checkout

  A `<file>.sha256` next to the source is verified when present; a mismatch aborts and installs
  nothing. Without one the script says so, once, and installs anyway — a hash that is not published
  cannot be checked, and pretending otherwise would be worse than saying it.

.EXAMPLE
  # from a source checkout, after `npm run window:publish`
  pwsh -File tools/install-window.ps1

.EXAMPLE
  # from the published release
  pwsh -File tools/install-window.ps1 -Url https://github.com/<owner>/dsh-agents-notch/releases/download/v0.1.0/AgentsNotch.exe
#>
[CmdletBinding()]
param(
	[string] $FromPath,
	[string] $Url,
	[string] $StateDir,
	[switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$packageRoot = Split-Path -Parent $PSScriptRoot
$version = (Get-Content (Join-Path $packageRoot 'package.json') -Raw | ConvertFrom-Json).version

if (-not $StateDir) {
	$dshHome = if ($env:DSH_HOME) { $env:DSH_HOME } else { Join-Path $env:USERPROFILE '.dsh' }
	$StateDir = Join-Path $dshHome 'state/agents-notch'
}
$targetDir = Join-Path $StateDir "window/$version"
$target = Join-Path $targetDir 'AgentsNotch.exe'

function Get-Sha256([string] $path) {
	(Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Install-Exe([string] $source, [string] $hashFile) {
	if (-not (Test-Path $source)) { throw "no such file: $source" }
	if ((Test-Path $hashFile) -and -not $Force) {
		$expected = ((Get-Content $hashFile -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
		$actual = Get-Sha256 $source
		if ($expected -ne $actual) {
			throw "SHA256 mismatch for $source`n  expected $expected`n  actual   $actual`nnothing was installed."
		}
		Write-Host "sha256 ok: $actual"
	} else {
		Write-Warning "no .sha256 beside $([System.IO.Path]::GetFileName($source)) - installing unverified (sha256 $((Get-Sha256 $source).Substring(0,16))...)"
	}
	New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
	Copy-Item -Path $source -Destination $target -Force
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("agents-notch-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
try {
	if ($FromPath) {
		$item = Get-Item -Path $FromPath
		if ($item.PSIsContainer) {
			Install-Exe (Join-Path $item.FullName 'AgentsNotch.exe') (Join-Path $item.FullName 'AgentsNotch.exe.sha256')
		} else {
			Install-Exe $item.FullName ($item.FullName + '.sha256')
		}
	} elseif ($Url) {
		$name = [System.IO.Path]::GetFileName(([uri] $Url).AbsolutePath)
		$downloaded = Join-Path $staging $name
		Write-Host "downloading $Url"
		Invoke-WebRequest -Uri $Url -OutFile $downloaded -UseBasicParsing
		if ($name -like '*.zip') {
			Expand-Archive -Path $downloaded -DestinationPath $staging -Force
			$exe = Get-ChildItem $staging -Recurse -Filter 'AgentsNotch.exe' | Select-Object -First 1
			if (-not $exe) { throw "the archive has no AgentsNotch.exe: $Url" }
			Install-Exe $exe.FullName ($exe.FullName + '.sha256')
		} else {
			# The hash is a SECOND release asset, so fetching the exe over HTTP does not bring it
			# along. Ask for it at `<url>.sha256` — that is where a release puts it — and let
			# Install-Exe warn if the release has no such asset. Measured: without this, an install
			# from a URL was always "unverified", which is the one path the README promises verifies.
			$sibling = $downloaded + '.sha256'
			if (-not (Test-Path $sibling)) {
				try {
					Invoke-WebRequest -Uri ($Url.TrimEnd('/') + '.sha256') -OutFile $sibling -UseBasicParsing -ErrorAction Stop
				} catch {
					Write-Host "the release publishes no .sha256 next to the exe; continuing unverified"
				}
			}
			Install-Exe $downloaded $sibling
		}
	} else {
		$local = Join-Path $packageRoot 'dist/win-x64'
		if (-not (Test-Path (Join-Path $local 'AgentsNotch.exe'))) {
			throw @"
No -SourcePath and no -Url, and no published build beside this package:
  $local

Either build one (npm run window:publish) and re-run, or name the release asset:
  pwsh -File tools/install-window.ps1 -Url <release asset url>
"@
		}
		Install-Exe (Join-Path $local 'AgentsNotch.exe') (Join-Path $local 'AgentsNotch.exe.sha256')
	}
} finally {
	Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "installed $target"
Write-Host "version   $version"
Write-Host "size      $([math]::Round((Get-Item $target).Length / 1MB, 1)) MB"
Write-Host ""
Write-Host "The host picks it up on its next supervision tick (a few seconds), no restart needed."
Write-Host "If it does not appear, read $StateDir\host-report.jsonl - the newest line says why."
