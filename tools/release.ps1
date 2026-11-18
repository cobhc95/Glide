<#
.SYNOPSIS
  Scripted GitHub release for Glide (replaces the per-version docs/history/PUBLISH-COMMANDS-*.md copies).

.DESCRIPTION
  Dry run by default: every step is checked and printed, nothing is written outside the scratch report, nothing is
  committed, tagged, pushed or uploaded. Pass -Publish to do it for real; push and `gh release create` never run without
  -Publish. (The script also honours -WhatIf, which forces a dry run even with -Publish.)

  Steps
    1. Version from Directory.Build.props (or -Version); release notes docs/releases/release-notes-<v>.md must exist.
    2. Assets in -ReleaseDir: Glide.Setup.exe (or "Glide Setup.exe", copied to Glide.Setup.exe), Glide-<v>-Portable.zip.
       SHA256 of the installer -> Glide.Setup.sha256; SHA256_PLACEHOLDER in the notes is replaced.   [-Publish only]
    3. Privacy grep over the files that would be published: personal paths (C:\Users\<name>, /Users/<name>), the
       Windows user name as a word, and any -PrivacyPattern. Any hit stops the run.
    4. Mirror the project into the clean clone (-Clone) with the standard exclusions (robocopy /MIR; /L in a dry run).
    5. git status in the clone; commit "Glide <v>", tag v<v>, push main + tag, `gh release create` with the assets.  [-Publish only]
    6. Download the published assets and verify the installer hash.                                                 [-Publish only]

  Hand checks (release notes review, screenshots, update check against the published build) stay with the owner.

.EXAMPLE
  powershell -NoProfile -File tools\release.ps1 -Clone C:\Temp\glide-repo -Title "Glide Image Viewer: ..."
  powershell -NoProfile -File tools\release.ps1 -Clone C:\Temp\glide-repo -Title "..." -Publish
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$Clone,
    [string]$Version,
    [string]$ReleaseDir,
    [string]$Title,
    [string]$Repo = 'cobhc95/Glide',
    [string[]]$PrivacyPattern = @(),
    [switch]$Draft,
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$live = $Publish -and -not $WhatIfPreference
$mode = if ($live) { 'PUBLISH' } else { 'DRY RUN (pass -Publish to act)' }
Write-Host "Glide release - $mode" -ForegroundColor Cyan

function Step([string]$name) { Write-Host "== $name" -ForegroundColor Cyan }
function Fail([string]$message) { Write-Host "STOP: $message" -ForegroundColor Red; exit 1 }
function Run([string]$what, [scriptblock]$body) {
    if ($live) { Write-Host "   run: $what"; & $body; if ($LASTEXITCODE) { Fail "$what (exit $LASTEXITCODE)" } }
    else { Write-Host "   would run: $what" -ForegroundColor Yellow }
}

