# Development dependencies

The distributed UPM runtime/editor source does not bundle third-party binary dependencies. It uses Unity's own Editor API when imported into the user's licensed Unity installation.

The isolated cloud verification used:
- Microsoft .NET SDK 8.0.425, obtained with the official dotnet-install script from dot.net/builds.dotnet.microsoft.com
- NUnit/NUnitLite 3.14.0 from NuGet, test-only (MIT)
- psd-tools 1.10.9, independent PSD oracle, installed outside this repository (MIT)
- Pillow 11.3.0, independent PSD oracle (HPND)
- available system ImageMagick as independent PSD reader

The package does not redistribute PSD-tool/Pillow/Unity executable code, Photoshop/CSP assets, Substance assets, SDKs, brushes, or proprietary file samples. PSD fixtures are generated from scratch. API/format names describe interoperability, not affiliation or endorsement.

Project distribution/licensing choice remains a user/product decision; no claim of full PSD, CSP, Painter or lilToon product compatibility is implied.
