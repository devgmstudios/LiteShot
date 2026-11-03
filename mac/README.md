# LiteShot for Mac

A native Swift version of LiteShot: a menu bar app for macOS 14 and later. Early days; it shares the Windows
app's design but none of its code.

## What it does so far

- **⌘⇧9** (or Capture Area in the menu bar) freezes every screen. Hover a window and click to capture it, or drag an area.
- Pen, line, arrow, box, circle, marker, text, numbered steps and pixelate, with colors and sizes. Shift keeps lines at 45° and boxes square.
- **Hide personal info (H):** emails, phone, card and social security numbers, your keywords and faces are pixelated, found with Apple's on-device Vision.
- **Blur faces (F)**, **Copy text (⇧⌘C)**, **Copy (⌘C)**, **Save (⌘S)**, **Pin to screen (⌘T)**, undo and redo.
- **Bright mode** and **Button names** in the menu bar menu: solid black toolbars, bigger icons, yellow for what's turned on, and a name on every button.

Not yet: uploads to fortefile.com, Edit with AI, scrolling capture, automatic updates.

## Building

On a Mac with Xcode 16 or later:

```sh
cd mac
swift test                 # the core: geometry, personal-info rules, rendering
./scripts/bundle.sh        # dist/LiteShot.app and dist/LiteShot-mac-<version>.zip
./dist/LiteShot.app/Contents/MacOS/LiteShot --render-shots shots   # the UI drawn to PNGs, with layout checks
```

GitHub Actions does all three on every push that touches `mac/` (`.github/workflows/mac.yml`), and attaches the
zipped app and the UI pictures to the run.

## First run

The app is signed ad hoc until it has a Developer ID, so macOS asks before opening it the first time: open it,
then choose **System Settings › Privacy & Security › Open Anyway**. LiteShot then asks for **Screen & System Audio
Recording**, which it needs to see the screen. Nothing it captures leaves the Mac unless you share it.
