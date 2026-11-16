"""Check that named settings editors are mapped to searchable catalogue entries and that every
setting sits in exactly one Settings card on the page named by its catalogue category (4.8.0)."""
import re
import xml.etree.ElementTree as ET
from pathlib import Path

root = Path(__file__).resolve().parent.parent
xaml_path = root / "src/Glide.App/Settings/SettingsWindow.axaml"
xaml = xaml_path.read_text(encoding="utf-8")
code = (root / "src/Glide.App/Settings/SettingsWindow.axaml.cs").read_text(encoding="utf-8")
gestures_code = (root / "src/Glide.App/Settings/SettingsWindow.Gestures.cs").read_text(encoding="utf-8")
catalog = (root / "src/Glide.Core/Settings/SettingsCatalog.cs").read_text(encoding="utf-8")
gesture_catalog = (root / "src/Glide.Core/Commands/GestureCatalog.cs").read_text(encoding="utf-8")

# Intentionally excluded from the "every editor is a setting" rule because they are not settings:
#  - SearchBox / SearchCategoryCombo / HotkeyFilterBox only filter what is shown;
#  - FullscreenXCloseCheck is a hidden, disabled legacy control (its value is derived from the
#    fullscreen Close caption button choice);
#  - the card header ToggleButtons (class settingsCardHeader), the page's "Expand all" /
#    "Collapse all" buttons and the "Open" buttons on search results are navigation, not settings
#    (they are Buttons/ToggleButtons, so the editor scan below never sees them).
# Settings that live outside every card, by design (documented exclusion from the card-placement rule):
#  - settings.alwaysExpand: the "Always expand" switch sits in the Settings footer's bottom-left corner
#    (the approved footer design). Search still finds it; "Open"
#    focuses the footer switch.
outside_cards = {"settings.alwaysExpand"}
excluded = {"SearchBox", "SearchCategoryCombo", "HotkeyFilterBox", "FullscreenXCloseCheck"}
editors = set(re.findall(r'<(?:CheckBox|ComboBox|NumericUpDown|TextBox)\b[^>]*x:Name="([^"]+)"', xaml))
mappings = re.findall(r'M\("([^"]+)",\s*(\w+)\)', code)
catalog_entries = re.findall(r'new\("([^"]+)",\s*"([^"]+)"', catalog)
ids = {entry_id for entry_id, _ in catalog_entries}
category_of = dict(catalog_entries)
missing = sorted(editors - {control for _, control in mappings} - excluded)
# Mapped legacy controls that are hidden along with their containing rows and are deliberately not
# catalogued: the removed embedded Explorer (first four) and, since 4.8.0, three settings that had no
# effect (tabs.showBackButton, tabs.showForwardButton, folderNav.explore). Their controls stay hidden
# so saved settings files round-trip.
hidden_ids = {"appearance.explorerTheme", "general.newExplorerTabDefaultDirectory", "general.newExplorerTabLastLocation",
              "tabs.showBackButton", "tabs.showForwardButton", "folderNav.explore"}
missing_ids = sorted({key for key, _ in mappings} - ids - hidden_ids)

# Mouse gesture panel (4.8.0). Its rows are built in code (SettingsWindow.Gestures.cs), not XAML:
#  - every GestureCatalog slot gets a GestureActionPicker registered as _settingControls["gesture.<slotId>"],
#    and SettingsCatalog generates one "gesture.<slotId>" entry per slot (GestureEntries), so they are
#    searchable without being listed here;
#  - the panel's own search box, "Changed only" toggle and the Input / Hold combos of custom gesture
#    rows are intentionally NOT catalogue settings: the search box and toggle only filter the panel,
#    and custom rows are dynamic user data covered by the single "gesture.custom" entry plus one
#    dynamic search card per custom gesture (CustomGestureSearchMatches / BuildCustomGestureSearchCard).
#    They all live under GestureMatrixHost, which the search-coverage test treats as dynamic.
gesture_problems = []
if 'yield return GestureEntry(slot)' not in catalog:
    gesture_problems.append("SettingsCatalog no longer generates one gesture.<slotId> entry per GestureCatalog slot")
if 'yield return new("gesture.custom"' not in catalog:
    gesture_problems.append("SettingsCatalog lost the gesture.custom entry for custom gestures")
if '_settingControls["gesture." + slot.Id] = picker' not in gestures_code:
    gesture_problems.append("BuildGestureMatrix no longer registers each slot picker for Settings search")
