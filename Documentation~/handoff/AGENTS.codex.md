# Development contract
- This is a G0/G1 experimental Unity 2022.3 editor-only UPM package, not the completed 22-page feature scope.
- Preserve source meshes/materials/scenes. Preview clones are renderer/transform-only.
- Never silently discard unsupported PSD data; preserve original bytes and refuse unsafe edits.
- Core code is Unity-independent and must pass `tools/run_core_tests.sh`.
- Unity API compilation, GPU output, tablet behavior, external PSD app round-trips are separate required gates.
- Update docs/STATUS.md accurately after changes. Do not label unexecuted tests as passed.
