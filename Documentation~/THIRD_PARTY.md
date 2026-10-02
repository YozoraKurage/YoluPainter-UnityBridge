# Development dependencies

The distributed UPM runtime/editor source does not bundle third-party binary dependencies. It uses Unity's own Editor API when imported into the user's licensed Unity installation.

Development-time verification runs inside Unity itself (EditMode tests). The Codex handoff environment additionally used the .NET 8 SDK, NUnit/NUnitLite 3.14.0 (MIT), psd-tools 1.10.9 (MIT), Pillow 11.3.0 (HPND) and ImageMagick as independent PSD oracles; those harnesses were removed from this repository (history: commit ca64f42). None of them is shipped or required at runtime.

The devcontainer's Unity test project also has lilToon 2.3.4 installed through VPM for future adapter work. The package does not depend on, bundle or modify lilToon.

The package does not redistribute PSD-tool/Pillow/Unity executable code, Photoshop/CSP assets, Substance assets, SDKs, brushes, or proprietary file samples. PSD fixtures are generated from scratch. API/format names describe interoperability, not affiliation or endorsement.

Project distribution/licensing choice remains a user/product decision; no claim of full PSD, CSP, Painter or lilToon product compatibility is implied.