if 'new SettingDefinition("gesture." + slot.Id, "Mouse & keyboard"' not in catalog:
    gesture_problems.append('generated gesture.<slotId> entries must use the category "Mouse & keyboard"')
if category_of.get("gesture.custom") != "Mouse & keyboard":
    gesture_problems.append('gesture.custom must use the category "Mouse & keyboard"')
slot_ids = re.findall(r'S\("([^"]+)",\s*GestureAreas\.', gesture_catalog)
behaviours = set(re.findall(r'\["([^"]+)"\]\s*=\s*"', gesture_catalog))
gesture_problems += [f"gesture slot without DefaultBehavior text: {slot}" for slot in slot_ids if slot not in behaviours]
if len(slot_ids) != len(set(slot_ids)):
    gesture_problems.append("duplicate gesture slot ids")

# ---- Card layout (4.8.0): parse the AXAML as XML ------------------------------------------------
X = "{http://schemas.microsoft.com/winfx/2006/xaml}"
C = "{using:Glide.App.Controls}"
A = "{https://github.com/avaloniaui}"
CARD = C + "SettingsCard"
SETTING_IDS = C + "SettingsCard.SettingIds"
card_problems = []
tree = ET.parse(xaml_path)
parent_of = {child: parent for parent in tree.iter() for child in parent}
by_name = {e.get(X + "Name"): e for e in tree.iter() if e.get(X + "Name")}


def ancestors(element):
    while element in parent_of:
        element = parent_of[element]
        yield element


# Page titles come from the navigation rail: Button Tag="<Key>" hosts the title TextBlock, and the
# page panel is "<Key>Panel".
page_titles = {}
rail = by_name.get("NavigationRail")
if rail is None:
    card_problems.append("NavigationRail not found")
else:
    for button in rail:
        key = button.get("Tag")
        texts = [t.get("Text") for t in button.iter(A + "TextBlock") if t.get("Text")]
        if not key or len(texts) != 1:
            card_problems.append(f"navigation button {key!r} must have one title TextBlock")
            continue
        page_titles[key + "Panel"] = texts[0]
if len(page_titles) != 8:
    card_problems.append(f"expected 8 navigation pages, found {len(page_titles)}")
pages = {name: by_name.get(name) for name in page_titles}
for name, element in pages.items():
    if element is None:
        card_problems.append(f"page panel {name} not found")

cards = [e for e in tree.iter(CARD)]
card_ids = [card.get("CardId") for card in cards]
duplicates = sorted({cid for cid in card_ids if card_ids.count(cid) > 1})
if duplicates:
    card_problems.append(f"duplicate card ids: {duplicates}")
card_page = {}
for card in cards:
    cid = card.get("CardId")
    for attribute in ("CardId", "Title", "Subtitle", "Icon"):
        if not (card.get(attribute) or "").strip():
            card_problems.append(f"card {cid!r} has no {attribute}")
    children = list(card)
    if len(children) != 1 or children[0].tag != A + "StackPanel" or "settingsCardBody" not in (children[0].get("Classes") or "").split():
        card_problems.append(f"card {cid!r} must contain exactly one StackPanel with class settingsCardBody")
    parent = parent_of.get(card)
    parent_name = parent.get(X + "Name") if parent is not None else None
    if parent_name not in page_titles:
        card_problems.append(f"card {cid!r} is not a direct child of a page panel (parent {parent_name!r})")
    else:
        card_page[card] = parent_name
    if any(a.tag == CARD for a in ancestors(card)):
        card_problems.append(f"card {cid!r} is nested inside another card")
for name, element in pages.items():
    if element is not None and not any(child.tag == CARD for child in element):
        card_problems.append(f"page {name} has no card")


def hosting_cards(element):
    return [a for a in ancestors(element) if a.tag == CARD]


# Every mapped editor sits inside exactly one card.
placements = {}
for setting_id, control_name in mappings:
    if setting_id in outside_cards:
        continue
    element = by_name.get(control_name)
    if element is None:
        card_problems.append(f"mapped control {control_name} ({setting_id}) not found in the AXAML")
        continue
    hosts = hosting_cards(element)
    if len(hosts) != 1:
        card_problems.append(f"{setting_id} ({control_name}) is inside {len(hosts)} cards")
        continue
    placements.setdefault(setting_id, []).append(hosts[0])
