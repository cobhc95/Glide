# Directory cleanup for 5.0.0 (manifest)

Status: DONE. Nothing in `src/`, `tests/` logic, `native/` sources, `installer/` sources, `dist/`, `dist-installer/`,
`.artifacts/` or the root `Glide-*.zip/exe` release artifacts was touched.

Recovery: every deleted file is in `Archive/cleanup-deleted-20261114-125941.zip` (original relative paths). The
pre-edit originals of every edited text file are in the same zip under `_edited-originals/`.

## Root layout after cleanup
Folders: `src/ tests/ native/ tools/ installer/ docs/ codecs/ Archive/` plus generated `.artifacts/ artifacts/ dist/ dist-installer/`.
Files: `Glide.sln Directory.Build.props global.json build.cmd .editorconfig .gitignore AGENTS.md README.md CHANGELOG.md`
plus build-generated `Glide-<ver>.exe`, `Glide-<ver>-Portable.zip`, `Glide-Zero-Context-Handover.zip`.
(`codecs/` stays at root: build.cmd indexes it and xcopies it to `dist\codecs`. No `nuget.config` or `LICENSE` exists.)
New release notes go in `docs/releases/`.

## Moves (old -> new, reason)
| Old path | New path | Reason |
|---|---|---|
| release-notes-4.2.3 ... 4.8.0.md (11 files, root) | docs/releases/ | release history out of the root; README/CHANGELOG links updated. (The 4.8.0 draft was dropped afterwards: 4.8.0 was never published, its changes are in 5.0.0, so `docs/releases/` has no 4.8.0 file.) |
| Archive/release-notes-4.1.1, 4.1.4, 4.1.5, 4.1.6, 4.1.7, 4.1.9, 4.2.0, 4.2.1, 4.2.2.md | docs/releases/ | all shipped release notes in one place |
| GLIDE_MANIFESTO_AND_HANDOFF.md | docs/history/GLIDE_MANIFESTO_AND_HANDOFF.md | loose doc; tools/package-source.ps1 updated |
| GITHUB_DESCRIPTION.txt | docs/GITHUB_DESCRIPTION.txt | loose doc (refreshed for 5.0.0 afterwards) |
| package-source.ps1 | tools/package-source.ps1 | script; project root now derived as parent of tools\ |
| build-installer.cmd | tools/build-installer.cmd | script; now `cd /d "%~dp0.."`, log still written to root `installer-output.txt` |
| run.cmd | tools/run.cmd | script; now `cd /d "%~dp0.."` |

## Consolidated into zips (originals removed only after the zip listing was verified file-for-file)
| Old | New |
|---|---|
| Archive/backup-* (11 folders, 646 files, all 20261113*/20261114* session backups) | Archive/session-backups-4.8-5.0.zip |
| Archive/AUDIT_REPORT_2026-09-19.md, AUDIT_SETTINGS_SEARCH_4.2.3.md, GLIDE_MANIFESTO_HISTORY_4.2.2.md, release-notes-4.2.0-1/-2/-3.md (interim drafts), loose-files/ (2 jpgs) | Archive/legacy-docs-pre-4.5.zip |

