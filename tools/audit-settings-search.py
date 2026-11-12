"""Check that named settings editors are mapped to searchable catalogue entries."""
import re
from pathlib import Path

root = Path(__file__).resolve().parent.parent
xaml = (root / "src/Glide.App/Settings/SettingsWindow.axaml").read_text(encoding="utf-8")
code = (root / "src/Glide.App/Settings/SettingsWindow.axaml.cs").read_text(encoding="utf-8")
catalog = (root / "src/Glide.Core/Settings/SettingsCatalog.cs").read_text(encoding="utf-8")
# Search/filter controls are not settings; mirror controls are indexed through their primary setting.
excluded = {"SearchBox", "SearchCategoryCombo", "HotkeyFilterBox", "StatusVisibleMirrorCheck", "StatusHoverMirrorCheck", "FullscreenXCloseCheck"}
editors = set(re.findall(r'<(?:CheckBox|ComboBox|NumericUpDown|TextBox)\b[^>]*x:Name="([^"]+)"', xaml))
mappings = re.findall(r'M\("([^"]+)",\s*(\w+)\)', code)
ids = set(re.findall(r'new\("([^"]+)",', catalog))
missing = sorted(editors - {control for _, control in mappings} - excluded)
 # These mapped legacy controls are hidden along with their containing rows.
hidden_ids = {"appearance.explorerTheme", "general.newExplorerTabDefaultDirectory", "general.newExplorerTabLastLocation", "tabs.navigateToFolderBehavior"}
missing_ids = sorted({key for key, _ in mappings} - ids - hidden_ids)
if missing or missing_ids:
    print("Unmapped settings controls:", missing)
    print("Mapped IDs missing from catalog:", missing_ids)
    raise SystemExit(1)
print(f"Settings search audit passed: {len(editors - excluded)} editors mapped.")
