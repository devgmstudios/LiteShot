# LiteShot

A remake of the Lightshot screenshot tool for Windows by **Devgm, LLC**, with the same two-click workflow, more tools, AI editing and a glass interface. Free and open source under the [MIT license](LICENSE).

Website: [liteshot.app](https://liteshot.app) · Encrypted uploads: [fortefile.com](https://fortefile.com)

Press **PrtScn**, drag over an area (or click a window), annotate, then copy, save, upload, pin, pull out the text, edit it with AI or turn it into a video.

## Build and install

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

This produces, in `dist\`:

- `LiteShot-Setup-<version>.msi`: the installer. It installs for the current user without administrator rights (to `%LocalAppData%\Programs\LiteShot`), adds a Start menu shortcut, starts LiteShot with Windows, closes a running copy before upgrading, and is listed under Devgm, LLC in Apps & features.
- `LiteShot.exe`: the same app as a portable, self-contained exe; no .NET install needed.

Neither is code-signed yet, so Windows SmartScreen will warn that the publisher is unknown. With a code-signing certificate in your certificate store, `build.ps1 -CertThumbprint <sha1>` signs both. Launching LiteShot again while it runs starts a capture, like Lightshot; `LiteShot.exe --exit` closes the running copy.

## What carries over from Lightshot

- PrtScn opens a frozen, dimmed copy of every monitor; drag to select, resize with handles, drag inside to move
- Size label at the top-left; tools to the right of the selection, actions below it
- Pen, line, arrow, rectangle, marker, text, color, undo
- Upload with the link copied, share to social networks, search similar images, print, copy, save
- Tray icon: left-click captures, right-click for the menu; Shift+PrtScn saves the full screen instantly

## What's better

| | |
|---|---|
| More tools | Ellipse, numbered steps (1, 2, 3…), pixelate for redaction, redo |
| Blur names automatically | Turn it on under **Settings › Capture**: text that looks like a person's name is blurred as soon as you capture, using the OCR built into Windows, so nothing leaves your PC. Ctrl+Z brings the names back; click a blur and press Delete to remove just one |
| Blur faces | The face button next to Pixelate (or **F**) finds every face in the selection with the face detector built into Windows and blurs each one |
| Editable drawings | With no tool selected, click a drawing to select it, drag to move, Delete to remove, change its color or size |
| Precision | Pixel loupe with coordinates and hex color (turn it on under **Settings › Capture**; press **C** to copy the color); arrow keys nudge, Shift+arrows resize, Shift draws straight lines and squares |
| Window capture | Hover a window and click once to capture exactly that window |
| Copy text (OCR) | Uses the OCR engine built into Windows, so it works offline |
| Pin to screen | Floats a screenshot above all windows: drag it, scroll to zoom, Ctrl+scroll for opacity |
| Last area | Ctrl+PrtScn reopens the editor on the area you used last |
| Encrypted links that delete themselves | Uploads go to fortefile.com, Devgm's own host. Each screenshot is encrypted on your PC (AES-256-GCM) and the key lives only after the `#` in the link, which browsers never send to a server, so fortefile can't see your files. Links delete themselves after 24 hours (or 1 or 12, in Settings), and **Recent uploads › Delete now** in the tray menu removes one sooner. Search similar images is the exception: Google has to see the picture, so that upload is plain and lasts an hour |
| Text styling | A style bar above the text: 10 built-in fonts (6 bundled, open-licensed), every installed font, or add your own .ttf/.otf; size −/+ or pick from a list; bold, italic; left, center, right; plain, outline, label or shadow; drag the frame to move it |
| Edit with AI | Describe a change ("remove the background") and get the edited screenshot back in a window where you can compare, refine, and keep every version. Uses FLUX Kontext Pro, Nano Banana or any Replicate model, through your SyncNode key |
| Make a video | Turns the screenshot into a 5 or 10 second clip (Seedance, Kling, Wan or any Replicate model). It's saved to your folder and copied, ready to paste |
| HiDPI and multi-monitor | Pixel-exact on every monitor, with the controls scaled to each monitor's DPI |
| Keyboard | Every tool and action has a shortcut (see below) |

## Glass design

The interface follows the Liquid Glass guidance, translated to Windows:

- **Glass only on controls.** Toolbars, popovers, labels and the loupe are glass. The screenshot and your drawings never are.
- **A real material.** Each panel blurs and boosts the color of what's behind it and magnifies it slightly like a lens, with a soft top glow and an edge highlight.
- **Adapts to what's behind it.** Over bright content the glass turns light with dark icons; over dark content it turns smoky with white icons. Each panel decides on its own.
- **Grouped controls.** Related buttons share one capsule, groups are separated by space instead of divider lines, and the round hover and selected states are concentric with their capsule.
- **System colors.** The selected tool and toggles use your Windows accent color.
- **Accessibility.** With *Transparency effects* off, the glass turns frosted and nearly opaque. *High contrast* switches to solid system colors. With *Animation effects* off, motion is disabled.
- **Native windows.** Settings and menus use the Windows 11 look: Mica title bar, rounded corners, grouped cards, toggles and segmented controls, in light or dark to match the system.

## Shortcuts

| In the editor | |
|---|---|
| Copy and close | Ctrl+C, Enter, or double-click inside the selection |
| Save | Ctrl+S |
| Upload and copy the link | Ctrl+D |
| Search similar images | Ctrl+K |
| Print | Ctrl+P |
| Pin to screen | Ctrl+T |
| Copy text (OCR) | Ctrl+Shift+C |
| Text: bold, italic, align, size | Ctrl+B, Ctrl+I, Ctrl+L / E / R, Ctrl+[ and Ctrl+] |
| Undo, redo | Ctrl+Z, Ctrl+Y |
| Pen, line, arrow, rectangle, ellipse, marker, text, number, pixelate | P L A R E M T N B (press again to deselect) |
| Blur faces | F |
| Stroke or font size | Mouse wheel |
| Select a whole monitor | Ctrl+A |
| Cancel | Esc or right-click |

The global shortcuts (PrtScn, Shift+PrtScn, Ctrl+PrtScn) can be changed under **Settings › Shortcuts**. While LiteShot runs it takes PrtScn over from the Snipping Tool.

## AI setup (SyncNode)

1. Create a SyncNode account at syncnode.ai and copy your API key from `/api_keys`.
2. On the same page, save your Replicate token under **Connected Provider Credentials**.
3. In LiteShot, open **Settings › Cloud & AI**, paste the key and press **Check**; it shows your balance.

Each run costs $0.02 on SyncNode plus the model's own price on Replicate. The screenshot travels inside the request as an image; it is never posted to a public host. The key is stored encrypted for your Windows account (DPAPI).

SyncNode has no general file hosting, so uploads go to fortefile.com instead (see below).

## Updates

Under **Settings › General**, **Check now** looks for a new version, and **Update automatically** (off by default) checks every few hours, downloads in the background and installs once LiteShot is idle (nothing open, no upload running). Installed copies update with the new MSI; the portable exe replaces itself and restarts.

Each release is signed with Devgm's private key, and the app only installs a release whose signature matches the public key built into it (`src/LiteShot/update-public-key.pem`) and whose download matches the signed SHA-256. A compromised server can't push code.

### The update server

`server/` is a Cloudflare Worker that stores releases in R2. To set it up once, run these in `server/`:

```bash
npm install
```

```bash
npx wrangler login
```

```bash
npx wrangler r2 bucket create liteshot-releases
```

```bash
npx wrangler secret put PUBLISH_TOKEN
```

```bash
npm run deploy
```

Put the Worker's URL in `<UpdateServer>` in `src/LiteShot/LiteShot.csproj` (or pass `-UpdateServer <url>` to `build.ps1`), then rebuild. Builds without a URL say "Updates aren't set up in this build yet."

### Publishing a release

Raise `<Version>` in `LiteShot.csproj`, run `build.ps1`, then upload and sign both files:

```bash
dotnet run --project tools/LiteShot.Publish -c Release -- publish --server https://<worker-url> --token <PUBLISH_TOKEN> --notes "What's new"
```

`verify --server <url> --download` checks what the server is handing out. The signing key lives at `%APPDATA%\Devgm\LiteShot Publishing\update-signing-key.pem`. **Back it up**: without it, existing installs can never receive another update.

## The website

`server/public/` is the LiteShot site, served by the same Worker as updates at https://liteshot.app (also https://liteshot-updates.tight-dawn-4128.workers.dev, which LiteShot uses for update checks). Its download buttons, `/download` and `/download/portable`, always point at the newest signed release. The product shots come from the harness: `dotnet run --project tests/LiteShot.Harness -- <folder> --site`.

## fortefile.com

`fortefile/` is the upload host: a Cloudflare Worker that stores encrypted files in R2, deletes them when they expire (an hourly job plus a 1-day R2 lifecycle rule), and serves the landing and viewer pages. The viewer decrypts in the browser with the key from the link.

## Project layout

```
src/LiteShot/
  Overlay/        capture surface, glass material, toolbars, text style bar, AI prompt, annotation shapes
  Services/       saving, clipboard, printing, uploads, OCR, SyncNode and AI
  UI/             settings, AI result and progress windows, pinned screenshots, menus
  Fonts/          bundled fonts (OFL / Apache licenses included)
  TrayApp.cs      tray icon, hotkeys, results
installer/                WiX installer (per-user MSI)
server/                   update server and website (Cloudflare Worker + R2)
fortefile/                fortefile.com: encrypted, self-deleting uploads (Cloudflare Worker + R2)
tests/LiteShot.Harness/   drives the real overlay offscreen and renders each state to PNG
tools/                    regenerate the icon (MakeIcon.ps1) and installer artwork (MakeInstallerArt.ps1),
                          and publish signed releases (LiteShot.Publish)
```

Run the harness with `dotnet run --project tests/LiteShot.Harness -- <output folder>`. It checks selection, every tool, undo and redo, moving drawings, the palette, the final image, OCR, saving, text styling and fonts, the AI popover and windows, SyncNode error handling, and name and face blurring, and writes a PNG of each state. With the local update server running (`npm run dev` in `server/`), add `--update-server http://127.0.0.1:8788` to also test signed updates, tampered releases and the exe swap. `--icons` renders every icon on one sheet.

Settings are stored in `%APPDATA%\LiteShot\settings.json`.

## Contributing

Issues and pull requests are welcome. Build with `build.ps1` (or `dotnet build`), and run the harness before sending a change; it renders every state to PNG so UI changes are easy to review.

## License

LiteShot is released under the [MIT license](LICENSE), © 2026 Devgm, LLC. The bundled fonts keep their own licenses (SIL Open Font License or Apache 2.0), included in `src/LiteShot/Fonts`. LiteShot is an independent project and isn't affiliated with Lightshot or Skillbrains.