## Deletions (copies in Archive/cleanup-deleted-20261114-125941.zip)
| Path | Reason |
|---|---|
| ImageViewport.cs (root, 0 bytes) | stray empty file; real file is src/Glide.App/Controls/ImageViewport.cs |
| 1.jpg, Screenshot_1.jpg | byte-identical duplicates of Archive/loose-files copies (kept in legacy zip) and docs/screenshots/screenshot_1.jpg |
| Hero 4.6.jpg, Welcome to Glide.jpg | byte-identical duplicates of docs/screenshots/4.6/hero.jpg, welcome.jpg (used by README) |
| Screenshots 4.6/ (63 PNGs) | raw capture set; every README-referenced image already lives in docs/screenshots/4.6 (the rest are duplicates/immediate-vs-settled captures) |
| docs/screenshots/settings/ (26 PNGs), home.png, screenshot_1.jpg | old gallery, not referenced by README, CHANGELOG or any doc |
| docs/screenshots/4.6/settings_overlays_scroll3_settled.png | unreferenced by README |
| tools/create-handover-zip.ps1 | superseded duplicate of package-source.ps1 (same output name, weaker exclusions); referenced only by a historical CHANGELOG line |
| artifacts/print-acceptance/, readme-captures/, thumbnail-acceptance/, format-acceptance/*.png | generated, git-ignored evidence from 2026-11-05 (kept format-acceptance/sample-gradient.tga + sample-vector.svg: optional inputs of tools/thumbnail-benchmark.ps1) |
| Archive/release-notes-4.2.3.md, 4.3.5.md, 4.4.0.md | byte-identical duplicates of the copies already in docs/releases/ |

## Reference fixes
- build.cmd: `tools\build-installer.cmd`, `tools\package-source.ps1` (+ warning text).
- tools/package-source.ps1: root = parent of tools; handoff at docs\history\GLIDE_MANIFESTO_AND_HANDOFF.md; `docs` is no longer stripped from the handover zip (only `screenshots`), so the zip still carries exactly one handoff file.
- tools/build-installer.cmd, tools/run.cmd: working directory resolved to project root.
- README.md (4 release-note links), CHANGELOG.md (release-notes-4.2.2 link), .gitignore (comment), docs/history/GLIDE_MANIFESTO_AND_HANDOFF.md (command list + layout paragraph).

## Deliberately left
- `.artifacts/` (incl. perf-baseline; no notes inside), `artifacts/diagnostic-fixtures` and `artifacts/ffmpeg-pack` (build/packaging outputs), `dist*`, root release zips/exe.
- `tools/` stays flat (build.cmd, tests and AGENTS.md reference several of its files); `tools/benchmark_corpus`, `benchmark_references`, `benchmark_cases.csv` are used by the benchmark scripts.
- `src/*/AvaloniaUI/BuildServices` and `tests/Glide.Core.Tests/AvaloniaUI` (generated, git-ignored XAML compiler marker files, ~120 KB, regenerated by every build).
- No test was deleted: none is provably obsolete.
- (Superseded by the documentation sweep below: the README now uses `docs/screenshots/5.0`.)

## Documentation sweep (release preparation, after the cleanup)

Backups of every edited text file: `Archive/backup-release-final-20261114-223137/` (original relative paths).

Moved (nothing deleted):
| Old | New |
|---|---|
| `Screenshot 5.0.0/` (59 files, project root) | `Archive/screenshots-5.0.0-source/` (every capture kept); the curated set is copied to `docs/screenshots/5.0/` |
| `docs/screenshots/4.6/` (20 files, no longer referenced) | `Archive/screenshots-4.6/` |

Added: `docs/screenshots/5.0/` (hero plus 29 captures), `docs/README.md` (index), `docs/releases/5.0.0-community-feedback.md`, `docs/history/PUBLISH-COMMANDS-5.0.0.md`.

Rewritten or updated: `README.md` (new hero, speed table, v4 vs v5 tables, feature tour, shortcut table), `CHANGELOG.md` [5.0.0] (counts, thanks, registry audit details), `docs/releases/release-notes-5.0.0.md` (the release body), `docs/history/RELEASE-5.0.0-CHECKLIST.md` (publish steps), `docs/GITHUB_ABOUT.txt`, `docs/GITHUB_DESCRIPTION.txt`.

Stale text fixed: `docs/history/GLIDE_MANIFESTO_AND_HANDOFF.md` (settings schema 11, counts, screenshot note), `docs/explorer-thumbnails.md` (status-line wording *pre-5.0*), `codecs/README.md` (removed "Glide 2.2"), `native/Glide.Native/README.md` (current exports, 5.0.0 additions), `src/Glide.Diagnostics/README.md` (removed the 2.9 counts), `src/Glide.Core/Workspace/README.md` (BrowserTabState is the Explorer tab), `src/Glide.Input/README.md` (gesture rows live in Core).

Left alone on purpose: older `docs/releases/release-notes-4.*.md` (historical), `AGENTS.md` (current), `docs/NET10-MIGRATION-PLAN.md`, `docs/ACTIONS-AND-HOTKEYS.md`, `docs/NAVIGATION-HISTORY.md`, `docs/code-signing.md`, `docs/format-coverage.md`, `docs/print-research.md` (dated evidence), `src/Glide.Imaging/README.md`, `installer/Glide.iss`. No document was old enough to move to a legacy folder.
