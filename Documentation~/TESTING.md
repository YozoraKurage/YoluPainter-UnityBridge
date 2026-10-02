# Build and test

## Portable core (requires .NET 8 SDK)

```sh
./tools/run_core_tests.sh
# If dotnet is not on PATH:
DOTNET=/absolute/path/to/dotnet ./tools/run_core_tests.sh
```

This compiles the actual production Core folder, not a Python translation or mock core. It does not compile UnityEngine/UnityEditor adapters. `tools/CoreNUnit` holds broader NUnit suites and a pinned NUnit/NUnitLite test-only dependency. The checked-in runtime package itself has no NuGet dependency.

For read-only CI homes set DOTNET_CLI_HOME and NUGET_PACKAGES to writable directories. The included script supplies /tmp defaults. Do not interpret .NET 8 compilation as proof of Unity's exact runtime or API compatibility.

## Unity import and EditMode gate (NOT executed in the cloud)

1. Open a disposable Unity 2022.3 project with the intended renderer pipeline
2. Add this UPM package from disk
3. Add `com.dot.texture-painter` to the test project's manifest `testables` array and ensure Unity Test Framework is available
4. Fix any actual Unity compilation/import errors before running tests
5. Window → General → Test Runner → EditMode → Run All
6. Record Editor exact patch, OS, CPU/GPU/driver, graphics API, color space, pipeline, tablet driver and model

Example batch command, adapt absolute paths and Unity license setup:

```sh
Unity -batchmode -projectPath /path/to/test-project -runTests -testPlatform EditMode -testResults /path/to/editmode-results.xml -logFile /path/to/editor.log
```

Use a graphics-enabled Editor for GPU tests. A `-nographics` run is insufficient for shader, rendering and readback gates. This repository does not activate or install Unity or accept Unity licensing on the user's behalf.

## Manual acceptance script

- Make 256/512 document, draw dot/slow line/fast line, vary pressure, erase, undo/redo, Escape and leave focus midstroke
- Check initial click, window drag/release outside, repeated new/open/save-as, exception cancellation, closing and domain reload
- Add layer, name with Japanese/emoji, change opacity, hide/show, reorder, delete and undo; ensure per-channel isolation
- Paint same UV in 2D/3D and check preview synchronized; test seam, backside, overlapping shell and material boundaries
- Try unreadable/skinned/incomplete model, nonmanifold edge, missing UV and out-of-range UV; verify refusal/diagnostic instead of invisible partial coverage
- Load supplied demo seam mesh when available; check source scene dirty state and asset hashes unchanged
- Save new root, reopen, compare native bytes, PSD layer IDs/names/opacities/visible state; add unsupported PSD metadata and verify edit refusal
- Interrupt saves at each stage, corrupt newest/current manifest/file, externally modify PSD, retry, and confirm last intact generation remains available
- Run GPU brush parity probe; test linear/gamma project, D3D/Metal/Vulkan only where supported; collect pixel error and texture-orientation images
- Run a 4K sparse stroke workload, then dense layers/channels under low RAM/VRAM; record source tiles, real RSS, GPU allocations, peak save memory, median/p95/p99 pointer-to-display latency
- Verify pen/mouse de-duplication, true pressure-zero behavior, multi-monitor/HiDPI, tablet buttons and driver update transitions
- Photoshop/CSP actual reopen + resave + reimport fixture matrix is still mandatory. Open-source PSD decoders alone do not satisfy this gate

## Retain results

Keep logs, XML, fixture hashes, OS/API/version metadata and image diffs. Never change the matrix to 'tested' based on code presence or a syntax check. Current measured results live in VALIDATION.md and validation/.
