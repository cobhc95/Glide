param([string]$Output = "Glide-Zero-Context-Handover.zip")
$ErrorActionPreference = 'Stop'
# This script lives in tools\; the project root is its parent folder.
$project = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$handoff = Join-Path $project 'docs\history\GLIDE_MANIFESTO_AND_HANDOFF.md'
if (-not (Test-Path $handoff)) { throw "Missing Glide Image Viewer\docs\history\GLIDE_MANIFESTO_AND_HANDOFF.md" }

$temp = Join-Path $env:TEMP ("GlideSource_" + [guid]::NewGuid().ToString('N'))
$stagedProject = Join-Path $temp 'Glide Image Viewer'
New-Item -ItemType Directory -Path $temp | Out-Null
Copy-Item $project $stagedProject -Recurse

# Generated field-audit/diagnostic evidence is deliberately excluded from source handoff ZIPs.
# Quantitative baselines and research conclusions belong in the single authoritative handoff;
# the next Windows audit must generate fresh evidence rather than inherit stale PASS/FAIL artefacts.
$auditEvidence = Join-Path $stagedProject 'audit_evidence'
if (Test-Path $auditEvidence) { Remove-Item $auditEvidence -Recurse -Force }

$codecDir = Join-Path $stagedProject 'codecs'
$indexTool = Join-Path $stagedProject 'tools\generate-codec-index.ps1'
if ((Test-Path $codecDir) -and (Test-Path $indexTool)) {
    & $indexTool -CodecDirectory $codecDir
}

# Superseded history lives under Archive/ and stays out of handover ZIPs. docs\ is kept (it carries the
# handoff document and release notes) but its screenshots\ gallery is dropped.
Get-ChildItem $stagedProject -Directory -Recurse | Where-Object { $_.Name -in @('bin','obj','build','dist','artifacts','.artifacts','dist-installer','.git','screenshots','diagnostic_corpus','Archive') } | Remove-Item -Recurse -Force
# Local-only working notes never ship: feedback-round queues (docs\QUEUE-*.md) and design mockups (docs\mockups\).
$stagedDocs = Join-Path $stagedProject 'docs'
if (Test-Path (Join-Path $stagedDocs 'mockups')) { Remove-Item (Join-Path $stagedDocs 'mockups') -Recurse -Force }
Get-ChildItem $stagedDocs -File -Filter 'QUEUE-*.md' -ErrorAction SilentlyContinue | Remove-Item -Force

Get-ChildItem $stagedProject -File -Recurse | Where-Object {
    $_.Name -match '\.(bak|tmp|orig|prehelddrag|log|pdb|exe|dll|ilk|exp|lib|obj|pch|idb)$' -or $_.Name -like '*.zip' -or $_.Name -like 'build-output.txt' -or $_.Name -like 'installer-output.txt'
} | Remove-Item -Force

# Permanent transfer contract: exactly one handoff document (docs\history\GLIDE_MANIFESTO_AND_HANDOFF.md), inside Glide Image Viewer.
$handoffs = @(Get-ChildItem $stagedProject -File -Recurse | Where-Object {
    ($_.Name -match '(?i)handoff|hand-over|handover' -and $_.Extension -in @('.md','.txt')) -or $_.Name -eq 'GLIDE_LEGACY_COMPLETE_ZERO_CONTEXT_HANDOFF.txt'
})
if ($handoffs.Count -ne 1 -or $handoffs[0].Name -ne 'GLIDE_MANIFESTO_AND_HANDOFF.md') {
    $found = ($handoffs | ForEach-Object { $_.FullName }) -join "`n"
    throw "Packaging contract requires exactly one handoff file named GLIDE_MANIFESTO_AND_HANDOFF.md inside Glide Image Viewer. Found:`n$found"
}

$destinationProject = Join-Path $project $Output

if (Test-Path $destinationProject) { Remove-Item $destinationProject -Force }
Compress-Archive -Path $stagedProject -DestinationPath $destinationProject -CompressionLevel Optimal

Remove-Item $temp -Recurse -Force
Write-Host "Created $destinationProject"
Write-Host "ZIP contract: output remains inside Glide Image Viewer; one comprehensive handoff inside it."
