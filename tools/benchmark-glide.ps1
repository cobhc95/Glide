param(
 [Parameter(Mandatory=$true)][string]$Exe,[Parameter(Mandatory=$true)][string]$Image,
 [ValidateRange(30,10000)][int]$Iterations=30,[int]$TimeoutMs=15000,[string]$Output="artifacts\glide-trace-benchmark",[switch]$InjectPreFenceContamination
)
$ErrorActionPreference='Stop'; $Exe=(Resolve-Path $Exe).Path; $Image=(Resolve-Path $Image).Path
$project=Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path); $root=if([IO.Path]::IsPathRooted($Output)){[IO.Path]::GetFullPath($Output)}else{Join-Path $project $Output}; New-Item -ItemType Directory -Force $root|Out-Null
$settings=Join-Path $root 'isolated-settings'; New-Item -ItemType Directory -Force $settings|Out-Null; $csv=Join-Path $root 'results.csv'; if(Test-Path $csv){Remove-Item $csv -Force}
$prev=[Environment]::GetEnvironmentVariable('GLIDE_SETTINGS_DIRECTORY','Process'); $prevInject=[Environment]::GetEnvironmentVariable('GLIDE_BENCHMARK_INJECT_PREFENCE_EVENT','Process'); [Environment]::SetEnvironmentVariable('GLIDE_SETTINGS_DIRECTORY',$settings,'Process'); [Environment]::SetEnvironmentVariable('GLIDE_BENCHMARK_INJECT_PREFENCE_EVENT',$(if($InjectPreFenceContamination){'1'}else{$null}),'Process')
function Quote([string]$v){if($v -notmatch '[\s"]'){return $v};return '"'+$v.Replace('"','\"')+'"'}
function Append($r){$r|Export-Csv -NoTypeInformation -Path $csv -Append:(Test-Path $csv)}
try{
 for($i=1;$i -le $Iterations;$i++){
  $trace=Join-Path $root ("run-{0:D3}.tsv" -f $i); Remove-Item $trace -Force -ErrorAction SilentlyContinue
  $row=[ordered]@{Iteration=$i;Attempted=$true;ProcessWallMs=$null;CompositionRenderedMs=$null;BitmapAssignedMs=$null;DecodeReadyMs=$null;RequestId=$null;BackgroundContamination='';Failure=''}; $p=$null
  try{
   $args=@('--perf-trace',$trace,'--benchmark-exit-after-first-frame',$Image); $sw=[Diagnostics.Stopwatch]::StartNew(); $p=Start-Process $Exe -ArgumentList (($args|%{Quote $_}) -join ' ') -PassThru
   if(-not $p.WaitForExit($TimeoutMs)){throw "timeout_${TimeoutMs}ms"}; $sw.Stop(); $row.ProcessWallMs=[math]::Round($sw.Elapsed.TotalMilliseconds,3); if($p.ExitCode -ne 0){throw "exit_$($p.ExitCode)"}; if(-not(Test-Path $trace)){throw 'missing_trace'}
   $events=Import-Csv -Delimiter "`t" $trace; $assigned=$events|? event -eq 'bitmap_assigned'|select -First 1; $rendered=$events|? event -eq 'composition_batch_rendered'|select -First 1; $decode=$events|?{$_.event -in @('foreground_decode_ready','foreground_path_preview_ready')}|select -First 1
   if(-not $assigned){throw 'missing_bitmap_assigned'}; if(-not $rendered){throw 'missing_composition_batch_rendered'}
   $row.BitmapAssignedMs=[double]$assigned.elapsed_ms; $row.CompositionRenderedMs=[double]$rendered.elapsed_ms; $row.DecodeReadyMs=if($decode){[double]$decode.elapsed_ms}else{$null}; $row.RequestId=if([string]$rendered.detail -match 'request=([^;]+)'){$Matches[1]}else{$null}
   $critical=@('history_load_start','history_persist_start','profile_read_start','profile_write_start','folder_index_start','metadata_start','prefetch_start','refinement_start'); $bad=@($events|?{$critical -contains $_.event -and [double]$_.elapsed_ms -lt [double]$rendered.elapsed_ms}); if($bad.Count){$row.BackgroundContamination=($bad.event -join ';');throw "pre_fence_contamination:$($row.BackgroundContamination)"}
   if([string]::IsNullOrWhiteSpace([string]$row.RequestId)){throw 'render_event_missing_request_identity'}
  }catch{$row.Failure=$_.Exception.Message}
  finally{if($p -and -not $p.HasExited){try{Stop-Process -Id $p.Id -Force}catch{}}; Append ([pscustomobject]$row)}
 }
}finally{[Environment]::SetEnvironmentVariable('GLIDE_SETTINGS_DIRECTORY',$prev,'Process'); [Environment]::SetEnvironmentVariable('GLIDE_BENCHMARK_INJECT_PREFENCE_EVENT',$prevInject,'Process')}
$rows=Import-Csv $csv; $ok=@($rows|?{[string]::IsNullOrWhiteSpace($_.Failure)}|%{[double]$_.CompositionRenderedMs}|sort); $sum=[pscustomobject]@{attempts=$rows.Count;successes=$ok.Count;failures=$rows.Count-$ok.Count;p50_ms=if($ok.Count){$ok[[math]::Floor(($ok.Count-1)*.5)]}else{$null};p95_ms=if($ok.Count){$ok[[math]::Floor(($ok.Count-1)*.95)]}else{$null}}; $sum|Format-List; $sum|ConvertTo-Json|Set-Content (Join-Path $root 'summary.json')
Write-Host "Internal decomposition only. User-visible cold-launch verdict requires benchmark-visible-first-pixel.ps1. Results: $csv"
