# Validation checkpoint — 2026-10-02

Target Unity **2022.3 confirmed by the user**. Exact patch/OS/pipeline/tablet/lilToon remain unknown. The cloud environment has Microsoft .NET SDK 8.0.425, Python and independent PSD readers. It does **not** have an activated Unity Editor.

## Executed

| Stage | Result | Evidence |
|---|---|---|
| Actual pure-C# core compile/run, .NET8/C#8 | PASS | validation/core-integration.log |
| Actual core .NET Standard 2.1 compile | PASS, 0 warnings, 0 errors | validation/netstandard-compile.log |
| NUnit production core + PSD | 59/59 PASS (31 core +28 PSD; includes 500 mutation inputs) | validation/core-psd-nunit.xml / tools/CoreNUnit/TestResult.xml |
| Integrated draw/undo/native save/PSD scenarios | 8/8 PASS | validation/core-integration.log |
| Same production geometry via math adapters | 19/19 PASS | validation/geometry-adapter.log |
| All Unity-facing C# syntax, C#8 | PASS, 0 syntax errors | validation/csharp-syntax.log |
| Independent raw/RLE PSD → actual C# import/rewrite → reimport | PASS; retained supported raster bytes | validation/psd-spike/report.json |
| C# PSD outputs → independent byte parser, Pillow, psd-tools, ImageMagick | PASS for fixture metadata/layer pixels | validation/psd-spike/report.json |
| Forced independent recomposition, sample-space fixture | maximum error 1 byte | validation/psd-spike/report.json |
| Unsupported PSD original preservation + guarded-edit refusal | PASS, original byte identity | PSD suite and fixture report |

The integrated save scenarios inject failure after individual file write, after verification, after generation rename, and before current-pointer replacement; the old current remains valid. A separate test injects a lost acknowledgement after current-pointer replacement and verifies the new current is intact. Stale-token saves and modified generation bytes are rejected. These are real filesystem/.NET tests, not OS power-loss tests.

## Not executed / not established

- Actual Unity API type resolution, package import, asmdef/testable import and Unity EditMode run
- Shader compilation and GPU paint/compositor parity, texture orientation, linear/gamma project display, preview render and pipeline behavior
- Tablet hardware, latency, HiDPI, multi-monitor, mouse/pen suppression, pressure quality
- Real Unity lifecycle/reload/Play/close/source asset isolation; three Unity-only preview tests are present but unrun
- Photoshop or CLIP STUDIO PAINT application round trips, 16/32bit/ICC fidelity, native adjustments/smart objects/text rendering
- 4K production interaction performance, dense large layers, real process RSS/VRAM and disk spill; the 4K sparse unit case is **not** a performance certification
- Full OS crash/power-loss/fsync behavior, Windows replacement locks, disk-full and permission-denied recovery
- Complete G0 or G1 acceptance. This is a verified core plus an unvalidated Unity-adapter checkpoint

## Official Unity validation route checked

An official Linux 2022.3.62f2 Editor archive was found and a HEAD request confirmed availability (approximately 4.14GB). It was not downloaded or installed. Unity documents an activated license as required even for headless use, with Personal requiring Hub sign-in. Download availability does not establish permission/activation for this cloud environment. A licensed Unity environment is the next requirement for genuine Editor execution.

- [Managing your Unity license, 2022.3](https://docs.unity3d.com/2022.3/Documentation/Manual/ManagingYourUnityLicense.html)
- [Managed plug-ins and compiling against Unity DLLs](https://docs.unity3d.com/2022.3/Documentation/Manual/UsingDLL.html)
- [Unity Editor software terms](https://unity.com/legal/editor-terms-of-service/software)
- [Official 2022.3.62f2 release](https://unity.com/releases/editor/whats-new/2022.3.62f2)

Source inspection, syntax checking and temporary API-shape stubs cannot replace this gate. No Unity account, license or new legal agreement was created/accepted.