# Action entries without a single editor are anchored with controls:SettingsCard.SettingIds.
for element in tree.iter():
    raw = element.get(SETTING_IDS)
    if not raw:
        continue
    hosts = hosting_cards(element)
    for setting_id in [part.strip() for part in raw.split(",") if part.strip()]:
        if setting_id not in ids:
            card_problems.append(f"SettingIds anchor names an unknown setting: {setting_id}")
        if len(hosts) != 1:
            card_problems.append(f"SettingIds anchor for {setting_id} is inside {len(hosts)} cards")
            continue
        placements.setdefault(setting_id, []).append(hosts[0])

# Data-category tick boxes (5.0.4). Reset Glide and Back up and restore share one data-driven list: their tick boxes are
# built in code by DataCategorySelector from GlideDataCategoryCatalog (no XAML per tick box) and registered in the
# search map by RegisterDataCategoryControls. Each card has one empty XAML host; every "reset.*" / "backup.*" id the
# catalogue names is placed in the card that hosts that host.
category_catalog = (root / "src/Glide.Core/Data/GlideDataCategoryCatalog.cs").read_text(encoding="utf-8")
backup_code = (root / "src/Glide.App/Settings/SettingsWindow.Backup.cs").read_text(encoding="utf-8")
category_rows = re.findall(r'^\s*"(reset\.[A-Za-z]+)",\s*(?:true|false)(?:,\s*"(backup\.[A-Za-z]+)")?', category_catalog, re.M)
dynamic_hosts = {"ResetCategoryHost": [r for r, _ in category_rows], "BackupCategoryHost": [b for _, b in category_rows if b]}
# 9 since 5.0.4: the "Downloaded video codec pack" category was folded into "Image and thumbnail caches".
if len(category_rows) < 9:
    card_problems.append(f"GlideDataCategoryCatalog: expected at least 9 categories, parsed {len(category_rows)}")
if "_settingControls[id] = selector.Checks[category.Id]" not in backup_code:
    card_problems.append("RegisterDataCategoryControls no longer registers the data-category tick boxes for Settings search")
for host_name, host_ids in dynamic_hosts.items():
    host = by_name.get(host_name)
    hosts = hosting_cards(host) if host is not None else []
    if len(hosts) != 1:
        card_problems.append(f"{host_name} must be inside exactly one card")
        continue
    for setting_id in host_ids:
        if setting_id not in ids:
            card_problems.append(f"data category tick box {setting_id} has no SettingsCatalog entry")
        placements.setdefault(setting_id, []).append(hosts[0])

static_ids = sorted(i for i in ids if not i.startswith("gesture.") and i not in outside_cards)
for setting_id in static_ids:
    hosts = placements.get(setting_id, [])
    if len(hosts) != 1:
        card_problems.append(f"{setting_id} is placed {len(hosts)} times (expected exactly one card)")
        continue
    page = card_page.get(hosts[0])
    title = page_titles.get(page)
    if title != category_of.get(setting_id):
        card_problems.append(f"{setting_id}: category {category_of.get(setting_id)!r} but its card "
                             f"{hosts[0].get('CardId')!r} is on page {title!r}")
for setting_id in hidden_ids:
    if len(placements.get(setting_id, [])) != 1:
        card_problems.append(f"hidden legacy control {setting_id} must stay inside exactly one card")
# Generated gesture entries live in the card that hosts the gesture panel.
matrix = by_name.get("GestureMatrixHost")
matrix_cards = hosting_cards(matrix) if matrix is not None else []
if len(matrix_cards) != 1 or matrix_cards[0].get("CardId") != "mouse-gestures":
    card_problems.append("GestureMatrixHost must be inside the mouse-gestures card")
elif page_titles.get(card_page.get(matrix_cards[0])) != "Mouse & keyboard":
    card_problems.append('the mouse-gestures card must be on the "Mouse & keyboard" page')

if missing or missing_ids or gesture_problems or card_problems:
    print("Unmapped settings controls:", missing)
    print("Mapped IDs missing from catalog:", missing_ids)
    print("Gesture panel problems:", gesture_problems)
    print("Card layout problems:")
    for problem in card_problems:
        print("  -", problem)
    raise SystemExit(1)
print(f"Settings search audit passed: {len(editors - excluded)} editors mapped; {len(static_ids)} settings placed in "
      f"{len(cards)} cards on {len(page_titles)} pages; {len(slot_ids)} gesture rows generated (+ custom gestures).")
