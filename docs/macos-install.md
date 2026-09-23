# Installing My Maps Generator on a Mac

Works on Macs with Apple Silicon (M1, M2, M3, M4 — any Mac from late 2020 on). To check:
Apple menu  → **About This Mac** → "Chip" says *Apple M…*.

The app is free and isn't registered with Apple (that costs $99/year), so macOS asks you to
confirm it once. Pick **one** of the two ways below.

## Option A — one command (easiest, no warnings)

1. Open **Terminal** (press ⌘ Space, type `Terminal`, press Return).
2. Paste this line and press Return:

   ```
   curl -fsSL https://raw.githubusercontent.com/BenDayan123/map-generator/main/build/install-macos.sh | bash
   ```

3. It downloads the latest version, puts it in **Applications**, and opens it. Done.

Run the same line again any time to reinstall.

## Option B — download the .dmg

1. Download **MyMapsGenerator-osx-arm64.dmg** from
   https://github.com/BenDayan123/map-generator/releases/latest
2. Double-click the `.dmg`, then drag **My Maps Generator** onto the **Applications** folder.
3. Open **Applications** and double-click **My Maps Generator**.
   macOS says *"Apple could not verify 'My Maps Generator' is free of malware…"* → click **Done**.
4. Open **System Settings** → **Privacy & Security**, scroll down to the message
   *"'My Maps Generator' was blocked…"* → click **Open Anyway** → enter your Mac password →
   click **Open Anyway** again.
5. The app opens. From now on it opens normally with a double-click.

## First-time setup inside the app

1. Go to **Settings** and fill in your API keys (or drop your one-file setup `.json`).
2. To publish maps to Google My Maps you need **Google Chrome** (or Microsoft Edge)
   installed — the app signs in to Google through it. The first publish may download a
   browser component (~150 MB), so give it a minute.

## Updates

Settings → **Check for updates** → **Install**. The app closes, updates itself, and
reopens — no dragging needed.

## If something goes wrong

- *"is damaged and can't be opened"*: you have an old download. Delete it and use Option A.
- The app closes right away: send the file
  `~/Library/Application Support/GmapPlanner/last_error.log`
  (Finder → Go → Go to Folder…, paste the path).
