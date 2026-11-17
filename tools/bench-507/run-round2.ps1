<#
.SYNOPSIS
  5.0.7 benchmark round 2 (after the quick tier): JPEG XL / JPEG 2000 alternatives, CLI decoders, the libvips
  "all" bundle, full-size WebP/AVIF/HEIC/JPEG, every real RAW file (Glide vs LibRaw) and cold-start samples.
  Strictly serial: waits for an idle PC with no build/test process before the harness build, the synthetic
  generation and every group, and the harness itself pauses while a build/test process exists (--idle-block).
  Results: $env:USERPROFILE\Downloads\Glide-bench-results\5.0.7-r2-*  Log: ...\Glide-bench-results-round2.log
#>
param([string]$Only = "", [switch]$NoBuild, [switch]$NoGenerate, [int]$QuietSeconds = 120, [int]$Threshold = 15, [switch]$NoWait)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$art = Join-Path $repo "tools\bench-507\.out"
$exe = Join-Path $art "bin\Glide.DecodeBench507\release\Glide.DecodeBench507.exe"
$codecs = "$env:USERPROFILE\Downloads\Glide-bench-codecs"
$synth = "$env:USERPROFILE\Downloads\Glide-bench-synthetic"
$real = "$env:USERPROFILE\Downloads\Glide-bench-corpus"
$log = "$env:USERPROFILE\Downloads\Glide-bench-results-round2.log"
$wait = Join-Path $PSScriptRoot "wait-idle.ps1"

function Wait-Idle([string]$what) {
    if ($NoWait) { return }
    "[$(Get-Date -Format HH:mm:ss)] waiting for idle before $what" | Tee-Object -Append $log
    & powershell -NoProfile -ExecutionPolicy Bypass -File $wait -QuietSeconds $QuietSeconds -Threshold $Threshold | Tee-Object -Append $log
    if ($LASTEXITCODE -ne 0) { throw "machine never became idle before $what" }
}

if (-not $NoBuild) {
    Wait-Idle "harness build"
    # --disable-build-servers: no MSBuild node / VBCSCompiler left behind to block the idle check.
    & dotnet build "$repo\tools\bench-507\Glide.DecodeBench507\Glide.DecodeBench507.csproj" -c Release -m:1 -nologo -v q -clp:ErrorsOnly --disable-build-servers "-p:ArtifactsPath=$art" | Tee-Object -Append $log
    if ($LASTEXITCODE -ne 0) { throw "harness build failed" }
    # The harness must decode with the bridge the app ships (dist\ is what build.cmd produced).
    if (Test-Path "$repo\dist\Glide.Native.dll") { Copy-Item "$repo\dist\Glide.Native.dll" (Split-Path $exe) -Force }
}
if (-not $NoGenerate) {
    Wait-Idle "synthetic generation"
    & python "$repo\tools\bench-507\generate_synthetic.py" $synth --tier full --only "jxl,jp2,avif,heic,webp" --confirm --jobs 4 `
        --cjxl "$codecs\libjxl\jxl-x64-windows-x\x64-windows\bin\cjxl.exe" | Tee-Object -Append $log
    if ($LASTEXITCODE -ne 0) { throw "generation failed" }
}

$common = @("--box", "2560x1369", "--baseline", "glide", "--out", "$env:USERPROFILE\Downloads\Glide-bench-results", "--idle-check", "case",
    "--idle-threshold", "30", "--idle-window", "500", "--idle-timeout", "5000", "--shuffle", "507", "--warmup", "1", "--confirm")
$groups = [ordered]@{
    "jxl-jp2"       = @("--corpus", "synthetic:$synth", "--format", "jxl,jp2", "--engines", "glide", "--reps", "5", "--reps-large", "3")
    "jxl-jp2-cli"   = @("--corpus", "synthetic:$synth", "--format", "jxl,jp2", "--engines", "djxl,opj", "--modes", "full", "--reps", "5", "--reps-large", "3")
    "jxl-jp2-vipsall" = @("--corpus", "synthetic:$synth", "--format", "jxl,jp2", "--engines", "vips-all", "--reps", "5", "--reps-large", "3")
    "webp-avif-heic" = @("--corpus", "synthetic:$synth", "--format", "webp,avif,heic", "--engines", "glide,skia,vips", "--reps", "5", "--reps-large", "3")
    "webp-avif-cli" = @("--corpus", "synthetic:$synth", "--format", "webp,avif", "--engines", "dwebp,avifdec", "--modes", "full", "--reps", "5", "--reps-large", "3")
    "jpeg-full"     = @("--corpus", "synthetic:$synth", "--format", "jpeg", "--engines", "glide,wic,turbojpeg,turbojpeg-bands", "--modes", "full", "--reps", "5", "--reps-large", "3")
    "raw-all"       = @("--corpus", "real:$real", "--format", "3fr,arw,cr2,cr3,crw,dcr,dng,erf,fff,gpr,iiq,kdc,mdc,mef,mos,mrw,nef,nrw,orf,ori,pef,raf,raw,rw2,rwl,sr2,srf,srw,x3f", "--engines", "glide,libraw", "--reps", "5")
    # Cold = first decode in a brand-new process (DLL load + codec init inside the timer), first frame only.
    "cold-synth"    = @("--corpus", "synthetic:$synth", "--format", "jpeg,tiff,png,webp,avif,heic,jxl,jp2", "--mp", "24", "--variant", "q90,lzw,rgb8-z6,lossy-q85,q70,q80,d1-e3,r20",
                        "--engines", "glide,wic,turbojpeg", "--modes", "preview", "--reps", "3", "--cold-reps", "3", "--cold-max-mp", "30")
    "cold-raw"      = @("--corpus", "real:$real", "--format", "cr3,nef,arw,orf,dng", "--max-per-format", "1",
                        "--engines", "glide,libraw", "--modes", "preview", "--reps", "3", "--cold-reps", "3", "--cold-max-mp", "200")
}
foreach ($name in $groups.Keys) {
    if ($Only -and ($Only -split ',') -notcontains $name) { continue }
    Wait-Idle "group $name"
    "[$(Get-Date -Format HH:mm:ss)] ==== $name ====" | Tee-Object -Append $log
    $groupArgs = @("run") + $groups[$name] + @("--label", "5.0.7-r2-$name") + $common
    $ErrorActionPreference = "Continue" # the harness writes progress to stderr; that is not a failure
    & $exe @groupArgs *>> $log
    $ErrorActionPreference = "Stop"
    "[$(Get-Date -Format HH:mm:ss)] ==== $name exit $LASTEXITCODE ====" | Tee-Object -Append $log
}
