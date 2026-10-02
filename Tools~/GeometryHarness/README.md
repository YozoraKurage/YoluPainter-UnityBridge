# Pure geometry math-adapter harness

This optional .NET 8/C# 8 harness compiles the actual `SurfaceGeometry.cs` against small **test adapters** for Unity vector/bounds/ray math. It executes the identical pure geometry methods from `GeometryTests.cs` through a small NUnit-like assertion adapter. Python 3 extracts those methods; no NuGet dependency is required.

Run `./run.sh` with `dotnet` on PATH, or `DOTNET=/path/to/dotnet ./run.sh`.

Passing means the tested algorithm branches and math are consistent with these adapters. It **does not** mean Unity compilation, Unity API behavior, actual NUnit/EditMode tests, safe preview ownership, GPU rendering, shader compilation, Editor lifecycle behavior, SRP compatibility, or device performance have passed. Use Unity 2022.3's Test Runner for those checks. The script deliberately excludes the Unity-only preview load/ownership tests.

The vector adapter follows Unity's normalization threshold. Keep the adapters separate from production code; never use them in the Unity package. Generated test source and `bin`/`obj` are disposable.
