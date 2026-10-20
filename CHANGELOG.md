# Changelog

## 0.1.0 - 2026-10-20

Initial Windows x64 release of FileShift.

- Native Explorer **Convert to** submenu with targets shared by the entire selection and filtered by configured engines. Windows 11 exposes it through **Show more options**.
- Batch conversion for up to 100 selected items, with two concurrent jobs, progress, cancellation, persistent history, and numbered outputs to avoid overwriting files.
- WPF interface and system tray status, compact queue rows, drag and drop, output-folder selection, and FileShift branding.
- Image, audio, video, document, spreadsheet, presentation, markup, and ebook conversion through built-in support and optional external engines. ZIP creation groups selected files and folders into one archive.
- Experimental Premiere project rendering through separately licensed Adobe Media Encoder 22.5 or later and user-supplied presets.
- Per-user installer with the .NET runtime included, optional Explorer/sign-in integration, engine detection, and options to obtain FFmpeg, ImageMagick, LibreOffice, Pandoc, Calibre, and Ghostscript.

### Known limitations

- The installer and app are unsigned. Conversion engines are installed separately; automatic engine installation requires internet access and may request administrator access.
- Engine detection checks executables, rather than every codec or export filter. Availability varies by engine build. For example, the ImageMagick build checked during development can read HEIC but cannot write it, although HEIC output is offered when ImageMagick is detected.
- Photoshop and Illustrator exports are flattened; image conversions export the first frame, page, or artboard. PDF/PostScript image input needs Ghostscript and compatible ImageMagick policy.
- Adobe rendering has not been live-validated. It requires exactly one root sequence, linked media, and a matching preset. Close AME before each job; multiple Adobe projects are not automatically batched. Cancelling stops FileShift monitoring; stop the render in AME itself.
- Document fidelity depends on the engine, fonts, and source features. CSV exports one sheet. PDF-to-document conversion uses text reflow; OCR and DRM removal are not included.
- ZIP creation is supported; archive extraction and encrypted archive creation are not included.

See [README.md](README.md) for the format catalog, setup, build instructions, and validation details.
