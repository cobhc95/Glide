param(
    [Parameter(Mandatory=$true)][string]$GlideManagedExe,
    [Parameter(Mandatory=$true)][string]$GlideFastLaunchExe,
    [ValidateRange(30,10000)][int]$Runs = 30,
    [int]$TimeoutMs = 10000,
    [int]$OwnerReadyTimeoutMs = 10000,
    [int]$PollMs = 8,
    [string]$OutputCsv = 'artifacts\existing-instance.csv',
    [string]$ProfileTemplateDirectory = '',
    [string]$CaseManifest = 'tools\benchmark_cases.csv',
    [string]$DisplayConditions = ''
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$GlideManagedExe=(Resolve-Path $GlideManagedExe).Path
$GlideFastLaunchExe=(Resolve-Path $GlideFastLaunchExe).Path
if(-not [IO.Path]::IsPathRooted($OutputCsv)){$OutputCsv=Join-Path $root $OutputCsv}
if(-not [IO.Path]::IsPathRooted($CaseManifest)){$CaseManifest=Join-Path $root $CaseManifest}
$CaseManifest=(Resolve-Path $CaseManifest).Path
if([string]::IsNullOrWhiteSpace($DisplayConditions)){throw 'Record monitor/DPI/display conditions with -DisplayConditions.'}
$artifactDir=Split-Path -Parent $OutputCsv;New-Item -ItemType Directory -Force $artifactDir|Out-Null
$settingsDir=Join-Path $artifactDir 'settings-existing-instance'
if(-not [string]::IsNullOrWhiteSpace($ProfileTemplateDirectory)){$ProfileTemplateDirectory=(Resolve-Path $ProfileTemplateDirectory).Path}

Add-Type -AssemblyName System.Drawing
Add-Type -Path (Join-Path $root 'tools\VisibleFirstPixelObserver.cs') -ReferencedAssemblies @('System.Drawing','System.Core')
function Resolve-RootPath([string]$value){if([IO.Path]::IsPathRooted($value)){return (Resolve-Path $value).Path};return (Resolve-Path (Join-Path $root $value)).Path}
$cases=@(Import-Csv $CaseManifest|ForEach-Object{[pscustomobject]@{Name=$_.name;Category=$_.category;Target=(Resolve-RootPath $_.target);Reference=(Resolve-RootPath $_.reference)}})
if($cases.Count -eq 0){throw 'Benchmark case manifest is empty.'}
$caseHashes=@{};foreach($case in $cases){$caseHashes[$case.Name]=[pscustomobject]@{Target=(Get-FileHash $case.Target -Algorithm SHA256).Hash;Reference=(Get-FileHash $case.Reference -Algorithm SHA256).Hash}}
$exeHashes=@{};foreach($e in @($GlideManagedExe,$GlideFastLaunchExe)){$exeHashes[$e]=(Get-FileHash $e -Algorithm SHA256).Hash}

function Quote([string]$v){if($v -notmatch '[\s"]'){return $v};return '"'+($v -replace '(\\*)"','$1$1\"' -replace '(\\+)$','$1$1')+'"'}
function Test-PresenceEvent{
  try{$evt=[Threading.EventWaitHandle]::OpenExisting('Local\Glide3.ProcessPresence.v3');$evt.Dispose();return $true}catch{return $false}
}
function Wait-OwnerReady([Diagnostics.Process]$owner){
  $deadline=[DateTime]::UtcNow.AddMilliseconds($OwnerReadyTimeoutMs)
  while([DateTime]::UtcNow -lt $deadline){if($owner.HasExited){throw 'Owner exited before publishing IPC presence.'};if(Test-PresenceEvent){return};Start-Sleep -Milliseconds 20}
  throw 'Timed out waiting for owner IPC presence event.'
}
function Get-Descendants([int]$rootPid){
  $seen=New-Object 'System.Collections.Generic.HashSet[int]';[void]$seen.Add($rootPid)
  try{$all=@(Get-CimInstance Win32_Process -ErrorAction Stop|Select-Object ProcessId,ParentProcessId)}catch{return @($rootPid)}
  $changed=$true;while($changed){$changed=$false;foreach($p in $all){if($seen.Contains([int]$p.ParentProcessId)-and $seen.Add([int]$p.ProcessId)){$changed=$true}}};return @($seen)
}
function Register-Owned([hashtable]$owned,[int]$pid){if($owned.ContainsKey($pid)){return};try{$proc=Get-Process -Id $pid -ErrorAction Stop;$owned[$pid]=$proc.StartTime.ToUniversalTime().Ticks}catch{}}
function Owned-Pids([hashtable]$owned){return @($owned.Keys|ForEach-Object{[int]$_})}
function Stop-Owned([hashtable]$owned){$failures=@();foreach($pid in @($owned.Keys|ForEach-Object{[int]$_}|Sort-Object -Descending)){try{$proc=Get-Process -Id $pid -ErrorAction SilentlyContinue;if($null -eq $proc){continue};if($proc.StartTime.ToUniversalTime().Ticks -ne [long]$owned[$pid]){$failures+=('pid '+$pid+': creation identity changed; not terminated');continue};$proc.Kill()}catch{$failures+=('pid '+$pid+': '+$_.Exception.Message)}};return ($failures -join '; ')}
function Prepare-Profile{
  if(Test-Path $settingsDir){Remove-Item $settingsDir -Recurse -Force};New-Item -ItemType Directory -Force $settingsDir|Out-Null
  if(-not [string]::IsNullOrWhiteSpace($ProfileTemplateDirectory)){Copy-Item (Join-Path $ProfileTemplateDirectory '*') $settingsDir -Recurse -Force}
}
function Assert-ReuseEnabled{
  $settingsFile=Join-Path $settingsDir 'glide.settings.json'
  if(-not(Test-Path $settingsFile)){return}
  try{$json=Get-Content $settingsFile -Raw|ConvertFrom-Json}catch{throw 'Existing-instance profile contains unreadable glide.settings.json.'}
  if($null -ne $json.ReuseSingleInstance -and -not [bool]$json.ReuseSingleInstance){throw 'Existing-instance benchmark profile has ReuseSingleInstance=false.'}
}
function Write-Row($row){$exists=Test-Path $OutputCsv;$row|Export-Csv -Path $OutputCsv -NoTypeInformation -Append:$exists}

# This runner owns the singleton protocol for every trial. Refuse to disturb an unrelated live owner.
if(Test-PresenceEvent){throw 'A Glide singleton owner already exists. Close it manually; this harness will never terminate or reuse an unrelated owner.'}
if(Test-Path $OutputCsv){Remove-Item $OutputCsv -Force}
$previousSettings=[Environment]::GetEnvironmentVariable('GLIDE_SETTINGS_DIRECTORY','Process')
$previousDisableReuse=[Environment]::GetEnvironmentVariable('GLIDE_BENCHMARK_DISABLE_REUSE','Process')
[Environment]::SetEnvironmentVariable('GLIDE_SETTINGS_DIRECTORY',$settingsDir,'Process')
[Environment]::SetEnvironmentVariable('GLIDE_BENCHMARK_DISABLE_REUSE',$null,'Process')
try{
  $routes=@(@{Name='Glide.ExistingOwner.ManagedCLI';Exe=$GlideManagedExe},@{Name='Glide.ExistingOwner.FastLaunchCLI';Exe=$GlideFastLaunchExe})
  foreach($case in $cases){
    for($run=1;$run -le $Runs;$run++){
      $order=if(($run%2)-eq 1){@(0,1)}else{@(1,0)}
      for($position=0;$position -lt $order.Count;$position++){
        $routeIndex=$order[$position];$route=$routes[$routeIndex];$hash=$caseHashes[$case.Name]
        $row=[ordered]@{utc=[DateTime]::UtcNow.ToString('O');category='existing_instance';case=$case.Name;format_category=$case.Category;run=$run;position=($position+1);route=$route.Name;attempted=$true;detected=$false;first_correct_ms=$null;clock_frequency=[Diagnostics.Stopwatch]::Frequency;launch_start_tick=$null;first_correct_tick=$null;owner_pid=$null;owner_process_start_utc_ticks=$null;launcher_pid=$null;launcher_process_start_utc_ticks=$null;first_hwnd=$null;first_client_width=$null;first_client_height=$null;observer_capture_ms=$null;observer_poll_ms=$PollMs;failure='';cleanup_failure='';target_sha256=$hash.Target;reference_sha256=$hash.Reference;exe_sha256=$exeHashes[$route.Exe]}
        $owned=@{};$owner=$null;$launcher=$null
        try{
          if(Test-PresenceEvent){throw 'Unexpected pre-existing owner before trial.'}
          Prepare-Profile
          Assert-ReuseEnabled
          # A no-file managed process is intentionally outside the measured interval and becomes the owner.
          $owner=Start-Process -FilePath $GlideManagedExe -PassThru;Register-Owned $owned $owner.Id;$row.owner_pid=$owner.Id;$row.owner_process_start_utc_ticks=$owner.StartTime.ToUniversalTime().Ticks
          Wait-OwnerReady $owner
          [GlideVisibleFirstPixelObserver]::ConfigureReference($case.Reference)
          $start=[Diagnostics.Stopwatch]::GetTimestamp();$row.launch_start_tick=$start
          $launcher=Start-Process -FilePath $route.Exe -ArgumentList (Quote $case.Target) -PassThru;Register-Owned $owned $launcher.Id;$row.launcher_pid=$launcher.Id;$row.launcher_process_start_utc_ticks=$launcher.StartTime.ToUniversalTime().Ticks
          $deadline=[DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
          while([DateTime]::UtcNow -lt $deadline){
            foreach($id in @(Get-Descendants $owner.Id)){Register-Owned $owned ([int]$id)}
            if($launcher -and -not $launcher.HasExited){foreach($id in @(Get-Descendants $launcher.Id)){Register-Owned $owned ([int]$id)}}
            $obs=[GlideVisibleFirstPixelObserver]::Observe((Owned-Pids $owned));$row.observer_capture_ms=[math]::Round([double]$obs.CaptureMilliseconds,3)
            if($obs.Detected){$tick=[Diagnostics.Stopwatch]::GetTimestamp();$row.detected=$true;$row.first_correct_tick=$tick;$row.first_correct_ms=[math]::Round(($tick-$start)*1000.0/[Diagnostics.Stopwatch]::Frequency,3);$row.first_hwnd=$obs.WindowHandle;$row.first_client_width=$obs.ClientWidth;$row.first_client_height=$obs.ClientHeight;break}
            Start-Sleep -Milliseconds $PollMs
          }
          if(-not $row.detected){$row.failure='timeout_no_correct_image_on_owned_existing_instance'}
        }catch{$row.failure=$_.Exception.GetType().Name+': '+$_.Exception.Message}
        finally{
          [GlideVisibleFirstPixelObserver]::ClearReference()
          $cleanup=Stop-Owned $owned;if($cleanup){$row.cleanup_failure=$cleanup}
          # Do not start the next trial until our singleton owner has actually released the presence event.
          $releaseDeadline=[DateTime]::UtcNow.AddSeconds(3);while((Test-PresenceEvent)-and [DateTime]::UtcNow -lt $releaseDeadline){Start-Sleep -Milliseconds 20}
          if(Test-PresenceEvent){if($row.cleanup_failure){$row.cleanup_failure+='; '};$row.cleanup_failure+='owned singleton presence did not release within 3 s'}
          Write-Row ([pscustomobject]$row)
        }
      }
    }
  }
}finally{[GlideVisibleFirstPixelObserver]::ClearReference();[Environment]::SetEnvironmentVariable('GLIDE_SETTINGS_DIRECTORY',$previousSettings,'Process');[Environment]::SetEnvironmentVariable('GLIDE_BENCHMARK_DISABLE_REUSE',$previousDisableReuse,'Process')}
$rows=Import-Csv $OutputCsv
$summary=$rows|Group-Object case,route|ForEach-Object{$ok=@($_.Group|Where-Object detected -eq 'True'|ForEach-Object{[double]$_.first_correct_ms}|Sort-Object);[pscustomobject]@{case=$_.Group[0].case;route=$_.Group[0].route;attempts=$_.Count;successes=$ok.Count;failures=$_.Count-$ok.Count;p50_ms=if($ok.Count){$ok[[math]::Floor(($ok.Count-1)*.5)]}else{$null};p95_ms=if($ok.Count){$ok[[math]::Floor(($ok.Count-1)*.95)]}else{$null}}}
$summary|Export-Csv -NoTypeInformation (Join-Path $artifactDir 'existing-instance-summary.csv')
[ordered]@{created_utc=[DateTime]::UtcNow.ToString('O');category='existing_instance_only_not_cold';runs_per_route_per_case=$Runs;poll_ms=$PollMs;timeout_ms=$TimeoutMs;owner_ready_timeout_ms=$OwnerReadyTimeoutMs;case_manifest=$CaseManifest;case_manifest_sha256=(Get-FileHash $CaseManifest -Algorithm SHA256).Hash;profile_template=$ProfileTemplateDirectory;display_conditions=$DisplayConditions;routes=$routes;observer='PID-owned existing managed owner plus owned launch descendants; image-specific spatial fingerprint';warning='Do not combine these rows with process-cold or storage-cold results.'}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $artifactDir 'existing-instance-manifest.json')
$summary|Format-Table -AutoSize
