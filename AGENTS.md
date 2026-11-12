# Project instructions

- Every new user-facing setting or configurable feature must be discoverable through Settings search. Add its metadata to `SettingsCatalog` and connect its control in `BuildSettingControlMap`; include useful search terms and the correct category. Run `python tools/audit-settings-search.py` when changing settings. If a control is intentionally excluded, document why in that audit.