# 1. Version and notes ------------------------------------------------------------------------------------------------
Step 'Version and release notes'
if (-not $Version) {
    $props = [xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
    $Version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "version '$Version' is not x.y.z" }
$tag = "v$Version"
$notes = Join-Path $root "docs\releases\release-notes-$Version.md"
if (-not (Test-Path $notes)) { Fail "missing $notes" }
if ((Get-Item $notes).Length -ge 125000) { Fail 'release notes are over the GitHub 125,000 character limit' }
if (-not $Title) { if ($live) { Fail '-Title is required to publish (the release title has no version number)' } else { $Title = '<title>' } }
Write-Host "   version $Version, tag $tag, notes $(Split-Path -Leaf $notes)"

# 2. Assets -----------------------------------------------------------------------------------------------------------
Step 'Assets and SHA256'
if (-not $ReleaseDir) { $ReleaseDir = Join-Path $root "dist-installer\release-$Version" }
$setup = Join-Path $ReleaseDir 'Glide.Setup.exe'
$built = Join-Path $ReleaseDir 'Glide Setup.exe'
$portable = Join-Path $ReleaseDir "Glide-$Version-Portable.zip"
$shaFile = Join-Path $ReleaseDir 'Glide.Setup.sha256'
if (-not (Test-Path $setup) -and (Test-Path $built)) { Run "copy 'Glide Setup.exe' -> Glide.Setup.exe" { Copy-Item $built $setup } }
$installer = if (Test-Path $setup) { $setup } elseif (Test-Path $built) { $built } else { $null }
if (-not $installer) { Fail "no installer in $ReleaseDir (build it with build.cmd first)" }
if (-not (Test-Path $portable)) { Fail "missing $portable" }
$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "   installer SHA256 $hash"
Run 'write Glide.Setup.sha256' { Set-Content -Path $shaFile -Value "$hash  Glide.Setup.exe" -Encoding ascii -NoNewline }
$notesText = [IO.File]::ReadAllText($notes, [Text.Encoding]::UTF8)
if ($notesText -match 'SHA256_PLACEHOLDER') {
    Run 'replace SHA256_PLACEHOLDER in the release notes' { [IO.File]::WriteAllText($notes, $notesText.Replace('SHA256_PLACEHOLDER', $hash), (New-Object Text.UTF8Encoding $false)) }
} elseif ($notesText -notmatch [regex]::Escape($hash)) {
    $message = 'the release notes carry a SHA256 that is not the installer''s (rebuilt since the notes were written?)'
    if ($live) { Fail $message } else { Write-Host "   WARNING: $message" -ForegroundColor Yellow }
}

# 3. Privacy grep -----------------------------------------------------------------------------------------------------
Step 'Privacy check (personal names and paths)'
$userName = [Environment]::UserName
$patterns = @("[A-Za-z]:\\+Users\\+$([regex]::Escape($userName))\b", "/Users/$([regex]::Escape($userName))\b", "\b$([regex]::Escape($userName))\b") + $PrivacyPattern
function Test-Privacy([string]$tree) {
    $files = git -C $tree ls-files --cached --others --exclude-standard | Where-Object { $_ -notmatch '\.(png|jpe?g|gif|webp|ico|exe|dll|zip|bin|avif|heic|jxl|psd|tif+)$' }
    $hits = foreach ($file in $files) {
        $path = Join-Path $tree $file
        if (Test-Path $path -PathType Leaf) { Select-String -Path $path -Pattern $patterns -CaseSensitive:$false -ErrorAction SilentlyContinue }
    }
    if ($hits) {
        $hits | Select-Object -First 30 | ForEach-Object { Write-Host ("   {0}:{1}: {2}" -f $_.Path.Substring($tree.TrimEnd('\').Length + 1), $_.LineNumber, $_.Line.Trim()) }
        Fail "$(@($hits).Count) personal-name/path hit(s) in $tree; fix or redact them before publishing"
    }
    Write-Host "   clean ($(@($files).Count) files checked in $tree)"
}
Test-Privacy $root
# 4. Mirror into the clean clone --------------------------------------------------------------------------------------
Step "Mirror into $Clone"
if (-not (Test-Path (Join-Path $Clone '.git'))) { Fail "$Clone is not a git clone (git clone https://github.com/$Repo.git $Clone)" }
$excludeDirs = @('.git', 'Archive', '.artifacts', 'artifacts', 'dist', 'dist-installer', 'dist-portable', '.vs', '.idea', '.vscode',
    'bin', 'obj', 'build', 'TestResults', 'coverage', 'diagnostics-out', 'AvaloniaUI', '__pycache__')
$excludeFiles = @('build-output.txt', 'installer-output.txt', '*.log', '*.zip', 'Glide-*.exe', 'Glide Setup.exe', 'Glide Setup.sha256',
    '*.user', '*.pdb', 'codecs.index.json')
[string[]]$listOnly = if ($live) { @() } else { @('/L') }
robocopy $root $Clone /MIR @listOnly /XD @excludeDirs /XF @excludeFiles /NFL /NDL /NP /NJH | Out-Host
if ($LASTEXITCODE -ge 8) { Fail "robocopy failed ($LASTEXITCODE)" }
$global:LASTEXITCODE = 0
if ($live) {
    $status = git -C $Clone status --short
    $status | Select-Object -First 60 | Out-Host
    if ($status | Select-String 'Archive/|\.zip|\.exe|dist|artifacts') { Fail 'build output or archives would be committed' }
    Test-Privacy $Clone   # what robocopy actually copied (it also copies git-ignored local files)
}

# 5. Commit, tag, push, release ---------------------------------------------------------------------------------------
Step 'Commit, tag, push and create the release'
$existing = git -C $Clone tag -l $tag
if ($existing) { Fail "$tag already exists in the clone" }
Run "git commit 'Glide $Version'" { git -C $Clone add -A; git -C $Clone commit -m "Glide $Version" -m "See CHANGELOG.md and docs/releases/release-notes-$Version.md." }
Run "git tag $tag" { git -C $Clone tag -a $tag -m "Glide $Version" }
Run 'git push origin main' { git -C $Clone push origin main }
Run "git push origin $tag" { git -C $Clone push origin $tag }
[string[]]$draftArg = if ($Draft) { @('--draft') } else { @('--latest') }
Run "gh release create $tag ($Title)" {
    gh release create $tag --repo $Repo --title $Title --notes-file $notes @draftArg $setup $portable $shaFile
}

# 6. Verify -----------------------------------------------------------------------------------------------------------
Step 'Verify the published assets'
if ($live) {
    $check = Join-Path ([IO.Path]::GetTempPath()) "glide-$Version-verify"
    New-Item -ItemType Directory -Force $check | Out-Null
    gh release download $tag --repo $Repo --dir $check --clobber
    $downloaded = (Get-FileHash (Join-Path $check 'Glide.Setup.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($downloaded -ne $hash) { Fail "downloaded installer hash $downloaded differs from $hash" }
    Write-Host "   published installer matches $hash" -ForegroundColor Green
} else { Write-Host '   would download the assets and compare the installer hash' -ForegroundColor Yellow }

Write-Host "Done - $mode" -ForegroundColor Green
