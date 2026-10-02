# PSD compatibility and safety gate

Status: implemented **limited RGB8 raster codec and G0 format spike**, 2026-10-02. This is not broad Photoshop/CLIP STUDIO compatibility, a production PSD backend, or completion of the full painting specification.

## What actually works

`Dot.TexturePainter.Core.Psd` is pure C#6 with no Unity or third-party runtime dependency. It writes an actual PSD version 1 containing normal raster layers, straight RGBA8 channel data, bounds, opacity, visibility, Unicode `luni` names, positive unique `lyid` IDs, and a current merged composite. Physical PSD layer records are bottom-to-top; the public DTO is top-to-bottom. Layer color samples under zero alpha and outside the canvas are retained exactly.

The reader accepts raw and PackBits/RLE layer and merged data. The writer deliberately emits uncompressed raw channels. It is a bounded whole-document prototype, not the future streaming/tile-backed codec required for large production documents.

### Public API and integration

- `PsdCodec.Read(byte[], PsdLimits = null)` and `Read(Stream, PsdLimits = null)` return `PsdReadResult`
- Result mode is `EditableRaster`, `PreserveOnly`, or `Rejected`; `Document` is **null unless editable**
- Result `Diagnostics` identify unsupported feature keys/resource IDs and source byte offsets/lengths, subject to a diagnostic-count cap
- `CopyOriginalBytes()` returns a fresh, complete unchanged source copy when source retention succeeded
- `PsdCodec.Write(PsdDocument, PsdLimits = null)` creates a new native raster PSD snapshot
- `PsdCodec.WriteEdited(PsdReadResult origin, PsdDocument edited, PsdLimits = null)` rejects PreserveOnly and Rejected origins before writing
- `PsdDocument` has Width, Height, and Layers. `PsdRasterLayer` has Id, Name, Left, Top, Width, Height, Opacity, Visible, and top-down straight `PixelsRgba`

The codec performs no filesystem writes. The application must use `WriteEdited` for imported-source edits, and must not build a partial native DTO from an unsupported import to bypass that guard. Copying original bytes is a backup/preservation operation, not permission to replace an externally modified file. The application owns immutable save snapshots, external-change detection, and transactional path replacement. Do not mutate DTOs during writing.

## E / P / R / T matrix

E = semantic editing in this codec; P = original-information preservation; R = rendering reproduction; T = evidence actually executed. P does not imply R. “Whole source” means byte-exact **unchanged-file preservation only**; unsupported records are not spliced into an edited file.

| PSD element | E: editing | P: preservation | R: reproduction | T: tested conditions |
|---|---|---|---|---|
| Untagged RGB8 normal raster, explicit valid IDs | Limited editable subset | Layer samples, names, IDs, bounds, order, visibility, opacity; whole original also retained | Declared encoded-sample source-over only | C# NUnit; independent raw/RLE fixtures; actual C# output checked by three external readers |
| Invisible RGB / off-canvas raster pixels | Yes | Byte-exact layer RGBA | Excluded from canvas only as geometry/visibility requires | C# assertions and psd-tools byte comparison |
| Raw compression | Read/write | Pixel-exact; file representation may be normalized on rewrite | Current merged image generated | C# and independent byte oracle + Pillow + psd-tools + ImageMagick |
| PackBits/RLE | Read; rewrites as raw | Pixel-exact | Same numeric compositor | Independent RLE import and C# rewrite; literal, repeat, no-op, overflow/underflow tests |
| Unicode names / layer IDs | Yes, unique positive IDs only | UTF-16 surrogate pairs, order and semantic name | Not a visual claim | C# Japanese/emoji round trip; external Unicode/ID/order/bounds checks |
| Missing/duplicate/invalid IDs, ambiguous legacy names | No: PreserveOnly | Whole source | Not claimed | Synthetic C# rejection/preservation tests |
| Other blend modes; clipping; lock/extra flags | No: PreserveOnly | Whole source | Not implemented | Synthetic guard tests; no native Photoshop fixture claim |
| Raster/vector masks, Blend If, groups, fill opacity, layer effects | No: PreserveOnly | Whole source | Not implemented | Synthetic mask/range/tag tests; no actual mask/effect rendering validation |
| Adjustments (including Curves), text, embedded/linked smart objects | No: PreserveOnly | Whole source, including embedded bytes; no external links followed | Not implemented; no substitute rasterization | Synthetic feature-key guards only; real Photoshop/CSP type-specific round trips NOT RUN |
| Unknown layer/document tags and image resources | No: PreserveOnly | Whole source | No inference from cached pixels | Unknown `zzzz` fixture byte preservation and rewrite guard; ICC resource guard |
| ICC and any other image resource | No: PreserveOnly | Whole source | No profile conversion or color-managed display | ICC marker guard; no ICC rendering validation |
| 16/32-bit, Gray/CMYK/Lab/etc. | No: PreserveOnly within header/source limits | Whole source, no quantization | Not decoded | 16-bit header guard; broader actual-file corpus NOT RUN |
| PSB | No: PreserveOnly after basic header check | Whole source within source cap | Not decoded | PSB header guard only; full PSB structure NOT validated |
| ZIP/prediction compression | No: PreserveOnly | Whole source | Not decoded | Synthetic compression guard |
| Missing/stale/different merged image | No: PreserveOnly | Whole source | No stale composite presented as newly rendered | Missing and mismatched merged-image tests |
| Malformed lengths / over-budget buffers / invalid UTF-16 | Rejected | Input retained only when source cap permits; oversize sources are not retained | Not decoded | Truncation, overflow, budgets, malformed RLE, 500 deterministic mutations |

