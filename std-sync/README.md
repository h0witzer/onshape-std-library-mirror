# std-sync

Keeps this custom-feature repo's **Standard Library reference files** (the root-level `*.fs`) in
sync with the public upstream mirror:
[Onshape-Standard-Library-Mirror-2-Electric-Boogaloo](https://github.com/h0witzer/Onshape-Standard-Library-Mirror-2-Electric-Boogaloo).

When the public mirror is bumped to a new Onshape version, run this to pull that version in:

```bash
node std-sync/sync.mjs            # sync to newest upstream version, stage, review
node std-sync/sync.mjs --commit   # ...and commit it for you
node std-sync/sync.mjs <ref>      # sync to a specific upstream commit instead of latest
```

## What it does / doesn't do

- **Pure git.** It adds the public mirror as an `upstream` remote, fetches it, and copies the
  root `*.fs` in. No Onshape login, no API, no credentials — nothing to leak.
- Overwrites the root std `*.fs` and removes any module upstream dropped. **Never touches
  `custom-features/`** or anything else in this repo.
- This is **not** the Onshape fetcher. That tool lives in the public mirror repo (`tools/`) and is
  what actually bumps the mirror from Onshape. This tool only consumes the published result — the
  two are intentionally separate.

## Convention

Root-level `*.fs` = the synced standard-library mirror (managed by this tool). Keep all custom work
under `custom-features/` so a sync never disturbs it.
