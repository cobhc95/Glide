"""Counts direct settings-bag reads in MainWindow partials (Stage C, C2 progress metric).

Usage: python tools/count-settings-reads.py [--files]

A "read" is `_settings.X` not followed by an assignment (=, +=, -=, ++, --). Slice reads are `<Feature>OptionsView.X`
(read-only views from src/Glide.Core/Settings/SettingsOptionSlices.cs). Stage C baseline before the slices: 533 reads (620 `_settings.X`
occurrences); after the first five slices: 315 reads.
"""
import re
import sys
from pathlib import Path

root = Path(__file__).resolve().parent.parent
read_re = re.compile(r'(?<![\w.])_settings\.\w+\b(?!\s*(?:=(?!=)|\+=|-=|\+\+|--))')
any_re = re.compile(r'(?<![\w.])_settings\.\w+')
slice_re = re.compile(r'\b(\w+OptionsView)\.\w+')
rows = []
for f in sorted((root / "src/Glide.App").glob("MainWindow*.cs")):
    text = f.read_text(encoding="utf-8-sig")
    rows.append((f.name, len(read_re.findall(text)), len(any_re.findall(text)), len(slice_re.findall(text))))
reads = sum(r[1] for r in rows)
total = sum(r[2] for r in rows)
slices = sum(r[3] for r in rows)
if "--files" in sys.argv:
    for name, r, a, s in sorted(rows, key=lambda x: -x[1]):
        print(f"{r:5} reads {a:5} total {s:5} slice  {name}")
print(f"MainWindow partials: {reads} direct _settings reads ({total} _settings.X incl. writes); {slices} slice reads.")