The narrow acceptance policy intentionally sends many ordinary Photoshop-produced files to PreserveOnly, including files with otherwise harmless but unimplemented image resources or default blend-range records. This is preferable to silently discarding metadata or claiming that preservation establishes visual parity.

### Exact editable acceptance rules

PSD v1; RGB8; 3 merged RGB channels or 4 with explicit negative-count merged-transparency flag; non-empty raster layers; normal blend; no clipping, masks, blend ranges, resources, global masks, extra flags, unknown tags, or unsupported compression. Layer channels must uniquely contain R/G/B and optionally transparency. Only `luni` and `lyid` additional layer data are interpreted. Empty layers are currently PreserveOnly. Duplicate/missing IDs are never assigned replacement identities automatically.

The stored merged image must agree with the codec's regenerated white-matted sample-space composite within one byte per applicable channel. This check catches some interpretation differences or stale images; it is not a proof of Photoshop equivalence. A matching current image cannot establish equivalence for every future edit.

Recognized names/IDs and pixel semantics survive editable rewrites. Full-file byte equality is **not** promised for editable rewrites: RLE becomes raw, the legacy Pascal fallback can be normalized, and the merged cache is regenerated. The original remains available separately.

## Alpha and color contract

Layer data is straight RGBA8. All transparent-pixel RGB remains untouched. Normal compositing uses high-precision premultiplied working values for one scanline, in encoded sample space, then rounds the output once. This is explicitly **not** a linear-light/sRGB-conversion implementation.

Merged PSD RGB is white-matted and merged transparency is its fourth channel, signaled by negative layer count. ImageMagick and psd-tools correctly remove that matte. Pillow exposes the stored white-matted RGB directly; its output alone is not a transparent-render fidelity oracle. Undo/save must retain the straight layer data, not recover source pixels from the quantized merged preview. Low-alpha colors in an un-matted merged preview can differ because 8-bit matting loses precision.

The output has no ICC profile. Native documents must declare their sample interpretation in the application; this codec must not label an arbitrary imported profile as sRGB.

## Resource and parser safety

Default limits (caller-configurable within PSD v1 dimensional constraints):

| Budget | Default |
|---|---:|
| Input / retained source bytes | 128 MiB |
| Encoded output bytes | 128 MiB |
| Maximum single dimension | 8192 |
| Canvas and per-layer pixels | 16,777,216 |
| Layer count | 256 |
| Decoded raster + working pixel buffers | 256 MiB |
| Interpreted/opaque metadata bytes inspected | 4 MiB |
| Layer-name UTF-16 code units | 4096 |
| Diagnostic entries | 128 |

