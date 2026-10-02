# Development dependencies

The distributed UPM runtime/editor source does not bundle third-party binary dependencies. It uses Unity's own Editor API when imported into the user's licensed Unity installation.

Development-time verification runs inside Unity itself (EditMode tests). The Codex handoff environment additionally used the .NET 8 SDK, NUnit/NUnitLite 3.14.0 (MIT), psd-tools 1.10.9 (MIT), Pillow 11.3.0 (HPND) and ImageMagick as independent PSD oracles; those harnesses were removed from this repository (history: commit ca64f42). None of them is shipped or required at runtime.

The devcontainer's Unity test project also has lilToon 2.3.4 installed through VPM for future adapter work. The package does not depend on, bundle or modify lilToon.

The package does not redistribute PSD-tool/Pillow/Unity executable code, Photoshop/CSP assets, Substance assets, SDKs, or proprietary file samples. PSD and brush-file fixtures are generated from scratch in the tests. API/format names describe interoperability, not affiliation or endorsement.

## Bundled brush tips

`BrushSets~/Krita4Default/brushes/` holds the 76 raster brush tips (`.png`, `.gih`, `.gbr`) of Krita's
`Krita_4_Default_Resources.bundle`, unchanged. The bundle declares the licence **CC0 1.0** in its `meta.xml`, which is
kept verbatim next to the tips together with `SHA256SUMS` and a `README.md` giving the source URL and the bundle's
SHA-256. Credit (not required by CC0): David Revoy (Deevad), with derivations of brushes by Ramon Miranda, Razvanc,
Radian, Wolthera, Storm, Scottyp and others, for the Krita project. Krita's paint-op presets (`.kpp`), SVG tips and
patterns are not included, and YoluPainter's settings for these tips are its own.

## Brush file formats

The brush importers are written from published format descriptions; no importer code from other projects is copied:

- GIMP `.gbr` / `.gih` / `.vbr`: the format notes in GIMP's `devel-docs` (`gbr.txt`, `gih.txt`, `vbr.txt`).
- Photoshop `.abr`: Adobe's Photoshop File Formats Specification (version 1/2 brushes and the ActionDescriptor
  structure) and public community write-ups of the version 6+ `samp` / `desc` layout. Brush settings this engine does not
  have are reported per brush.
- CLIP STUDIO PAINT `.sut` is refused: its tip images are stored in a protected container, and decoding it is not done.

Brushes a user imports are copied into the Unity project's `UserSettings/YoluPainter/Brushes/` (per user, not in
`Assets`, not in the package) and are never redistributed by this package.

Project distribution/licensing choice remains a user/product decision; no claim of full PSD, CSP, Painter or lilToon product compatibility is implied.
