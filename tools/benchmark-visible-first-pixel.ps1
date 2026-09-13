param(
    [Parameter(Mandatory=$true)][string]$GlideManagedExe,
    [Parameter(Mandatory=$true)][string]$GlideFastLaunchExe,
    [Parameter(Mandatory=$true)][string]$IrfanExe,
    [ValidateRange(30,10000)][int]$Runs = 30,
    [int]$TimeoutMs = 10000,
    [int]$PollMs = 8,
    [string]$OutputCsv = "artifacts\visible-first-pixel.csv",
    [ValidateSet('fresh','stable','saved')][string]$Profile = 'fresh',
    [string]$ProfileTemplateDirectory = '',
    [string]$CaseManifest = 'tools\benchmark_cases.csv',
    [ValidateSet('warm','cold')][string]$StorageState = 'warm',
    [string]$ColdStoragePreparationCommand = '',
    [string]$ComparatorSettings = '',
    [string]$ComparatorPlugins = 'none declared',
    [string]$DisplayConditions = '',
    [int]$ForceFailureAtAttempt = 0,
    [switch]$IncludeAssociationRoute
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$GlideManagedExe=(Resolve-Path $GlideManagedExe).Path; $GlideFastLaunchExe=(Resolve-Path $GlideFastLaunchExe).Path; $IrfanExe=(Resolve-Path $IrfanExe).Path
if(-not [IO.Path]::IsPathRooted($OutputCsv)){$OutputCsv=Join-Path $root $OutputCsv}
if(-not [IO.Path]::IsPathRooted($CaseManifest)){$CaseManifest=Join-Path $root $CaseManifest}
$CaseManifest=(Resolve-Path $CaseManifest).Path
$artifactDir=Split-Path -Parent $OutputCsv; New-Item -ItemType Directory -Force $artifactDir|Out-Null
$settingsDir=Join-Path $artifactDir "settings-$Profile"; New-Item -ItemType Directory -Force $settingsDir|Out-Null
$syntheticTarget=Join-Path $artifactDir 'observer-selftest-target.png'
$profileSnapshotDir=Join-Path $artifactDir 'profile-input-snapshot'
if(Test-Path $profileSnapshotDir){Remove-Item $profileSnapshotDir -Recurse -Force}
New-Item -ItemType Directory -Force $profileSnapshotDir|Out-Null

if($StorageState -eq 'cold' -and [string]::IsNullOrWhiteSpace($ColdStoragePreparationCommand)){
  throw 'Cold storage certification requires -ColdStoragePreparationCommand. The harness will not label ordinary process-cold runs as storage-cold.'
}
if([string]::IsNullOrWhiteSpace($ComparatorSettings)){throw 'Record IrfanView comparator settings with -ComparatorSettings before benchmarking.'}
if([string]::IsNullOrWhiteSpace($DisplayConditions)){throw 'Record monitor/DPI/display conditions with -DisplayConditions before benchmarking.'}

Add-Type -AssemblyName System.Drawing
Add-Type -Path (Join-Path $root 'tools\VisibleFirstPixelObserver.cs') -ReferencedAssemblies @('System.Drawing','System.Core')

function Resolve-RootPath([string]$value){ if([IO.Path]::IsPathRooted($value)){return (Resolve-Path $value).Path}; return (Resolve-Path (Join-Path $root $value)).Path }
$cases=@(Import-Csv $CaseManifest | ForEach-Object {
  [pscustomobject]@{Name=$_.name;Category=$_.category;Target=(Resolve-RootPath $_.target);Reference=(Resolve-RootPath $_.reference)}
})
if($cases.Count -eq 0){throw 'Benchmark case manifest is empty.'}
$requiredCategories=@('baseline_jpeg','progressive_jpeg','large_landscape','large_portrait','orientation','transparent_png','tiff','specialist')
foreach($category in $requiredCategories){if(-not ($cases.Category -contains $category)){throw "Benchmark case manifest is missing required category: $category"}}

# Synthetic fingerprint self-test only. It is deliberately not part of performance results.
if(-not(Test-Path $syntheticTarget)){
  $bmp=New-Object Drawing.Bitmap -ArgumentList 2400,1600; $g=[Drawing.Graphics]::FromImage($bmp)
  $g.Clear([Drawing.Color]::Black); $g.FillRectangle([Drawing.Brushes]::Magenta,0,0,1200,800); $g.FillRectangle([Drawing.Brushes]::Lime,1200,0,1200,800)
  $g.FillRectangle([Drawing.Brushes]::Cyan,0,800,1200,800); $g.FillRectangle([Drawing.Brushes]::Yellow,1200,800,1200,800)
  $g.FillRectangle([Drawing.Brushes]::White,240,160,150,110); $g.Dispose(); $bmp.Save($syntheticTarget,[Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
$calibration=New-Object Drawing.Bitmap -ArgumentList $syntheticTarget
if(-not [GlideVisibleFirstPixelObserver]::MatchesTarget($calibration)){$calibration.Dispose();throw 'Observer synthetic self-test rejected correct target.'}
$rotated=New-Object Drawing.Bitmap -ArgumentList $calibration; $rotated.RotateFlip([Drawing.RotateFlipType]::Rotate90FlipNone)
if([GlideVisibleFirstPixelObserver]::MatchesTarget($rotated)){$rotated.Dispose();$calibration.Dispose();throw 'Observer synthetic self-test accepted rotated target.'}
$rotated.Dispose();$calibration.Dispose()

# Pre-hash every executable/target/reference before measured blocks. This validates identity but does
# not constitute a warm-storage claim: cold mode executes its supplied cache-flush command after it.
$exeHashes=@{}; foreach($e in @($GlideManagedExe,$GlideFastLaunchExe,$IrfanExe)){$exeHashes[$e]=(Get-FileHash $e -Algorithm SHA256).Hash}
$caseHashes=@{}; $previousReference=$null; $previousReferenceHash=$null
foreach($case in $cases){
  $caseHashes[$case.Name]=[pscustomobject]@{Target=(Get-FileHash $case.Target -Algorithm SHA256).Hash;Reference=(Get-FileHash $case.Reference -Algorithm SHA256).Hash}
  [GlideVisibleFirstPixelObserver]::ConfigureReference($case.Reference)
  $referenceBitmap=New-Object Drawing.Bitmap -ArgumentList $case.Reference
  try{
    if(-not [GlideVisibleFirstPixelObserver]::MatchesConfiguredReference($referenceBitmap)){throw "Image-specific observer rejected exact reference for $($case.Name)."}
    $wrongOrientation=New-Object Drawing.Bitmap -ArgumentList $referenceBitmap; $wrongOrientation.RotateFlip([Drawing.RotateFlipType]::Rotate90FlipNone)
    try{if([GlideVisibleFirstPixelObserver]::MatchesConfiguredReference($wrongOrientation)){throw "Image-specific observer accepted rotated reference for $($case.Name)."}}finally{$wrongOrientation.Dispose()}
    $wrongColour=New-Object Drawing.Bitmap -ArgumentList ([Math]::Max(32,$referenceBitmap.Width)),([Math]::Max(32,$referenceBitmap.Height));$wg=[Drawing.Graphics]::FromImage($wrongColour);$wg.Clear([Drawing.Color]::Fuchsia);$wg.Dispose()
    try{if([GlideVisibleFirstPixelObserver]::MatchesConfiguredReference($wrongColour)){throw "Image-specific observer accepted unrelated colour field for $($case.Name)."}}finally{$wrongColour.Dispose()}
    if($previousReference -and $previousReferenceHash -ne $caseHashes[$case.Name].Reference){
      $stale=New-Object Drawing.Bitmap -ArgumentList $previousReference
      try{if([GlideVisibleFirstPixelObserver]::MatchesConfiguredReference($stale)){throw "Image-specific observer accepted stale prior target for $($case.Name)."}}finally{$stale.Dispose()}
    }
  }finally{$referenceBitmap.Dispose()}
  $previousReference=$case.Reference;$previousReferenceHash=$caseHashes[$case.Name].Reference
}
[GlideVisibleFirstPixelObserver]::ClearReference()

function Get-PeBitness([string]$path){
  try{$fs=[IO.File]::OpenRead($path);$br=New-Object IO.BinaryReader($fs);try{$fs.Position=0x3c;$pe=$br.ReadInt32();$fs.Position=$pe+4;$machine=$br.ReadUInt16();switch($machine){0x8664{'x64'}0x14c{'x86'}0xAA64{'arm64'}default{"machine-0x{0:X4}" -f $machine}}}finally{$br.Dispose();$fs.Dispose()}}catch{'unknown'}
}
$comparatorVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($IrfanExe).FileVersion
$comparatorBitness=Get-PeBitness $IrfanExe
$previousSettings=[Environment]::GetEnvironmentVariable('GLIDE_SETTINGS_DIRECTORY','Process')
$previousBenchmarkDisableReuse=[Environment]::GetEnvironmentVariable('GLIDE_BENCHMARK_DISABLE_REUSE','Process')
$profileTemplateHash=''
if($Profile -ne 'fresh'){
  if([string]::IsNullOrWhiteSpace($ProfileTemplateDirectory) -or -not(Test-Path $ProfileTemplateDirectory -PathType Container)){throw "$Profile profile requires -ProfileTemplateDirectory."}
  $ProfileTemplateDirectory=(Resolve-Path $ProfileTemplateDirectory).Path
  $profileTemplateHash=(@(Get-ChildItem $ProfileTemplateDirectory -File -Recurse|Sort-Object FullName|ForEach-Object{"{0}|{1}" -f $_.FullName.Substring($ProfileTemplateDirectory.Length),(Get-FileHash $_.FullName -Algorithm SHA256).Hash}) -join "`n")
  $sha=[Security.Cryptography.SHA256]::Create();try{$profileTemplateHash=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($profileTemplateHash))).Replace('-','')}finally{$sha.Dispose()}
  Copy-Item (Join-Path $ProfileTemplateDirectory '*') $profileSnapshotDir -Recurse -Force
} else {
  Set-Content (Join-Path $profileSnapshotDir 'FRESH_PROFILE.txt') 'Fresh profile: settings directory is empty before each Glide route.'
}
function Prepare-GlideProfile{
  if(Test-Path $settingsDir){Remove-Item $settingsDir -Recurse -Force}
  New-Item -ItemType Directory -Force $settingsDir|Out-Null
  if($Profile -ne 'fresh'){Copy-Item (Join-Path $ProfileTemplateDirectory '*') $settingsDir -Recurse -Force}
}
function Prepare-Storage([string]$target){
  if($StorageState -eq 'warm'){
    $stream=[IO.File]::Open($target,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    try{$buffer=New-Object byte[] 1048576;while($stream.Read($buffer,0,$buffer.Length)-gt 0){}}finally{$stream.Dispose()}
  } else {
    # Caller supplies an auditable platform-specific cache-flush command (RAMMap, test VM reset, etc.).
    # It runs before the external Stopwatch starts and its exact text is recorded in the manifest.
    Invoke-Expression $ColdStoragePreparationCommand | Out-Null
  }
}
function Quote([string]$v){if($v -notmatch '[\s"]'){return $v};return '"'+($v -replace '(\\*)"','$1$1\"' -replace '(\\+)$','$1$1')+'"'}
function Get-Descendants([int]$rootPid){
  $seen=New-Object 'System.Collections.Generic.HashSet[int]';[void]$seen.Add($rootPid)
  try{$all=@(Get-CimInstance Win32_Process -ErrorAction Stop|Select-Object ProcessId,ParentProcessId)}catch{return @($rootPid)}
  $changed=$true;while($changed){$changed=$false;foreach($p in $all){if($seen.Contains([int]$p.ParentProcessId)-and $seen.Add([int]$p.ProcessId)){$changed=$true}}};return @($seen)
}
function Register-Owned([hashtable]$owned,[int]$pid){
  if($owned.ContainsKey($pid)){return}
  try{$proc=Get-Process -Id $pid -ErrorAction Stop;$owned[$pid]=$proc.StartTime.ToUniversalTime().Ticks}catch{}
}
function Owned-Pids([hashtable]$owned){return @($owned.Keys|ForEach-Object{[int]$_})}
function Stop-Owned([hashtable]$owned){
  $failures=@()
  foreach($pid in @($owned.Keys|ForEach-Object{[int]$_}|Sort-Object -Descending)){
    try{
      $proc=Get-Process -Id $pid -ErrorAction SilentlyContinue
      if($null -eq $proc){continue}
      if($proc.StartTime.ToUniversalTime().Ticks -ne [long]$owned[$pid]){$failures+=('pid '+$pid+': creation identity changed; not terminated');continue}
      $proc.Kill()
    }catch {$failures+=('pid '+$pid+': '+$_.Exception.Message)}
  }
  return ($failures -join '; ')
}
function Write-Row($row){$exists=Test-Path $OutputCsv;$row|Export-Csv -Path $OutputCsv -NoTypeInformation -Append:$exists}
${script:attemptOrdinal}=0
function Measure-Route($case,$route,[int]$run,[int]$position,[string]$block){
  $hash=$caseHashes[$case.Name]
  $row=[ordered]@{utc=[DateTime]::UtcNow.ToString('O');profile=$Profile;storage_state=$StorageState;case=$case.Name;category=$case.Category;block=$block;position=$position;run=$run;route=$route.Name;attempted=$true;detected=$false;first_correct_ms=$null;stable_managed_ms=$null;clock_frequency=[Diagnostics.Stopwatch]::Frequency;launch_start_tick=$null;first_correct_tick=$null;stable_managed_tick=$null;post_first_miss_polls=0;first_pid=$null;first_hwnd=$null;first_client_width=$null;first_client_height=$null;observer_capture_ms=$null;observer_poll_ms=$PollMs;failure='';cleanup_failure='';root_pid=$null;root_process_start_utc_ticks=$null;target_sha256=$hash.Target;reference_sha256=$hash.Reference;exe_sha256=$exeHashes[$route.Exe]}
  $owned=@{};$p=$null
  try{
    $script:attemptOrdinal++
    if($route.Name -like 'Glide.*'){Prepare-GlideProfile}
    Prepare-Storage $case.Target
    [GlideVisibleFirstPixelObserver]::ConfigureReference($case.Reference)
    if($ForceFailureAtAttempt -gt 0 -and $script:attemptOrdinal -eq $ForceFailureAtAttempt){throw 'forced_mid_run_failure_for_raw_persistence_calibration'}
    $args=Quote $case.Target;$startTicks=[Diagnostics.Stopwatch]::GetTimestamp();$row.launch_start_tick=$startTicks;$p=Start-Process -FilePath $route.Exe -ArgumentList $args -PassThru
    $row.root_pid=$p.Id;$row.root_process_start_utc_ticks=$p.StartTime.ToUniversalTime().Ticks;Register-Owned $owned $p.Id;$deadline=[DateTime]::UtcNow.AddMilliseconds($TimeoutMs);$first=$null;$stable=$null
    while([DateTime]::UtcNow -lt $deadline){
      foreach($id in @(Get-Descendants $p.Id)){Register-Owned $owned ([int]$id)}
      $obs=[GlideVisibleFirstPixelObserver]::Observe((Owned-Pids $owned));$row.observer_capture_ms=[math]::Round([double]$obs.CaptureMilliseconds,3)
      $elapsed=([Diagnostics.Stopwatch]::GetTimestamp()-$startTicks)*1000.0/[Diagnostics.Stopwatch]::Frequency
      if($obs.Detected -and -not $first){$first=$elapsed;$row.detected=$true;$row.first_correct_ms=[math]::Round($elapsed,3);$row.first_correct_tick=[Diagnostics.Stopwatch]::GetTimestamp();$row.first_pid=$obs.ProcessId;$row.first_hwnd=$obs.WindowHandle;$row.first_client_width=$obs.ClientWidth;$row.first_client_height=$obs.ClientHeight}
      elseif($first -and -not $obs.Detected){$row.post_first_miss_polls++}
      if($obs.Detected){
        $isManaged=($route.Name -eq 'Glide.ManagedDirect') -or ($route.Name -eq 'IrfanView.Direct') -or ($route.Name -eq 'Glide.FastLaunch' -and $obs.ProcessId -ne $p.Id)
        if($isManaged){$stable=$elapsed;$row.stable_managed_ms=[math]::Round($elapsed,3);$row.stable_managed_tick=[Diagnostics.Stopwatch]::GetTimestamp();break}
      }
      Start-Sleep -Milliseconds $PollMs
    }
    if(-not $first){$row.failure='timeout_no_correct_image'}elseif($route.Name -eq 'Glide.FastLaunch' -and -not $stable){$row.failure='correct_preview_but_no_managed_takeover'}
  }catch{$row.failure=$_.Exception.GetType().Name+': '+$_.Exception.Message}
  finally{$cleanup=Stop-Owned $owned;if($cleanup){$row.cleanup_failure=$cleanup};[GlideVisibleFirstPixelObserver]::ClearReference();Write-Row([pscustomobject]$row)}
}

if(Test-Path $OutputCsv){Remove-Item $OutputCsv -Force}
[Environment]::SetEnvironmentVariable('GLIDE_SETTINGS_DIRECTORY',$settingsDir,'Process')
[Environment]::SetEnvironmentVariable('GLIDE_BENCHMARK_DISABLE_REUSE','1','Process')
try{
  $routes=@(@{Name='Glide.FastLaunch';Exe=$GlideFastLaunchExe},@{Name='Glide.ManagedDirect';Exe=$GlideManagedExe},@{Name='IrfanView.Direct';Exe=$IrfanExe})
  $perms=@(@(0,1,2),@(1,2,0),@(2,0,1),@(0,2,1),@(2,1,0),@(1,0,2))
  foreach($case in $cases){
    for($i=1;$i -le $Runs;$i++){$perm=$perms[($i-1)%$perms.Count];$block="$($case.Name)-P$((($i-1)%$perms.Count)+1)";for($pos=0;$pos -lt 3;$pos++){Measure-Route $case $routes[$perm[$pos]] $i ($pos+1) $block}}
  }
  if($IncludeAssociationRoute){Write-Warning 'Shell association/Open With is explicitly UNSUPPORTED by this PID-owned harness because ShellExecute may return a shell PID rather than the viewer. Keep it as a separate integration category.'}
}finally{[GlideVisibleFirstPixelObserver]::ClearReference();[Environment]::SetEnvironmentVariable('GLIDE_SETTINGS_DIRECTORY',$previousSettings,'Process');[Environment]::SetEnvironmentVariable('GLIDE_BENCHMARK_DISABLE_REUSE',$previousBenchmarkDisableReuse,'Process')}
$rows=Import-Csv $OutputCsv
$summary=$rows|Group-Object case,route|ForEach-Object{$ok=@($_.Group|Where-Object detected -eq 'True'|ForEach-Object{[double]$_.first_correct_ms}|Sort-Object);$stable=@($_.Group|Where-Object{$_.stable_managed_ms -ne ''}|ForEach-Object{[double]$_.stable_managed_ms}|Sort-Object);[pscustomobject]@{case=$_.Group[0].case;category=$_.Group[0].category;route=$_.Group[0].route;attempts=$_.Count;successes=$ok.Count;failures=$_.Count-$ok.Count;p50_ms=if($ok.Count){$ok[[math]::Floor(($ok.Count-1)*.5)]}else{$null};p95_ms=if($ok.Count){$ok[[math]::Floor(($ok.Count-1)*.95)]}else{$null};stable_p50_ms=if($stable.Count){$stable[[math]::Floor(($stable.Count-1)*.5)]}else{$null};stable_p95_ms=if($stable.Count){$stable[[math]::Floor(($stable.Count-1)*.95)]}else{$null}}}
$summary|Export-Csv -NoTypeInformation (Join-Path $artifactDir 'visible-first-pixel-summary.csv');$summary|Format-Table -AutoSize
[ordered]@{created_utc=[DateTime]::UtcNow.ToString('O');profile=$Profile;runs_per_route_per_case=$Runs;storage_state=$StorageState;cold_storage_preparation=if($StorageState -eq 'cold'){$ColdStoragePreparationCommand}else{'sequential full-file read before timed launch'};poll_ms=$PollMs;timeout_ms=$TimeoutMs;case_manifest=$CaseManifest;case_manifest_sha256=(Get-FileHash $CaseManifest -Algorithm SHA256).Hash;cases=$cases;observer='PID/process-tree-owned top-level client ROI; per-image 9x7 spatial fingerprint; exact-reference self-test; synthetic rotated negative control';profile_template_sha256=$profileTemplateHash;profile_input_snapshot=$profileSnapshotDir;comparator=[ordered]@{path=$IrfanExe;version=$comparatorVersion;bitness=$comparatorBitness;settings=$ComparatorSettings;plugins=$ComparatorPlugins};display_conditions=$DisplayConditions;reuse_forced_off=$true;shell_association=if($IncludeAssociationRoute){'UNSUPPORTED by PID-owned harness; requires separate attributable integration runner'}else{'not requested'}}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $artifactDir 'visible-first-pixel-manifest.json')