Length-delimited readers validate parent bounds before reads, allocations or skips. Unsigned PSD lengths are checked before conversion to managed indices. Rectangle differences use 64-bit arithmetic; RLE rows cannot expand past their declared width and must fill exactly that width. RLE row tables and compressed row lengths are independently bounded. Compression that is not implemented is never passed to an unchecked decompressor. Byte-array inputs are cloned; returned original-byte copies do not alias retained data.

These are logical allocation budgets, not a hard process RSS ceiling. Caller-owned source buffers, retained source, stream staging, CLR object overhead and garbage collection add memory. `Read(Stream)` consumes at most the source cap plus one lookahead byte and leaves the stream open. `Read(byte[])` cannot prevent the caller from allocating an oversized input first. Current writer and reader are unsuitable as the final low-memory 4K/many-layer backend without streaming/tile integration. No cancellation/background UI scheduling is implemented inside this codec.

## Executed verification

2026-10-02, Linux x64:

1. Actual pure C# codec compiled under .NET SDK 8.0.425 with `LangVersion=6`; no Unity runtime was used
2. **28/28 PSD NUnit cases passed** under NUnitLite 3.14.0 / .NET 8.0.31. They include an independent fixture encoder, format guards, bounds, RLE, source-copy isolation, layer attributes, Unicode and 500 deterministic mutations. See `validation/psd-spike/psd-nunit.xml`; this is ordinary .NET execution, not Unity EditMode execution
3. `tools/psd_spike.py` independently generated 3-layer raw and RLE fixtures and an opaque unknown-record fixture
4. A temporary C#6 bridge compiled the actual `Runtime/Core/Psd/*.cs`, imported both independent raster fixtures, performed guarded writes, reimported them, and checked every raster byte. It verified PreserveOnly + byte-identical copy + edit rejection for the unknown fixture
5. The C#-written PSDs were parsed by the independent Python byte oracle, Pillow **11.3.0**, psd-tools **1.10.9**, and ImageMagick **7.1.1-43 Q16**
6. External psd-tools verification confirmed exact layer RGBA, Unicode names (including emoji), IDs, order, offsets/bounds, opacity and visibility. Independently forced recomposition differed by at most **1/255** per sample on these fixtures

Both C# raster outputs are 530 bytes with SHA-256 `fce3e8fa3fd542c818258f0d8361d945df22cd9196d1056905e8271da24d4ed1`. Their pixel equality is meaningful; their tiny dimensions are not a performance test. See `validation/psd-spike/report.json`, `csharp-bridge.log`, and the five `.psd` fixtures for reproducible evidence.

Reproduction from repository root with an existing SDK:

```sh
PYTHONPATH=/tmp/dot_psd_oracle python tools/psd_spike.py \
  --out validation/psd-spike --dotnet /workspace/shared/dotnet/dotnet
```

The Python script installs nothing. Without `--dotnet`, it only tests its independently generated fixtures and explicitly reports C# execution as not run. Without optional readers, those checks report SKIP. `--csharp file.psd ...` checks other actual writer outputs. Optional development-only readers used here were installed outside the package; psd-tools is MIT, Pillow is MIT-CMU. They are not shipped or required by the Unity codec.

NOT RUN: Unity Editor compilation/EditMode execution; Windows/macOS; actual Photoshop or CLIP STUDIO authored round trips; broad legacy/modern PSD corpus; color-profile parity; 16/32-bit rendering; low-memory or 4K performance. These remain release gates, not implied passes.

## References and remaining gates

- [Adobe Photoshop File Formats Specification, November 2019](https://www.adobe.com/devnet-apps/photoshop/fileformatashtml/): normative binary structure used for the implementation; it does not define all rendering semantics
- [psd-tools source repository](https://github.com/psd-tools/psd-tools): independent development-time interoperability oracle, not a production dependency or proof of Photoshop parity
- [Pillow PSD reader](https://github.com/python-pillow/Pillow/blob/main/src/PIL/PsdImagePlugin.py): independent stored-sample decoding check

Before expanding E, add real version-labelled Photoshop/CSP fixtures, native-app reopen/re-edit validation, ICC-aware reference rendering, unknown-record mutation safety, masks/clip/group semantics, cancellation, and streaming allocations. Unsupported-feature preservation must remain the default while those gates are incomplete.
