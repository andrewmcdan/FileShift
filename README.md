# FileShift

A Windows tray app for local file conversion and ZIP creation, built with .NET 10, WPF, and external conversion engines. The installer includes the .NET runtime.

## Install and use

Download `FileShiftSetup.exe` from [GitHub Releases](https://github.com/andrewmcdan/FileShift/releases/latest) (repository access required), or build it locally as described below. Choose Explorer integration, optional sign-in startup, and whichever conversion engines you want. Existing engines are detected. Review the linked licenses before selecting automatic engine installation.

The initial release is Windows x64 and unsigned. See the [changelog](CHANGELOG.md) for release notes and known limitations.

1. Select files in Explorer and open **Convert to**. On Windows 11 this is under **Show more options**.
2. Choose **PNG image**, **MP3 audio**, **PDF document**, or another available target to queue the conversion immediately. Targets must work for every selected item and have an installed/configured engine. Same-format targets are omitted. Outputs are saved beside the originals, independently of the app's format-picker output folder.
3. Choose **ZIP archive** to put the entire selection into one archive, including selected folders. Folders and arbitrary file types support ZIP even when they have no conversion route. Choose **Open FileShift...** to use the format picker, choose a different output folder, or install additional engines.
4. Click the tray icon to see running jobs, progress, failures, and completed outputs. Closing the window keeps the app running. The tray menu's **Exit FileShift** stops the app.

You can also add files in the app or drag them into its window. The current batch limit is 100 selected items; a ZIP folder can contain up to 100,000 entries. Mixed file types offer targets common to the entire selection. Convert incompatible groups separately.

Outputs default to the source folder. Originals remain unchanged, and filename collisions receive numbered names. Two jobs run at once; FFmpeg gets two encoder threads per job. Cancelling external CLI jobs terminates their process trees. History is saved locally and unfinished jobs are marked **Interrupted** after a restart, ready for manual retry.

## Formats and engines

Routes are explicit, logical input/output pairs. Presence of an engine is not a guarantee that its particular build includes every codec, delegate, font, or export filter.

| Area | Representative inputs | Outputs / engine |
| --- | --- | --- |
| Built-in images | PNG, JPEG, BMP, GIF, TIFF, ICO | PNG, JPEG, BMP, GIF, TIFF; Windows image decoder |
| Additional images | WebP, HEIC/HEIF, AVIF, SVG, PSD/PSB, AI/EPS, JPEG 2000, JXL, PNM, TGA, DDS, EXR, HDR, camera RAW | PNG, JPEG, WebP, AVIF, HEIC, BMP, GIF, TIFF, ICO, PDF; ImageMagick |
| Audio | WAV, MP3, FLAC, AAC, M4A, OGG, Opus, WMA, AIFF, APE and others | MP3, WAV, FLAC, M4A, OGG, Opus, AAC, AIFF; FFmpeg |
| Video | MP4, MKV, MOV, AVI, WebM, WMV, MPEG, TS, FLV and others | MP4, MKV, WebM, MOV, AVI, GIF, or audio extraction; FFmpeg |
| Office documents | DOC/DOCX, ODT, RTF, TXT, HTML, WPS | PDF, DOCX, ODT, RTF, TXT; LibreOffice |
| Spreadsheets | XLS/XLSX, XLSM/XLSB, ODS, CSV/TSV | PDF, XLSX, ODS, CSV; LibreOffice |
| Presentations | PPT/PPTX, PPTM, PPS/PPSX, ODP | PDF, PPTX, ODP; LibreOffice |
| Drawing / publishing | ODG, VSD/VSDX, PUB | PDF; LibreOffice, subject to import support |
| Markup / structured documents | Markdown, HTML, DOCX, ODT, EPUB, RST, LaTeX, Org, Jupyter | Markdown, HTML, DOCX, ODT, EPUB, RST, LaTeX, Org, TXT; Pandoc. Markup PDF uses Pandoc → DOCX → LibreOffice |
| Ebooks / document reflow | EPUB, MOBI, AZW/AZW3, FB2, CHM, DJVU, comics, DOCX, ODT, RTF, TXT, HTML, PDF | EPUB, MOBI, AZW3, FB2, DOCX, PDF, TXT, RTF; Calibre |
| Premiere projects | PRPROJ with one root sequence | MP4, MOV, MP3, WAV; experimental Adobe Media Encoder adapter |
| Archives | Any regular files and folders | One ZIP containing the selection; built in |

Image exports flatten Photoshop layers and Illustrator artwork, and export the first frame/page/artboard. They do not preserve an editable Adobe document, all animation frames, or all PDF pages. Illustrator/PostScript/PDF image input needs Ghostscript and permission in ImageMagick's policy; the app does not loosen engine policies. Camera RAW and newer image codecs depend on the installed ImageMagick build. The built-in image path exports a rendered bitmap and does not preserve original metadata or color profiles. CSV exports one sheet and loses spreadsheet styling. Document layouts can change when fonts or layout features differ. Calibre's PDF input is text reflow, not a faithful PDF-to-Word layout reconstruction. There is no OCR, DRM removal, encrypted archive creation, or arbitrary executable conversion.

## Installer engine options

The app is bundled for offline installation. Optional engines are obtained online, installed as separate shared applications, and retained when FileShift is uninstalled:

- FFmpeg: `Gyan.FFmpeg` from winget.
- LibreOffice: `TheDocumentFoundation.LibreOffice` from winget.
- ImageMagick: `ImageMagick.ImageMagick` from winget.
- Pandoc: `JohnMacFarlane.Pandoc` from winget.
- Calibre: `calibre.calibre` from winget.
- Ghostscript: Artifex's official GitHub release. Setup verifies the published SHA-256 digest and opens the vendor installer. Ghostscript is not currently in the winget repository.

Packages may require an administrator prompt. If winget is unavailable or a package fails, setup preserves the installed app, reports which engines need attention, and offers official manual download links. It does not report an engine ready merely because a package command exited successfully: it checks for its executable afterwards.

Use **Engines & setup** to rescan, override paths, or rerun the installer. Detection checks portable `tools/<engine name>/` folders next to the app, common install locations, PATH, and winget links. An invalid explicit override remains unavailable until corrected. User data and setup logs are in `%LOCALAPPDATA%\FileShift`.

## Premiere / Media Encoder (experimental)

This integration has not been live-validated here. Adobe apps are licensed separately and are not downloaded by FileShift. Their folders on this machine contain no usable modern AME executable.

1. Install Adobe Media Encoder **22.5 or later**, with Premiere/Dynamic Link compatible with the project.
2. Export an AME `.epr` preset for each desired target and enter its absolute path in **Engines & setup**. The preset's codec/container must match the target extension.
3. Prepare a project with **exactly one root sequence**, linked media online, and required fonts/effects/plugins available.
4. Close AME before starting the render in FileShift. The adapter launches AME with an ExtendScript request and monitors encode-progress / encode-finished events through job-local status files. AME remains open after rendering; close it before another project render. Multiple projects are not currently automated as an Adobe batch.

Multi-sequence projects fail with a clear message rather than picking an arbitrary sequence. Cancelling in FileShift stops monitoring; stop the actual render in AME. FileShift never kills an interactive Adobe process or stops unrelated Adobe jobs. Unfinished Adobe working files are retained under `%LOCALAPPDATA%\FileShift\AdobeJobs`. Newer AME scripting versions may need adapter updates; this is not a production-certified Adobe integration.

References: [Adobe's Premiere encoder API](https://developer.adobe.com/premiere-pro/uxp/ppro-reference/classes/encodermanager/), [Adobe employee guidance on AME command-line scripting](https://community.adobe.com/questions-506/unable-to-run-jsx-from-command-line-for-media-encoder-996123), [AME scripting reference linked by Adobe](https://ame-scripting.docsforadobe.dev/reference/).

## Explorer submenu and latency

The submenu uses a small native x64 `IExplorerCommand` DLL with a statically linked C++ runtime. The managed app and conversion engines are never loaded into Explorer. Menu construction consumes one cached registry capability snapshot and checks shell-supplied names/attributes for the selection. Each selection is inspected once and its target intersection is shared by the child commands. It does not read file contents, search for engines, start processes, or contact the app while displaying the menu. There is no menu icon resource lookup.

The app/installer generates the cached target table after engine discovery. App startup, opening **Engines & setup**, saving paths/rescanning, and completing engine installation refresh the table. A removed engine or stale menu is checked again when a request reaches the app. Adobe targets require AME 22.5+ and an existing preset for that target. Image/PDF targets retain the format limitations described above; the menu does not decode content to discover codec, audio-track, encryption, or corruption issues.

The handler passes the entire selection in one bounded, user-local JSON request only after a command is chosen, avoiding per-file process launches and command-line length limits. The app uses its existing single-instance IPC and conversion queue. Requests are validated and consumed; a ZIP request produces one archive rather than a ZIP per item. Selection is limited to 100 filesystem items.

This design minimizes work on Explorer's menu path; it is not a zero-latency guarantee. The DLL must load on first use. A Windows 11 top-level entry would additionally require package identity and is not included. Windows' built-in AQS static filters only evaluated the first selected file in integration testing, so they were insufficient for mixed-batch target selection.

Development-machine verification: actual Windows shell menus were checked for images, video, folders with misleading extensions, and mixed selections. Invoking **PNG image** converted 20 selected JPEGs with output encoding readback and preserved originals; invoking **ZIP archive** created one archive containing two files and an empty folder. A 100-file handler benchmark measured first activation plus target-state evaluation at 6.48 ms, with warm median 1.35 ms and 95th percentile 3.11 ms across 29 warm samples. These timings cover FileShift's handler, not total Explorer/menu latency, and vary by machine and shell provider.

References: [Microsoft static menu guidance](https://learn.microsoft.com/en-us/windows/win32/shell/creating-static-cascading-menus), [selection model limits](https://learn.microsoft.com/en-us/windows/win32/shell/how-to-employ-the-verb-selection-model), [Windows 11 integration](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/integrate-packaged-app-with-file-explorer).

## Build and verify

The root `logo.png` is the branding source. `scripts/Update-Branding.ps1` creates a centered, tightly framed mark and a Windows icon with 16–256 pixel frames in `assets/`, leaving the source unchanged. The app sidebar, window/taskbar, system tray, installer, Start menu shortcut, and Windows uninstall entry use this branding. The release build regenerates these assets automatically; run the script after changing the logo when building directly with `dotnet`.

Requires Windows x64, the .NET 10 SDK, and Visual Studio C++ Build Tools (x64 tools and Windows SDK) for the native submenu component. There are no third-party NuGet dependencies in the application or test harness.

```powershell
dotnet run --project tests/FileShift.Tests -c Release
.\scripts\Test-Shell.ps1
.\scripts\Build.ps1
```

The build publishes a self-contained app, ZIPs the payload, and embeds it in a self-contained `artifacts/release/FileShiftSetup.exe`. Conversion engines are not redistributed inside this installer. The resulting development binaries are unsigned; release distribution should add code signing and broader OS/codec testing.

For an app-only installer smoke test (no engine downloads or startup entry):

```powershell
Start-Process .\artifacts\release\FileShiftSetup.exe -ArgumentList '--install-app-only' -Wait
```

This registers the Explorer verb by default; add `--no-explorer` to omit it. Uninstall from Windows Apps & features or run `%LOCALAPPDATA%\FileShift\FileShiftSetup.exe --uninstall`. Uninstall keeps converted outputs, shared engines, settings/history, and the maintenance installer. App replacement stages the payload first and preserves a previous recognized app directory if replacement fails.

Windows can keep the native shell DLL loaded after an upgrade or uninstall. Setup retains any locked old component in an owned backup/removal directory instead of restarting Explorer. Rerun setup after signing out to clean up that directory.

Tests include actual image readback, concurrent output collisions, a 20-file batch, subprocess argument quoting, bounded dual-stream logs, cancellation/timeouts, ZIP entry/content verification, history recovery, engine-aware menu snapshots, request validation, and single-archive batch handoff. Native tests cover all-selection target intersection, folder/unknown-file handling, selection caching, batch limits, JSON escaping, and COM enumeration/lifetime. `tests/FileShift.Shell.Tests/ExplorerProbe.cpp` exercises actual Windows menu construction and can invoke a selected submenu command for end-to-end checks. Live FFmpeg, ImageMagick, LibreOffice and Pandoc tests run when those engines are installed; missing engines are explicitly reported as skipped.
