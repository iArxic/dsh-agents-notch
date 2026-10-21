<#
.SYNOPSIS
  Publish the window as one self-contained executable, with a hash beside it.

.DESCRIPTION
  The window is a native Windows binary; the plugin on npm is JavaScript. A stranger who installs
  the plugin should not have to install a .NET runtime as well, so the release build is
  self-contained: one file, no prerequisites, ~70 MB, extracted to a temp directory by the .NET
  host on first run (single-file + compression).

  It writes exactly what a release needs:

      dist/win-x64/AgentsNotch.exe
      dist/win-x64/AgentsNotch.exe.sha256     <- what tools/install-window.ps1 verifies

  and it fails loudly if anything else appears beside them, because "one file" is a promise the
  installer depends on.

.PARAMETER Output
  Output directory, relative to the repository root. Default `dist/win-x64`.

.PARAMETER Runtime
  RID to publish for. Default `win-x64`. `win-arm64` is the other one worth building.

.PARAMETER Reference
  Framework-dependent publish (`.NET 9 desktop runtime` required on the target) instead of a
  self-contained one: ~150 KB instead of ~70 MB. For a development machine only.

.PARAMETER NoCompress
  Skip compression inside the single file. Faster to start, roughly 2.5x the size.
#>
[CmdletBinding()]
param(
	[string] $Output = 'dist/win-x64',
	[string] $Runtime = 'win-x64',
	[switch] $Reference,
	[switch] $NoCompress
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'desktop/AgentsNotch.csproj'
$destination = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }
$staging = "$destination.dist"

if (-not (Test-Path $project)) { throw "no project at $project" }

$arguments = @(
	'publish', $project,
	'-c', 'Release',
	'-r', $Runtime,
	'--self-contained', $(if ($Reference) { 'false' } else { 'true' }),
	'-p:PublishSingleFile=true',
	# WPF's native dependencies (wpfgfx, PenImc, D3DCompiler, vcruntime) are NOT bundled by
	# PublishSingleFile alone — measured: a release without this left five DLLs beside the exe, which
	# is a second file to install and a second file to get wrong.
	'-p:IncludeNativeLibrariesForSelfExtract=true',
	'-p:DebugType=None',
	'-p:DebugSymbols=false',
	'-p:SatelliteResourceLanguages=en',
	'-o', $staging
)
if (-not $NoCompress -and -not $Reference) { $arguments += '-p:EnableCompressionInSingleFile=true' }

Write-Host "dotnet $($arguments -join ' ')"
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $staging 'AgentsNotch.exe'
if (-not (Test-Path $exe)) { throw "publish produced no AgentsNotch.exe in $staging" }

# "One file" is the install contract: anything else beside it is a build mistake worth stopping on.
$strays = Get-ChildItem $staging -File | Where-Object { $_.Name -notin @('AgentsNotch.exe') }
if ($strays) {
	$names = ($strays | ForEach-Object Name) -join ', '
	throw "the publish left files beside the exe ($names). A release is ONE file: check that PublishSingleFile took effect and that no native library needs IncludeNativeLibrariesForSelfExtract."
}

New-Item -ItemType Directory -Force -Path $destination | Out-Null
Get-ChildItem $destination -File -ErrorAction SilentlyContinue | Remove-Item -Force
Move-Item -Path $exe -Destination (Join-Path $destination 'AgentsNotch.exe') -Force
Remove-Item -Recurse -Force $staging

$published = Join-Path $destination 'AgentsNotch.exe'
$hash = (Get-FileHash -Path $published -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$published.sha256" -Value "$hash  AgentsNotch.exe" -Encoding ascii -NoNewline

$size = [math]::Round((Get-Item $published).Length / 1MB, 1)
Write-Host ""
Write-Host "published $published"
Write-Host "  runtime $Runtime$(if ($Reference) { ' (framework-dependent)' } else { ' (self-contained)' })"
Write-Host "  size    $size MB"
Write-Host "  sha256  $hash"
Write-Host ""
Write-Host "Install it where the host looks: pwsh -File tools/install-window.ps1 -FromPath $destination"
