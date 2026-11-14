# Glide: .NET 8 to .NET 10 LTS migration plan

Status: plan only. No project file was changed to produce it. Every probe ran on a copy of the sources under the agent scratchpad (`...\scratchpad\net10-probe\`).
Target: the first follow-up release after 5.0.0 (call it 5.1.0).
Date of investigation: 2026-11-14. **.NET 8 support ended on 2026-11-10**, so Glide is already running on an unsupported runtime. The current NativeAOT publish also reports `NU1903: Package 'Microsoft.DotNet.ILCompiler' 8.0.31 has a known high severity vulnerability (GHSA-p3jp-9v83-76w9)`. That advisory is the strongest reason to ship this soon.

---

## 1. Bottom line

The migration is small. A scratch copy of the current tree was retargeted to `net10.0` with **only `TargetFramework` and `global.json` changed**. The results:

| Check (scratch copy) | net8.0 (SDK 8.0.425) | net10.0 (SDK 10.0.403) |
|---|---|---|
| `dotnet build Glide.sln -c Release` | 0 errors, 929 unique warnings | 0 errors, 931 unique warnings (4 new, 2 gone; all analyzer/style) |
| `dotnet test Glide.sln` | n/a | **894 / 894 passed** (Core 873, Input 21)* |
| Off-solution projects (Glide.Explorer.Tests, Glide.PerfBench) | builds | builds |
| NativeAOT publish win-x64 | OK, 3 IL warnings + 5x NU1903 | OK, the same 3 IL warnings, **no NU1903** |
| `Glide.exe` size (AOT) | 38,300,672 B | **35,573,248 B (-7.1 %)** |
| AOT exe `--write-diagnostic-fixtures` (headless) | (build.cmd does this) | OK: 10 core fixtures, 196 routing probes, 240 nav JPEGs |
| Publish time | 31 s | 34 s |

\* Three Core tests first failed only because the scratch copy lacked repo files they read (`installer\Glide.iss`, `native\...\CMakeLists.txt`, `GlideThumbnailProvider.cpp`). They passed once those files were copied in.

No source code changes are required to compile, test or AOT-publish on .NET 10. The remaining work is: package hygiene, the build script and docs, deliberate decisions on two .NET 10 defaults (CET and the C# language version), and verification (perf plus a manual smoke test of shell/codec interop).

---

## 2. Current state (as found)

| Item | Value | Where |
|---|---|---|
| TFM | `net8.0` (single, global) | `Directory.Build.props:3` |
| SDK pin | `"version": "8.0.100", "rollForward": "latestFeature"` | `global.json` |
| C# version | `LangVersion 12` (deliberately pinned; comment says SDK upgrades must not change semantics) | `Directory.Build.props:11` |
| Analyzers | `AnalysisMode=Recommended`, `EnforceCodeStyleInBuild=true`, `TreatWarningsAsErrors=false` | `Directory.Build.props` |
| Output layout | `UseArtifactsOutput=true`, `ArtifactsPath=.artifacts`. Paths contain **no TFM** (`.artifacts\bin\Glide.App\release\Glide.exe`), so the TFM change does not move any path used by scripts. | `Directory.Build.props`, `tools\perf\run-perf.ps1:30-31` |
| Shipping publish | `dotnet publish src\Glide.App\Glide.App.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -o dist` (vswhere dir added to PATH first) | `build.cmd` [7/8] |
| AOT size knobs | `IlcOptimizationPreference=Size`, `EventSourceSupport/MetricsSupport/HttpActivityPropagationSupport/DebuggerSupport=false` | `Directory.Build.props` |
| Installer | Inno Setup, `ArchitecturesAllowed=x64compatible`, packages `dist\*`. **No .NET runtime prerequisite** (self-contained AOT). No `MinVersion` set. | `installer\Glide.iss` |
| CI | None in repo (no `.github`/yml). `build.cmd` (restore, build, test, self-test, publish, fixtures, installer) is the gate. | |
| Test projects | Core.Tests and Input.Tests are in `Glide.sln`. **Glide.Explorer.Tests is not in the solution**, so `build.cmd` never builds or runs it. | `Glide.sln` |
| Perf harness | `tools\perf\Glide.PerfBench` (JIT, not in sln) + `run-perf.ps1` / `compare-perf.ps1` / `generate_perf_data.py` | `tools\perf` |

### Packages (resolved graph of Glide.App)

| Package | Current | Latest seen on nuget.org | net10 verdict |
|---|---|---|---|
| Avalonia, .Win32, .Skia, .Themes.Fluent, .Headless, .Diagnostics (Debug) | 11.3.20 | 11.3.23 (11.x), 12.1.6 (12.x) | 11.3 ships `net8.0` assets, which net10 consumes fine (proven above). **Stay on 11.3.x**; optionally bump to 11.3.23. Avalonia 12 is a separate major migration (SkiaSharp 3, API breaks). Do not combine it with this one. |
| Avalonia.Controls.ItemsRepeater | 11.1.5 | 12.0.0 | Keep 11.1.5 (12.0.0 pairs with Avalonia 12). |
| SkiaSharp (+NativeAssets) | 2.88.9 (transitive via Avalonia.Skia) | 3.119.4 / 4.156.0 | Pinned by Avalonia 11.3 (`SkiaSharp 2.88.9`). Must stay 2.88.x while on Avalonia 11. |
| HarfBuzzSharp | 8.3.1.1 | 14.4.0.100 | Pinned by Avalonia 11.3. Keep. |
| SkiaSharp.HarfBuzz | 2.88.6 (via Svg.Skia) | | Keep. |
| Svg.Skia / Svg.Model / Svg.Custom / ShimSkiaSharp | 1.0.0.18 | 5.2.3 | 1.0.0.19 and 2.0.0.x still target SkiaSharp 2.88. **5.x needs SkiaSharp 4.148 and HarfBuzzSharp 14**, which is incompatible with Avalonia 11.3. Keep 1.0.0.18 (or move to 1.0.0.19). |
| ExCSS | 4.2.3 (transitive) | 4.3.2 | Keep (transitive). |
| System.Drawing.Common | **8.0.11** (direct, printing) | 10.0.15 / 11.0.0 | **Bump to the latest 10.0.x** to match the runtime. Version 8.0.x is out of support too. Windows-only, same API surface for GDI+ printing. |
| Microsoft.DotNet.ILCompiler (implicit) | 8.0.31/8.0.32 (NU1903 vulnerable) | 10.0.15 | Comes from the SDK. SDK 10.0.403 resolved 10.0.14. |
| Microsoft.NET.Test.Sdk | 17.11.1 | 18.12.0 | 17.11.1 works on net10 (proven). Bump is optional (WP5). |
| xunit / xunit.runner.visualstudio | 2.9.2 / 2.8.2 | 2.9.3 / 3.1.5 (v2 line), xunit.v3 4.0.3 | 2.9.2 works on net10 (proven). Optional bump to xunit 2.9.3 + runner 3.1.x. **Do not** move to xunit.v3 in this release. |

### AOT/trim posture of the code

- JSON uses source generation (`JsonSerializerContext`, 4 contexts, 17 `[JsonSerializable]`). Reflection-based `JsonSerializer` calls appear only in tests, which run JIT where they are fine.
- COM: production uses `GeneratedComInterface`/`StrategyBasedComWrappers`. The only `[ComImport]` is in `tests\Glide.Core.Tests\ShellThumbnailProviderTests.cs`, which runs JIT.
- P/Invoke: 117 `DllImport` + 35 `LibraryImport`. No behaviour change expected.
- No `BinaryFormatter`, `Reflection.Emit`, `Type.GetType` or `Activator.CreateInstance`.
- AOT warnings are identical on net8 and net10: `IL2104` (ExCSS, Svg.Custom trim) and `IL3053` (Svg.Custom AOT). These are pre-existing, third-party, and covered by the SVG fixtures.

### Toolchain on this machine

- SDKs: 8.0.425, **10.0.402, 10.0.403**. Runtimes: 8.0.31, 10.0.13, 10.0.14 (NETCore, WindowsDesktop, AspNetCore).
- VS: Visual Studio Community 2026 (18.10) plus Build Tools 18 with MSVC 14.51. The NativeAOT linker works.
- A NativeAOT hello-world on net10 published and ran (`Hello, World!`, 1.1 MB). Note: it **fails with `'vswhere.exe' is not recognized`** unless `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` is on PATH. `build.cmd` already handles this. Any new script or agent job must do the same.
- Nothing needs installing. On another machine, install the .NET 10 SDK x64 (10.0.1xx or later; latest 10.0.4xx recommended) and VS 2022/2026 Build Tools with "Desktop development with C++".

---

## 3. Blockers and decisions

**Hard blockers: none.** Everything compiles, tests and publishes.

**Decisions the owner must make (in priority order):**

1. **CET shadow stack (new default, high impact).** The .NET 10 ILCompiler targets link NativeAOT exes with `/CETCOMPAT` by default (`Microsoft.NETCore.Native.Windows.targets:107-110`; the 8.0 targets do not). On CET-capable CPUs (Intel 11th gen+/AMD Zen 3+) running Windows 11 or 10 21H2+, Windows enforces hardware shadow stacks for the whole process. Glide loads a lot of third-party native code in-process:
   - the embedded Windows Explorer host (shell extensions, context-menu handlers, preview and thumbnail handlers from arbitrary vendors)
   - WIC codecs (HEIF/AVIF/RAW/JPEG XL extensions)
   - the downloaded FFmpeg pack
   - files in the `codecs\` folder
   - `Glide.Native.dll`

   A non-compliant DLL (setjmp/longjmp tricks, return-address patching, some older packers or DRM) terminates the process with `STATUS_STACK_BUFFER_OVERRUN` (0xC0000409) and leaves no managed stack.
   *Recommendation:* ship with the default (CET on, a free hardening win). Run the interop smoke test in WP4 on a CET-capable machine. Keep `<CETCompat>false</CETCompat>` in Glide.App.csproj as the documented one-line fallback, and use it if any shell extension or codec crash shows up in beta.
2. **C# language version.** `LangVersion` is pinned to 12. Keep 12 for the migration release (zero semantic risk; already proven). Raising to 14 (`field` keyword, extension members, first-class span conversions, which change overload resolution for some `Span`/array calls) is a separate, later work package with its own test pass.
3. **Avalonia 12 / SkiaSharp 3+ / Svg.Skia 5.** Explicitly out of scope. These are a major UI-stack migration.
4. **Inno `MinVersion`.** .NET 10 supports Windows 10 1607+ / Windows 11 / Server 2012+ (x64). That is the same floor as .NET 8, so this is not a regression. Adding `MinVersion=10.0.14393` to `Glide.iss` turns a silent startup failure on Windows 7/8.1 into a clear installer message. This is optional.

**Pre-existing finding (not caused by .NET 10):** `IlcOptimizationPreference` in `Directory.Build.props` is **not a recognized property** in either the 8.0 or 10.0 ILCompiler/SDK targets (grep finds no consumer). The documented property is `OptimizationPreference`. Today's "Size" preference is therefore a no-op, and the shipping exe uses the default (blended) optimization. Do not fix it silently inside the migration, because it changes perf and size. Treat it as WP7: measure `OptimizationPreference=Size` and `Speed` against the default with tools\perf, then decide.

---

## 4. Work packages (ordered; each can be one agent job)

All jobs work in the project folder only after the 5.0.0 release is cut. Every agent job must follow these rules:
- never touch `C:\Program Files\Glide`
- only run or kill Glide processes started from the project folder
- never drive the desktop UI (manual checks go to the owner)
- put `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` on PATH before any AOT publish

### WP0: Baseline capture (before any change), about 1 h, agent
- On the 5.0.0 tree, build the AOT candidate into a side folder and capture perf and size. See §6 for commands.
- Archive `dist\Glide.exe` size, `--write-diagnostic-fixtures` output, and the perf result folder labelled `net8`.
- Exit criteria: `perf-results\net8\` exists with app, decode, navigate, render and ui JSON.

### WP1: Retarget (core change), about 1 h, agent
Edit exactly these files:
- `global.json` becomes `{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature", "allowPrerelease": false } }`. This keeps the existing roll-forward policy; any installed 10.0.1xx-4xx SDK satisfies it.
- `Directory.Build.props`: `<TargetFramework>net10.0</TargetFramework>`. Leave `LangVersion` at 12. Update the stale comment at line 12 if desired.
- `src\Glide.App\Glide.App.csproj`: `System.Drawing.Common` 8.0.11 becomes the latest `10.0.x` (10.0.15 at time of writing).
- Add an explicit, commented `<CETCompat>true</CETCompat>` (or `false` if decision 1 goes that way) to `Glide.App.csproj`, so the choice is visible instead of an implicit SDK default.
- Exit criteria: `dotnet build Glide.sln -c Release` succeeds with 0 errors, and the warning diff against the WP0 log contains only the expected new items (CA1870, CS9191, 2x CA1859; see §5).

### WP2: Build script, installer and docs, about 1 h, agent
- `build.cmd` ([preflight], currently line 114): change `Install .NET 8 SDK x64` to `Install .NET 10 SDK x64`.
- Optional hardening in `build.cmd` preflight: fail fast with a clear message when `dotnet --list-sdks` has no `10.` entry, instead of the generic global.json error.
- `docs\GLIDE_MANIFESTO_AND_HANDOFF.md:38`: Toolchain becomes .NET 10 SDK (x64), with VS 2022/2026 Build Tools C++ x64.
- `installer\Glide.iss`: optionally add `MinVersion=10.0.14393` (decision 4). No other installer change. Output paths, the `dist\*` payload layout, and the absence of a runtime prerequisite are all unchanged.
- `CHANGELOG.md` / release notes: "Runtime moved to .NET 10 LTS (supported until Nov 2028); NativeAOT binary about 7 % smaller; CET shadow-stack compatible."
- `tools\package-source.ps1`, `tools\build-installer.cmd` and `tools\perf\*.ps1`: no change needed (they contain no TFM-specific paths; verified).
- Exit criteria: `build.cmd --no-pause` completes end to end on the owner's machine. The owner runs it, because build.cmd wipes `dist\` and builds the installer.

### WP3: Analyzer and warning hygiene, about 1-2 h, agent, can run in parallel with WP2
Fix the 4 new warnings (or suppress them with a reason):
- `src\Glide.App\Explorer\ExplorerFileOperations.cs(75)`: CA1870, use a static `SearchValues<char>`.
- `src\Glide.App\Platform\WindowsExplorerHost.cs(235)`: CS9191, `ref` to `in` for an `in` parameter.
- `src\Glide.App\Services\ExternalLaunchBroker.cs(553)` and `src\Glide.App\Settings\SettingsWindow.axaml.cs(1856)`: CA1859 (the newer analyzer is more aggressive). Prefer suppressing with a justification over changing public shapes.
- Exit criteria: the warning count is less than or equal to the WP0 baseline. `build.cmd` style enforcement still passes.

### WP4: Verification (tests, AOT, interop smoke), about 2-3 h agent + about 1 h owner manual
- Agent:
  - full `dotnet test Glide.sln`
  - also build **and run** `tests\Glide.Explorer.Tests`, which is not in the sln
  - AOT publish to a side folder
  - `--write-diagnostic-fixtures` plus `tools\fixture-manifest.ps1` validation
  - `--diagnostics self-test`
  - diff the fixture outputs (decode hashes/routing) between the WP0 net8 exe and the net10 exe, which should be identical apart from timings
- Owner (manual, on a CET-capable PC; check with `Get-CimInstance Win32_Processor` plus Windows Security > Device security > "Kernel-mode Hardware-enforced Stack Protection" capability):
  1. Open JPEG, PNG, WebP, TIFF, GIF, SVG, HEIC/AVIF (WIC extension), RAW and video (Windows path), and a video that forces the FFmpeg fallback.
  2. Open the embedded Explorer pane. Right-click files (third-party context-menu handlers: 7-Zip, Git, cloud-drive overlays). Use the preview pane and the thumbnail views.
  3. Print preview and print to PDF (System.Drawing.Common 10).
  4. Explorer thumbnail provider (`Glide.ShellThumbnail.dll` registration via the installer).
  5. Settings round-trip and Settings search (no new settings in this release, so `tools/audit-settings-search.py` should be unchanged; run it anyway per AGENTS.md).
  6. Install over 5.0.0 with the installer, then uninstall.
- Exit criteria: all green, no 0xC0000409 crashes. If a CET crash appears, set `<CETCompat>false</CETCompat>`, rebuild and re-test (decision 1 fallback).

### WP5: Optional test-package refresh, about 1 h, agent, after WP4
- `Microsoft.NET.Test.Sdk` 17.11.1 to 18.x; `xunit` 2.9.2 to 2.9.3; `xunit.runner.visualstudio` 2.8.2 to 3.1.x (all three test csprojs).
- Keep on VSTest. Do not opt into `Microsoft.Testing.Platform` / `dotnet test` MTP mode in `global.json` in this release.
- Exit criteria: test counts identical (894 in sln + Explorer.Tests).

### WP6: Perf regression gate, about 2 h agent (+ machine time)
See §6. Exit criteria: no metric regresses more than 5 % (median) against WP0 in `app` (first frame) or `decode`. Any regression above 5 % must be explained or fixed before release.

### WP7: Optional follow-ups (separate releases, not part of 5.1)
- `IlcOptimizationPreference` to `OptimizationPreference` (Size vs Speed vs default), measured with WP6 tooling.
- `LangVersion` 12 to 14, with a full test pass.
- Add `tests\Glide.Explorer.Tests` to `Glide.sln` (or document why it is excluded).
- Avalonia 12 / SkiaSharp 3 / Svg.Skia 5: its own plan.

**Dependency order:** WP0, then WP1, then (WP2 and WP3 in parallel), then WP4, then WP6, then WP5 (optional), then release. WP7 comes later.

---

## 5. Expected warnings after retarget (from the probe)

New on net10, with the same code:
```
src\Glide.App\Explorer\ExplorerFileOperations.cs(75,29): warning CA1870  (SearchValues)
src\Glide.App\Platform\WindowsExplorerHost.cs(235,63): warning CS9191   ('ref' -> 'in')
src\Glide.App\Services\ExternalLaunchBroker.cs(553,113): warning CA1859
src\Glide.App\Settings\SettingsWindow.axaml.cs(1856,82): warning CA1859
```
Gone on net10: two `CA1708` warnings on generated COM types.
AOT publish (same on both TFMs): `IL2104` ExCSS, `IL2104` Svg.Custom, `IL3053` Svg.Custom.
Gone on net10: `NU1903` (vulnerable ILCompiler 8.0.31).

Line numbers will drift as 5.0 work lands. Re-run the diff in WP1.

---

## 6. Exact commands

Run from a developer PowerShell in the project root. These are for the migration branch after 5.0.0, not for now.

```powershell
# --- prerequisites / sanity
dotnet --list-sdks                      # needs a 10.0.x line (10.0.403 present here)
$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"   # NativeAOT linker needs vswhere
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1

# --- WP0 baseline (on the untouched 5.0.0 tree)
dotnet build Glide.sln -c Release "-flp:logfile=$env:TEMP\glide-net8-build.log;verbosity=minimal"
dotnet publish src\Glide.App\Glide.App.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -o "$env:TEMP\glide-aot-net8"
Copy-Item native\Glide.Native\build\Release\Glide.Native.dll "$env:TEMP\glide-aot-net8\" -Force
dotnet build tools\perf\Glide.PerfBench\Glide.PerfBench.csproj -c Release -o "$env:TEMP\glide-bench-net8"

# --- WP1 retarget, then verify
dotnet restore Glide.sln
dotnet build Glide.sln -c Release --no-restore -m "-flp:logfile=$env:TEMP\glide-net10-build.log;verbosity=minimal"
dotnet test Glide.sln -c Release --no-build
dotnet build tests\Glide.Explorer.Tests\Glide.Explorer.Tests.csproj -c Release
dotnet test  tests\Glide.Explorer.Tests\Glide.Explorer.Tests.csproj -c Release --no-build
dotnet run --project src\Glide.App\Glide.App.csproj -c Release --no-build -- --diagnostics self-test
dotnet list src\Glide.App\Glide.App.csproj package --vulnerable --include-transitive

# --- AOT candidate in a side folder (never into dist\ while another job uses it)
dotnet publish src\Glide.App\Glide.App.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -o "$env:TEMP\glide-aot-net10"
Copy-Item native\Glide.Native\build\Release\Glide.Native.dll "$env:TEMP\glide-aot-net10\" -Force
$env:GLIDE_SETTINGS_DIRECTORY = "$env:TEMP\glide-iso-settings"
& "$env:TEMP\glide-aot-net10\Glide.exe" --write-diagnostic-fixtures "$env:TEMP\glide-fixtures-net10"
powershell -NoProfile -ExecutionPolicy Bypass -File tools\fixture-manifest.ps1 -Mode Write    -Directory "$env:TEMP\glide-fixtures-net10" -Generator "$env:TEMP\glide-aot-net10\Glide.exe" -SourceIdentity "Glide-net10-probe"
powershell -NoProfile -ExecutionPolicy Bypass -File tools\fixture-manifest.ps1 -Mode Validate -Directory "$env:TEMP\glide-fixtures-net10" -Generator "$env:TEMP\glide-aot-net10\Glide.exe" -SourceIdentity "Glide-net10-probe"

# --- WP6 perf comparison (tools\perf). The "app" mode launches visible Glide windows
#     (isolated settings, --force-new-instance, auto-exit after first frame): run it when the PC is idle.
dotnet build tools\perf\Glide.PerfBench\Glide.PerfBench.csproj -c Release -o "$env:TEMP\glide-bench-net10"
python tools\perf\generate_perf_data.py <corpus-dir>          # once; same corpus for both runs
powershell -ExecutionPolicy Bypass -File tools\perf\run-perf.ps1 -Data <corpus-dir> -Out <perf-results> -Label net8  -NoBuild -AppExe "$env:TEMP\glide-aot-net8\Glide.exe"  -BenchExe "$env:TEMP\glide-bench-net8\Glide.PerfBench.exe"  -Runs 7 -AppRuns 10
powershell -ExecutionPolicy Bypass -File tools\perf\run-perf.ps1 -Data <corpus-dir> -Out <perf-results> -Label net10 -NoBuild -AppExe "$env:TEMP\glide-aot-net10\Glide.exe" -BenchExe "$env:TEMP\glide-bench-net10\Glide.PerfBench.exe" -Runs 7 -AppRuns 10
powershell -ExecutionPolicy Bypass -File tools\perf\compare-perf.ps1 -Results <perf-results> -Before net8 -After net10 -Out <perf-results>\net8-vs-net10.md

# --- full release gate (owner; wipes dist\ and builds the installer)
build.cmd --no-pause
```

Check `generate_perf_data.py --help` for its exact arguments before running it. The script was not executed during this investigation. `-BenchExe` measures the JIT runtime (decode, navigate, render, ui). `-AppExe` measures the shipped NativeAOT exe, which is the number users feel. Run net8 and net10 back to back on the same power plan and an idle machine. Alternate the order on a second pass to cancel thermal and cache bias.

---

## 7. Test strategy

| Layer | What | Pass bar |
|---|---|---|
| Compile | sln + Explorer.Tests + PerfBench on net10 | 0 errors; warning diff limited to §5 |
| Unit/headless | `dotnet test Glide.sln` (873 Core + 21 Input today) + Explorer.Tests | Same counts as net8, 0 failures |
| AOT smoke (headless) | `--diagnostics self-test`, `--write-diagnostic-fixtures` + manifest validate | Success; fixture decode outputs identical to the net8 exe |
| AOT analysis | publish log | Only IL2104 x2 / IL3053 (pre-existing, third-party). Any **new** IL2xxx/IL3xxx from Glide code blocks release. |
| Perf | tools\perf `app`, `decode`, `navigate`, `render`, `ui`, `cold` | Median regression of at most 5 %; first-frame (`app`) is the primary metric |
| Interop/CET manual | WP4 owner list, on a CET-capable PC | No crashes; Explorer pane, context menus, WIC codecs, FFmpeg fallback, print all work |
| Install | Inno install over 5.0.0, uninstall, Explorer thumbnail provider | Works; no runtime prerequisite prompt |

---

## 8. Risks and rollback

| # | Risk | Likelihood / impact | Mitigation |
|---|---|---|---|
| 1 | **CET shadow-stack crashes** from third-party code loaded in-process (shell extensions in the embedded Explorer host, WIC codecs, FFmpeg, `codecs\`). New default `/CETCOMPAT` in the .NET 10 NativeAOT link. | Medium / High (hard crash, no managed trace, only on newer CPUs) | WP4 manual interop pass on CET hardware; `<CETCompat>false</CETCompat>` one-line fallback; note in release notes |
| 2 | **Perf regression in cold launch or first frame** (new GC/codegen defaults, a different ILCompiler, larger or smaller static data). Cold launch is Glide's overriding constraint. | Low-Medium / High | WP0/WP6 A/B with tools\perf on identical corpora; 5 % gate; size dropped 7 %, which tends to help cold start |
| 3 | **Concurrency with 5.0 work and subtle runtime behaviour changes** (System.Text.Json, globalization/ICU, `Process`/shell APIs, analyzer churn), surfacing only in manual paths that tests do not cover; plus scripts or agents forgetting the vswhere PATH fix. | Low / Medium | Start only after 5.0.0 is tagged; one isolated branch; fixture diff net8 vs net10; settings round-trip tests (already passing); keep `LangVersion 12` |
| 4 | Third-party packages (Avalonia 11.3, SkiaSharp 2.88, Svg.Skia 1.0) have no `net10.0` assets and stay on `net8.0`/netstandard builds | Low / Low | Proven to work (build, test and AOT). Avalonia 11.3 is still serviced (11.3.23). Revisit with Avalonia 12. |
| 5 | Toolchain drift: SDK 10.0.4xx ILCompiler newer than tested | Low / Low | `global.json` `latestFeature`; record the SDK used in release notes; rerun WP4 if the SDK changes before release |

**Rollback:**
- The change set is small: global.json, Directory.Build.props, one package version, a CETCompat line, build.cmd text and docs. Revert that commit (or the 4-5 lines) and rebuild.
- Keep the .NET 8 SDK installed until 5.1 has been in the field for one release cycle.
- The product is self-contained NativeAOT. Users have no runtime dependency, so a rollback is just shipping 5.1.1 built from the reverted tree. Settings, history and workspace JSON formats are unchanged (same source-gen contexts; round-trip tests pass), so downgrade or upgrade is data-safe.
- A partial rollback for CET issues only: set `CETCompat=false` and keep .NET 10.

---

## 9. Effort estimate

| WP | Agent time | Owner time |
|---|---|---|
| WP0 baseline | 1 h | (machine idle for perf) |
| WP1 retarget | 1 h | review |
| WP2 scripts/installer/docs | 1 h | run `build.cmd` (~15-30 min) |
| WP3 warnings | 1-2 h | review |
| WP4 verification | 2-3 h | ~1 h manual interop/CET pass |
| WP5 test packages (optional) | 1 h | none |
| WP6 perf | 2 h (+ machine time) | review the comparison |
| **Total** | **~1.5 working days** (about 1 day without WP5) | **~2-3 h** |

---

## 10. Evidence (scratch artifacts)

Under a scratch folder named `net10-probe` (not part of the repository):
- `hello\`: net10 NativeAOT hello-world (works with vswhere on PATH)
- `glide\`: retargeted snapshot of src/tests/perf
- `build-net8.keep.log`, `build-net10-step1.log`: warning diff inputs
- `publish-net8.log`, `publish-net10.log`: AOT publish warnings
- `dist-net8\`, `dist-net10\`: AOT outputs (size comparison)
- `test-net10\*.trx`: test results
- `fixtures-net10\`: headless fixture output from the net10 AOT exe
