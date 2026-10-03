using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Yozolab.YoluPainter.Core.MeshMaps
{
    /// <summary>
    /// mesh map のベイク（純 C#、CPU。行ごとに Parallel.For）。GPU の経路を足すときの基準でもある。
    /// <list type="number">
    /// <item>UV 空間でテクセルを n×n のサブサンプルに分け、その中心を三角形に当てる（中心が三角形の内側か辺の上のサブサンプルだけ）。
    /// UV が重なるサブサンプルは添字の小さい三角形の値にし、Overlap と印を付けて数える（重なった面を別々の値にはできない: 仕様 12 章）。</item>
    /// <item>サブサンプルごとに低ポリの位置・面の法線・頂点法線・接線を重心座標で補間する。高ポリ（参照）があれば、低ポリの点から
    /// ケージの法線の向きに外へ Frontal 出た所から内へ Frontal + Rear の範囲で最初に当たる高ポリの面を探し、当たればその面で
    /// 法線・位置・AO・厚み・曲率・ID を求める。当たらなければ低ポリで求め（自己ベイクと同じ）、接空間の法線は平ら、高さは 0.5、
    /// 不透明度は 0 にする。AO・厚み・ベントノーマルは BVH にレイを飛ばす。レイの向きは Hammersley の点列をサブサンプルごとの決まった
    /// 乱数で回したもの（同じ入力なら並列の分け方に依らず同じバイト列）。</item>
    /// <item>テクセルの値は覆うサブサンプルの平均（法線は平均を正規化し直す、ID は多数決で同数なら先のサブサンプル）。</item>
    /// <item>余白: 島の外の空のテクセルを 8 近傍で 1 段ずつ広げ、いちばん近い島のテクセルの値を写す。島の本体には書かない。
    /// どの段にも届かない所は Empty のまま値 0（データを作らない）。</item>
    /// </list>
    /// メモリは割り当てる前に見積もって予算を超えれば断る。取消・時間切れは途中の結果を返さない（呼び出し側の持つマップは変わらない）。
    /// 進み具合のコールバックは呼んだスレッドで、行のまとまりごとに呼ぶ（false を返すと取消）。
    /// </summary>
    public static class MeshBaker
    {
        /// <summary>結果を変える変更をしたら上げる（由来に入り、古いマップを見分ける）。2: サブサンプル・高ポリ・新しい種類。</summary>
        public const int EngineVersion = 2;
        /// <summary>空間・基準の姿勢・高ポリの無い焼く元（自己ベイク）。高ポリがあれば焼く元は <see cref="MeshBakeSettings.SourceKey"/>。</summary>
        public const string Space = "SnapshotWorld", Pose = "StaticSnapshot", Source = "Self";
        /// <summary>レイの始点を面から浮かせる量（対角線比）。</summary>
        const double RayOffset = 1e-5;
        /// <summary>面すれすれのレイ（面の法線との内積がこれ以下）は数えない。</summary>
        const double GrazingLimit = 1e-4;
        const int BandRows = 8;
        const int KindCount = 10;
        /// <summary>GPU にまとめて渡すサンプルの数の上限（1 回の行のまとまり）。仕事・結果・テクセルの和で約 100 MiB。</summary>
        const int MaxDeferredJobs = 262144;
        const long DeferredBytes = MaxDeferredJobs * (104L + 48 + 1) + MaxDeferredJobs * (17L * 8 + 16 * 4 + 4 + 4 + 1);

        /// <summary>この条件で焼いたときの由来の鍵（<see cref="MeshMapProvenance.ConditionKey"/>）。キャッシュの照合に使う。</summary>
        public static string ConditionKey(MeshBakeInput input, MeshBakeSettings settings, MeshMapKind kind, MeshBakeInput reference = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return MeshMapProvenance.ComputeConditionKey(kind, EngineVersion, input.Hash, input.UvChannel, settings.Width, settings.Height, settings.TargetSlot,
                settings.Padding, settings.Antialiasing, settings.KindKey(kind), Space, Pose, settings.SourceKey(reference?.Hash));
        }

        static int Threads(MeshBakeBudget budget) => budget != null && budget.MaxDegreeOfParallelism > 0 ? budget.MaxDegreeOfParallelism : Math.Max(1, Environment.ProcessorCount);
        static bool NeedsAoRays(MeshBakeSettings s) => s.Includes(MeshMapKind.AmbientOcclusion) || s.Includes(MeshMapKind.BentNormal);
        static bool NeedsRays(MeshBakeSettings s) => NeedsAoRays(s) || s.Includes(MeshMapKind.Thickness);

        /// <summary>割り当てる量の見積もり（出力・由来・余白の作業・BVH・UV の帯・行の作業領域・曲率の辺の表・高ポリ。曲率の線分と
        /// 帯の参照は割り当てる前に別に確かめる）。</summary>
        public static long EstimateBytes(MeshBakeInput input, MeshBakeSettings settings, MeshBakeBudget budget = null, MeshBakeInput reference = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            long texels = (long)settings.Width * settings.Height, triangles = input.TriangleCount, n = settings.Antialiasing;
            long bytes = texels; // 由来
            foreach (var kind in settings.Maps) bytes += texels * BakedMeshMap.ChannelCount(kind) * 2;
            if (settings.Padding > 0) bytes += texels * 5;
            bytes += SurfaceBytes(triangles, settings, NeedsRays(settings), settings.Includes(MeshMapKind.Curvature));
            bytes += triangles * (6 * 8 + 4 * 4 + 4 + 2 * 4);             // UV の三角形と帯の参照（1 つあたり 2 帯と見る）
            bytes += ((long)settings.Height / BandRows + 2) * 4;
            bytes += triangles * (9 * 4 * 2);                              // 接線・従法線
            if (settings.Includes(MeshMapKind.Id))
            {
                bytes += IdTable.EstimateBytes(triangles, settings.IdSource);
                if (settings.ManualIdColors.Colors.Count > 0) bytes += MeshRegions.EstimateBytes(triangles) + triangles * 12;
                if (reference != null && settings.IdSource != MeshIdSource.UvIsland) bytes += IdTable.EstimateBytes(reference.TriangleCount, settings.IdSource);
            }
            if (reference != null)
            {
                bytes += triangles * 9 * 4;                                // ケージの法線
                long high = reference.TriangleCount;
                bytes += SurfaceBytes(high, settings, true, settings.Includes(MeshMapKind.Curvature));
                if (settings.ReferenceMatchByName) bytes += high * MeshRayBvh.BytesPerTriangle;
            }
            bytes += (long)Threads(budget) * settings.Width * n * n * (4 + 8 + 8 + 1 + 1) + Threads(budget) * 2048L;
            return bytes;
        }
        static long SurfaceBytes(long triangles, MeshBakeSettings settings, bool rays, bool curvature)
        {
            long bytes = triangles * (3 * 4 + 1);                          // 面の法線・有効の印
            if (rays) bytes += triangles * MeshRayBvh.BytesPerTriangle;
            if (curvature) bytes += triangles * (3 * 4 + 3 * 32 + 4 + 2 * 40); // 溶接・辺の表・成分
            return bytes;
        }

        sealed class Control
        {
            public volatile bool Stop;
            public MeshBakeStatus Reason = MeshBakeStatus.Completed;
            public CancellationToken Token;
            public Stopwatch Clock;
            public double MaxSeconds;
            public bool ShouldStop()
            {
                if (Stop) return true;
                if (Token.IsCancellationRequested) { lock (this) if (!Stop) { Reason = MeshBakeStatus.Canceled; Stop = true; } return true; }
                if (MaxSeconds > 0 && Clock.Elapsed.TotalSeconds > MaxSeconds) { lock (this) if (!Stop) { Reason = MeshBakeStatus.TimedOut; Stop = true; } return true; }
                return false;
            }
            public void Cancel() { lock (this) if (!Stop) { Reason = MeshBakeStatus.Canceled; Stop = true; } }
        }

        /// <param name="progress">(0〜1, 段階の名前) を受け取り、false を返すと取消。呼んだスレッドで呼ぶ。</param>
        /// <param name="reference">高ポリ（低ポリと同じ空間）。null なら自己ベイク。</param>
        /// <param name="rayTracer">AO・ベントノーマル・厚みのレイをまとめて処理するもの（GPU など）。null か、使えない・途中で失敗したら CPU。</param>
        /// <exception cref="MeshBakeRefusedException">予算を超える、焼き込む三角形が無い、UV が 0〜1 の外。何も割り当てない。</exception>
        public static MeshBakeResult Bake(MeshBakeInput input, MeshBakeSettings settings, MeshBakeBudget budget = null,
            Func<double, string, bool> progress = null, CancellationToken cancellation = default, MeshBakeInput reference = null, IMeshBakeRayTracer rayTracer = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Validate(); settings = settings.WithIdContext(input, reference, settings.ManualIdColors);
            budget = budget ?? new MeshBakeBudget();
            var report = new MeshBakeReport();
            var control = new Control { Token = cancellation, Clock = Stopwatch.StartNew(), MaxSeconds = budget.MaxSeconds };
            int width = settings.Width, height = settings.Height, triangles = input.TriangleCount;
            var corners = input.Corners; var uvs = input.Uvs;

            // 焼き込む三角形（スロット・UV の面積）と、UV の範囲の確認。まだ大きなものは割り当てない
            var receivers = new List<int>();
            for (int t = 0; t < triangles; t++)
            {
                int slot = input.Slots[t];
                if (slot < 0 || (settings.TargetSlot >= 0 && slot != settings.TargetSlot)) continue;
                int k = t * 6;
                double area = (uvs[k + 2] - uvs[k]) * (uvs[k + 5] - uvs[k + 1]) - (uvs[k + 4] - uvs[k]) * (uvs[k + 3] - uvs[k + 1]);
                if (Math.Abs(area) * width * height < 1e-9) { report.ZeroUvAreaTriangles++; continue; }
                if (!HasArea(corners, t)) continue; // 面積の無い三角形（下で数える）は向きが決まらないので焼かない
                for (int i = 0; i < 6; i++)
                    if (uvs[k + i] < -1e-6f || uvs[k + i] > 1 + 1e-6f)
                        throw new MeshBakeRefusedException("UV" + input.UvChannel + " of triangle " + t + " lies outside 0–1. Repeating/UDIM UVs are not baked (nothing would match the texture).");
                receivers.Add(t);
            }
            if (receivers.Count == 0)
                throw new MeshBakeRefusedException(settings.TargetSlot >= 0 ? "No triangle with UVs uses material slot " + settings.TargetSlot + "; nothing to bake." : "No triangle has UVs; nothing to bake.");
            long estimate = EstimateBytes(input, settings, budget, reference) + (rayTracer != null && NeedsRays(settings) ? DeferredBytes : 0);
            report.EstimatedBytes = estimate;
            if (estimate > budget.MaxBytes)
                throw new MeshBakeRefusedException("Baking " + settings.Maps.Length + " map(s) at " + width + "×" + height + (settings.Antialiasing > 1 ? " with " + settings.Antialiasing + "×" + settings.Antialiasing + " antialiasing" : "")
                    + (reference != null ? " from a " + reference.TriangleCount + "-triangle high poly" : "") + " needs about " + (estimate >> 20) + " MiB, over the " + (budget.MaxBytes >> 20)
                    + " MiB budget. Bake fewer maps at once or raise the memory budget (Project Settings > YoluPainter).");
            report.ReceivingTriangles = receivers.Count;
            if (!Report(progress, control, 0, "Preparing")) return Stopped(control, report);

            long remaining = budget.MaxBytes - estimate;
            double diagonal = input.Diagonal;
            var low = new SurfaceData(input);
            report.DegenerateTriangles = low.Degenerate;
            bool curvatureWanted = settings.Includes(MeshMapKind.Curvature);
            if (NeedsRays(settings))
            {
                var occluders = new List<int>();
                for (int t = 0; t < triangles; t++)
                    if (low.Valid[t] && (settings.Occluders == MeshOccluders.WholeModel || input.Slots[t] == settings.TargetSlot || settings.TargetSlot < 0)) occluders.Add(t);
                low.Occluders = new MeshRayBvh(corners, occluders);
                report.OccluderTriangles = occluders.Count;
            }
            if (!Report(progress, control, 0.01, "Preparing")) return Stopped(control, report);
            if (curvatureWanted)
            {
                low.Curvature = new MeshCurvatureField(input, low.Valid, low.FaceNormals, settings.CurvatureRadius * diagonal, remaining);
                report.BoundaryEdges = low.Curvature.BoundaryEdges; report.NonManifoldEdges = low.Curvature.NonManifoldEdges;
                report.InconsistentWindingEdges = low.Curvature.InconsistentEdges; report.CurvatureSegments = low.Curvature.SegmentCount;
            }
            SurfaceData high = null; Projection projection = null;
            if (reference != null)
            {
                high = new SurfaceData(reference);
                var all = new List<int>();
                for (int t = 0; t < reference.TriangleCount; t++) if (high.Valid[t]) all.Add(t);
                high.Occluders = new MeshRayBvh(reference.Corners, all);
                if (!Report(progress, control, 0.02, "Preparing")) return Stopped(control, report);
                if (curvatureWanted) high.Curvature = new MeshCurvatureField(reference, high.Valid, high.FaceNormals, settings.CurvatureRadius * diagonal, remaining);
                projection = new Projection(input, reference, high, settings, receivers);
                report.ReferenceTriangles = all.Count;
            }
            var frames = new LowFrames(input, low, receivers, settings.Includes(MeshMapKind.TangentNormal));
            var ids = settings.Includes(MeshMapKind.Id) ? new IdTable(input, reference, settings.IdSource, receivers, settings.ManualIdColors) : null;
            var raster = new UvRaster(input, receivers, width, height, settings.Antialiasing, remaining);
            report.PrepareSeconds = control.Clock.Elapsed.TotalSeconds;
            if (!Report(progress, control, 0.05, "Baking")) return Stopped(control, report);

            var job = new Job(input, settings, raster, low, high, projection, frames, ids, control);
            int threads = Threads(budget);
            var options = new ParallelOptions { MaxDegreeOfParallelism = threads };
            int batch = Math.Max(16, Math.Min(256, threads * 4));
            report.RayBackend = "CPU";
            if (rayTracer != null && NeedsRays(settings))
            {
                string why;
                try { why = rayTracer.Prepare(job.Scene()); }
                catch (Exception ex) { why = ex.Message; }
                if (why == null)
                {
                    report.RayBackend = rayTracer.Name;
                    job.Defer(Math.Max(1, Math.Min(256, MaxDeferredJobs / (width * settings.Antialiasing * settings.Antialiasing))));
                    batch = job.BatchRows;
                }
                else { report.Diagnostics.Add("The GPU path is not available (" + why + "); rays ran on the CPU."); rayTracer.Release(); rayTracer = null; }
            }
            else rayTracer = null;
            try
            {
                for (int y0 = 0; y0 < height; y0 += batch)
                {
                    int y1 = Math.Min(height, y0 + batch);
                    job.BeginBatch(y0);
                    Parallel.For(y0, y1, options, () => new Scratch(width, settings.Antialiasing), (y, state, scratch) => { job.Row(y, scratch); return scratch; },
                        scratch => { Interlocked.Add(ref job.Rays, scratch.Rays); Interlocked.Add(ref job.Projected, scratch.Projected); Interlocked.Add(ref job.Missed, scratch.Missed); });
                    if (control.ShouldStop()) return Stopped(control, report);
                    if (rayTracer != null)
                    {
                        try { job.FinishBatch(y0, y1, rayTracer); }
                        catch (Exception ex) when (!(ex is OutOfMemoryException))
                        {
                            // GPU が途中で失敗した: 全体を CPU で焼き直す（途中の GPU の結果は使わない）
                            rayTracer.Release(); rayTracer = null;
                            var cpu = Bake(input, settings, budget, progress, cancellation, reference, null);
                            cpu.Report.Diagnostics.Insert(0, "The GPU path failed (" + ex.Message + "); the bake was redone on the CPU.");
                            return cpu;
                        }
                    }
                    if (!Report(progress, control, 0.05 + 0.85 * y1 / height, "Baking")) return Stopped(control, report);
                }
            }
            finally { rayTracer?.Release(); }
            report.Rays = job.Rays; report.ProjectedSamples = job.Projected; report.MissedSamples = job.Missed;
            report.RasterSeconds = control.Clock.Elapsed.TotalSeconds - report.PrepareSeconds;
            if (settings.Padding > 0 && !job.Pad(settings.Padding, options, progress)) return Stopped(control, report);
            report.PaddingSeconds = control.Clock.Elapsed.TotalSeconds - report.PrepareSeconds - report.RasterSeconds;

            foreach (byte b in job.Coverage)
                switch ((MeshTexelCoverage)b)
                {
                    case MeshTexelCoverage.Covered: report.CoveredTexels++; break;
                    case MeshTexelCoverage.Overlap: report.OverlapTexels++; break;
                    case MeshTexelCoverage.Padding: report.PaddedTexels++; break;
                    default: report.EmptyTexels++; break;
                }
            if (report.OverlapTexels > 0) report.Diagnostics.Add(report.OverlapTexels + " texels are covered by more than one triangle (overlapping or mirrored UVs). Those faces share pixels; the lower triangle index was baked.");
            if (report.ZeroUvAreaTriangles > 0) report.Diagnostics.Add(report.ZeroUvAreaTriangles + " triangles in the target slot have no UV area and were not baked (they still occlude).");
            if (input.NormalSource != "authored") report.Diagnostics.Add(input.NormalSource == "face" ? "No vertex normals were given; the normal map and ray directions use flat face normals." : "Vertex normals were reconstructed from the shape (" + input.NormalSource + "); custom-edited normals are not reproduced.");
            if (settings.Includes(MeshMapKind.TangentNormal) && frames.FallbackTriangles > 0) report.Diagnostics.Add(frames.FallbackTriangles + " triangles had no tangents; their tangent frame was derived from the UVs (it may not match how the material decodes the normal map).");
            if (report.NonManifoldEdges > 0 || report.InconsistentWindingEdges > 0) report.Diagnostics.Add((report.NonManifoldEdges + report.InconsistentWindingEdges) + " edges are non-manifold or have flipped winding; curvature ignores them.");
            if (reference != null && report.MissedSamples > 0)
                report.Diagnostics.Add(report.MissedSamples + " of " + report.ProjectedSamples + " samples found no high-poly surface within the frontal/rear distances" + (settings.ReferenceMatchByName ? " (or no high-poly part with a matching name)" : "")
                    + "; they were baked from the low poly (tangent normal flat, height 0.5, opacity 0).");
            if (ids != null)
            {
                report.IdParts = ids.Parts;
                if (settings.ManualIdColors.Colors.Count > 0)
                    report.Diagnostics.Add("Manual ID colours override the source on " + settings.ManualIdColors.Colors.Count + " mesh part(s). Equal colours select together; palette separation is not guaranteed for manual colours.");
                else if (settings.IdSource == MeshIdSource.VertexColor)
                {
                    if (!input.HasColors) report.Diagnostics.Add("The model has no vertex colours, so the ID map is white.");
                }
                else report.Diagnostics.Add("ID map: " + ids.Parts + " part(s) (" + settings.IdSource + "); any two of their colours differ by at least " + IdPalette.MinimumSeparation(ids.Parts)
                    + " in one 8-bit channel, so a colour selection with a smaller tolerance picks one part.");
            }
            if (reference == null && (settings.Includes(MeshMapKind.TangentNormal) || settings.Includes(MeshMapKind.Height)))
                report.Diagnostics.Add("No high-poly reference: the tangent-space normal is flat and the height is 0.5.");
            var maps = new List<BakedMeshMap>();
            string source = settings.SourceKey(reference?.Hash);
            foreach (var kind in settings.Maps)
            {
                var provenance = new MeshMapProvenance(kind, EngineVersion, input.Hash, input.TopologyHash, input.UvChannel, width, height, settings.TargetSlot, settings.Padding,
                    settings.Antialiasing, settings.KindKey(kind), Space, Pose, source, job.BoundsMin, job.BoundsMax);
                maps.Add(new BakedMeshMap(provenance, job.Outputs[(int)kind], job.Coverage));
            }
            report.TotalSeconds = control.Clock.Elapsed.TotalSeconds;
            Report(progress, control, 1, "Done");
            return new MeshBakeResult(MeshBakeStatus.Completed, maps, report);
        }

        static bool HasArea(float[] corners, int t)
        {
            int k = t * 9;
            double e1x = corners[k + 3] - corners[k], e1y = corners[k + 4] - corners[k + 1], e1z = corners[k + 5] - corners[k + 2];
            double e2x = corners[k + 6] - corners[k], e2y = corners[k + 7] - corners[k + 1], e2z = corners[k + 8] - corners[k + 2];
            double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
            return Math.Sqrt(nx * nx + ny * ny + nz * nz) > 1e-30;
        }

        static bool Report(Func<double, string, bool> progress, Control control, double fraction, string phase)
        {
            if (control.ShouldStop()) return false;
            if (progress != null && !progress(fraction, phase)) { control.Cancel(); return false; }
            return true;
        }
        static MeshBakeResult Stopped(Control control, MeshBakeReport report)
        {
            report.TotalSeconds = control.Clock.Elapsed.TotalSeconds;
            return new MeshBakeResult(control.Reason == MeshBakeStatus.Completed ? MeshBakeStatus.Canceled : control.Reason, null, report);
        }

        /// <summary>
        /// ID の色の表（三角形ごとの 0xRRGGBB）。部品の分け方は元ごと: マテリアルスロット（全体を平らにした番号 = レンダラーとサブメッシュの組）、
        /// メッシュ（レンダラー）、メッシュの塊・UV アイランド（<see cref="MeshRegions"/>。ポリゴン塗りつぶしの範囲と同じ）。部品の番号は、
        /// スロット・レンダラーはその番号の小さい順、塊・アイランドは成分のいちばん小さい三角形の順で、低ポリは焼き込む三角形が持つ部品だけ、
        /// 高ポリは全部の部品を数え、低ポリ → 高ポリの順に <see cref="IdPalette"/> の色を振る（高ポリに当たらなかった所の色は高ポリの部品の色と
        /// 重ならない）。UV アイランドはいつも低ポリの島。頂点カラーは補間しない: 三角形ごとに角の色（8 bit に丸めた RGB）の多数決、3 つとも
        /// 違えば値のいちばん小さい色（どの角の色でもない混ぜた色を作らない。角の並びによらない）。
        /// </summary>
        sealed class IdTable
        {
            /// <summary>三角形ごとの色。Low は焼き込む三角形の分だけ意味がある。頂点カラーの無いメッシュは null（白）。</summary>
            public readonly int[] Low, High;
            /// <summary>色を振った部品の数（低ポリと高ポリの和。頂点カラーでは 0）。</summary>
            public readonly int Parts;
            public readonly int[] Manual;

            public static long EstimateBytes(long triangles, MeshIdSource source)
                => triangles * (source == MeshIdSource.MaterialAsset ? 160 : 8) + (source == MeshIdSource.MeshPart || source == MeshIdSource.UvIsland ? MeshRegions.EstimateBytes(triangles) : 0);

            public IdTable(MeshBakeInput low, MeshBakeInput high, MeshIdSource source, List<int> receivers, IdColorAssignments manual)
            {
                if (manual.Colors.Count > 0)
                {
                    var parts = new IdPartIndex(low); parts.Validate(manual); Manual = Enumerable.Repeat(-1, low.TriangleCount).ToArray();
                    for (int t = 0; t < Manual.Length; t++) if (manual.Colors.TryGetValue(parts.Parts[t], out int color)) Manual[t] = color;
                }
                if (source == MeshIdSource.MaterialAsset)
                {
                    var keys = new SortedSet<string>(StringComparer.Ordinal);
                    string Key(MeshBakeInput mesh, int t, bool reference) => !string.IsNullOrEmpty(mesh.MaterialKeys?[t]) ? "asset:" + mesh.MaterialKeys[t] : (reference ? "high:" : "low:") + mesh.Slots[t];
                    for (int t = 0; t < low.TriangleCount; t++) if (low.Slots[t] >= 0) keys.Add(Key(low, t, false));
                    if (high != null) for (int t = 0; t < high.TriangleCount; t++) keys.Add(Key(high, t, true));
                    Parts = keys.Count; var assetColors = IdPalette.Colors(Parts); var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (string key in keys) byKey.Add(key, assetColors[byKey.Count]);
                    Low = new int[low.TriangleCount]; foreach (int t in receivers) Low[t] = byKey[Key(low, t, false)];
                    if (high != null) { High = new int[high.TriangleCount]; for (int t = 0; t < High.Length; t++) High[t] = byKey[Key(high, t, true)]; }
                    return;
                }
                if (source == MeshIdSource.VertexColor) { Low = TriangleColors(low); High = high != null ? TriangleColors(high) : null; return; }
                var lowKeys = Keys(low, source);
                var lowRanks = Ranks(lowKeys, receivers);
                int[] highKeys = null; Dictionary<int, int> highRanks = null;
                if (high != null && source != MeshIdSource.UvIsland)
                {
                    highKeys = Keys(high, source);
                    var all = new List<int>(high.TriangleCount); for (int t = 0; t < high.TriangleCount; t++) all.Add(t);
                    highRanks = Ranks(highKeys, all);
                }
                Parts = lowRanks.Count + (highRanks?.Count ?? 0);
                var colors = IdPalette.Colors(Parts);
                Low = new int[low.TriangleCount];
                foreach (int t in receivers) Low[t] = colors[lowRanks[lowKeys[t]]];
                if (highKeys == null) return;
                High = new int[high.TriangleCount];
                for (int t = 0; t < High.Length; t++) High[t] = colors[lowRanks.Count + highRanks[highKeys[t]]];
            }

            /// <summary>三角形ごとの部品の鍵（スロット・レンダラー・成分の番号）。</summary>
            static int[] Keys(MeshBakeInput input, MeshIdSource source)
            {
                switch (source)
                {
                    case MeshIdSource.MaterialSlot: return input.Slots;
                    case MeshIdSource.Mesh: return input.Renderers;
                    case MeshIdSource.MeshPart: return MeshRegions.MeshParts(input.Corners, input.Slots, out _);
                    default: return MeshRegions.UvIslands(input.Uvs, input.Slots, out _);
                }
            }
            /// <summary>triangles の三角形が持つ鍵を小さい順に 0, 1, 2… にする。</summary>
            static Dictionary<int, int> Ranks(int[] keys, List<int> triangles)
            {
                var distinct = new SortedSet<int>(); foreach (int t in triangles) distinct.Add(keys[t]);
                var ranks = new Dictionary<int, int>(distinct.Count);
                foreach (int k in distinct) ranks.Add(k, ranks.Count);
                return ranks;
            }
            static int[] TriangleColors(MeshBakeInput input)
            {
                var colors = input.Colors; if (colors == null) return null;
                var result = new int[input.TriangleCount];
                for (int t = 0; t < result.Length; t++)
                {
                    int a = Rgb(colors, t * 12), b = Rgb(colors, t * 12 + 4), c = Rgb(colors, t * 12 + 8);
                    result[t] = a == b || a == c ? a : b == c ? b : Math.Min(a, Math.Min(b, c));
                }
                return result;
            }
            static int Rgb(float[] colors, int at)
            {
                int rgb = 0;
                for (int c = 0; c < 3; c++) { float v = colors[at + c]; rgb = rgb << 8 | (v <= 0 ? 0 : v >= 1 ? 255 : (int)(v * 255 + 0.5)); }
                return rgb;
            }
        }

        /// <summary>1 つのメッシュ（低ポリか高ポリ）の、焼くときに引く形: 面の法線、頂点法線、レイの BVH、曲率。</summary>
        sealed class SurfaceData
        {
            public readonly MeshBakeInput Input;
            public readonly float[] FaceNormals; public readonly bool[] Valid;
            public readonly int Degenerate;
            public MeshRayBvh Occluders; public MeshCurvatureField Curvature;
            public SurfaceData(MeshBakeInput input)
            {
                Input = input; int n = input.TriangleCount; var corners = input.Corners;
                FaceNormals = new float[n * 3]; Valid = new bool[n];
                for (int t = 0; t < n; t++)
                {
                    int k = t * 9;
                    double e1x = corners[k + 3] - corners[k], e1y = corners[k + 4] - corners[k + 1], e1z = corners[k + 5] - corners[k + 2];
                    double e2x = corners[k + 6] - corners[k], e2y = corners[k + 7] - corners[k + 1], e2z = corners[k + 8] - corners[k + 2];
                    double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
                    double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (!(length > 1e-30)) { Degenerate++; continue; }
                    Valid[t] = true; FaceNormals[t * 3] = (float)(nx / length); FaceNormals[t * 3 + 1] = (float)(ny / length); FaceNormals[t * 3 + 2] = (float)(nz / length);
                }
            }
            /// <summary>三角形 t の重心座標 (1 − b1 − b2, b1, b2) の点: 位置、面の法線、頂点法線（無い・面の裏を向くなら面の法線）。</summary>
            public void Point(int t, double b1, double b2, out double px, out double py, out double pz, out double gx, out double gy, out double gz, out double nx, out double ny, out double nz)
            {
                var corners = Input.Corners; var normals = Input.Normals; int k = t * 9; double b0 = 1 - b1 - b2;
                px = b0 * corners[k] + b1 * corners[k + 3] + b2 * corners[k + 6];
                py = b0 * corners[k + 1] + b1 * corners[k + 4] + b2 * corners[k + 7];
                pz = b0 * corners[k + 2] + b1 * corners[k + 5] + b2 * corners[k + 8];
                gx = FaceNormals[t * 3]; gy = FaceNormals[t * 3 + 1]; gz = FaceNormals[t * 3 + 2];
                nx = gx; ny = gy; nz = gz;
                if (normals == null) return;
                double sx = b0 * normals[k] + b1 * normals[k + 3] + b2 * normals[k + 6];
                double sy = b0 * normals[k + 1] + b1 * normals[k + 4] + b2 * normals[k + 7];
                double sz = b0 * normals[k + 2] + b1 * normals[k + 5] + b2 * normals[k + 8];
                double length = Math.Sqrt(sx * sx + sy * sy + sz * sz);
                if (length > 1e-6 && (sx * gx + sy * gy + sz * gz) > 0) { nx = sx / length; ny = sy / length; nz = sz / length; }
            }
        }

        /// <summary>低ポリの接空間（Unity がノーマルマップを読むのと同じ: 頂点の接線 t、従法線 cross(n, t)·w、法線 n を三角形の中で補間）。
        /// 接線の無い三角形は UV の勾配から作る（向きは w で表す）。</summary>
        sealed class LowFrames
        {
            public readonly float[] Tangents, Bitangents, Normals; // 9 個ずつ
            public readonly int FallbackTriangles;
            public LowFrames(MeshBakeInput input, SurfaceData low, List<int> receivers, bool wanted)
            {
                if (!wanted) return;
                int n = input.TriangleCount; var corners = input.Corners; var uvs = input.Uvs; var tangents = input.Tangents; var normals = input.Normals;
                Tangents = new float[n * 9]; Bitangents = new float[n * 9]; Normals = new float[n * 9];
                foreach (int t in receivers)
                {
                    bool hasTangents = false;
                    if (tangents != null) for (int c = 0; c < 3 && !hasTangents; c++) { int o = t * 12 + c * 4; hasTangents = tangents[o] != 0 || tangents[o + 1] != 0 || tangents[o + 2] != 0; }
                    double ux = 0, uy = 0, uz = 0, sign = 1;
                    if (!hasTangents)
                    {
                        FallbackTriangles++;
                        int k = t * 9, q = t * 6;
                        double e1x = corners[k + 3] - corners[k], e1y = corners[k + 4] - corners[k + 1], e1z = corners[k + 5] - corners[k + 2];
                        double e2x = corners[k + 6] - corners[k], e2y = corners[k + 7] - corners[k + 1], e2z = corners[k + 8] - corners[k + 2];
                        double du1 = uvs[q + 2] - uvs[q], dv1 = uvs[q + 3] - uvs[q + 1], du2 = uvs[q + 4] - uvs[q], dv2 = uvs[q + 5] - uvs[q + 1];
                        double r = du1 * dv2 - du2 * dv1; r = Math.Abs(r) > 1e-30 ? 1 / r : 0;
                        ux = (e1x * dv2 - e2x * dv1) * r; uy = (e1y * dv2 - e2y * dv1) * r; uz = (e1z * dv2 - e2z * dv1) * r;
                        double vx = (e2x * du1 - e1x * du2) * r, vy = (e2y * du1 - e1y * du2) * r, vz = (e2z * du1 - e1z * du2) * r;
                        double gx = low.FaceNormals[t * 3], gy = low.FaceNormals[t * 3 + 1], gz = low.FaceNormals[t * 3 + 2];
                        double cx = gy * uz - gz * uy, cy = gz * ux - gx * uz, cz = gx * uy - gy * ux;
                        sign = cx * vx + cy * vy + cz * vz < 0 ? -1 : 1;
                    }
                    for (int c = 0; c < 3; c++)
                    {
                        int o = t * 9 + c * 3;
                        double nx = low.FaceNormals[t * 3], ny = low.FaceNormals[t * 3 + 1], nz = low.FaceNormals[t * 3 + 2];
                        if (normals != null)
                        {
                            double sx = normals[o], sy = normals[o + 1], sz = normals[o + 2], l = Math.Sqrt(sx * sx + sy * sy + sz * sz);
                            if (l > 1e-6) { nx = sx / l; ny = sy / l; nz = sz / l; }
                        }
                        double tx, ty, tz, w;
                        if (hasTangents) { int p = t * 12 + c * 4; tx = tangents[p]; ty = tangents[p + 1]; tz = tangents[p + 2]; w = tangents[p + 3] < 0 ? -1 : 1; }
                        else
                        {
                            // 頂点の法線に直交させる（Unity の RecalculateTangents と同じ考え方）
                            double d = ux * nx + uy * ny + uz * nz; tx = ux - nx * d; ty = uy - ny * d; tz = uz - nz * d;
                            double l = Math.Sqrt(tx * tx + ty * ty + tz * tz); if (l > 1e-30) { tx /= l; ty /= l; tz /= l; }
                            w = sign;
                        }
                        Tangents[o] = (float)tx; Tangents[o + 1] = (float)ty; Tangents[o + 2] = (float)tz;
                        Normals[o] = (float)nx; Normals[o + 1] = (float)ny; Normals[o + 2] = (float)nz;
                        Bitangents[o] = (float)((ny * tz - nz * ty) * w); Bitangents[o + 1] = (float)((nz * tx - nx * tz) * w); Bitangents[o + 2] = (float)((nx * ty - ny * tx) * w);
                    }
                }
            }
        }

        /// <summary>高ポリへの投影: ケージの法線（低ポリ）と、レイを当てる BVH（名前で対応づけるなら部品ごと）。</summary>
        sealed class Projection
        {
            public readonly float[] Cage;          // 低ポリ 9 個ずつ
            public readonly MeshRayBvh[] TargetOf;  // 低ポリの三角形 → 当てる BVH（null は対応する部品が無い）
            public Projection(MeshBakeInput low, MeshBakeInput high, SurfaceData highData, MeshBakeSettings settings, List<int> receivers)
            {
                Cage = settings.ReferenceAverageNormals ? MeshBakeInput.ReconstructNormals(low.Corners, 180) : low.Normals ?? MeshBakeInput.ReconstructNormals(low.Corners, 0);
                TargetOf = new MeshRayBvh[low.TriangleCount];
                if (!settings.ReferenceMatchByName) { foreach (int t in receivers) TargetOf[t] = highData.Occluders; return; }
                var groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                for (int t = 0; t < high.TriangleCount; t++)
                {
                    if (!highData.Valid[t]) continue;
                    string name = BaseName(NameOf(high, t));
                    if (!groups.TryGetValue(name, out var list)) groups.Add(name, list = new List<int>());
                    list.Add(t);
                }
                var built = new Dictionary<string, MeshRayBvh>(StringComparer.OrdinalIgnoreCase);
                foreach (int t in receivers)
                {
                    string name = BaseName(NameOf(low, t));
                    if (!built.TryGetValue(name, out var bvh)) { bvh = groups.TryGetValue(name, out var list) ? new MeshRayBvh(high.Corners, list) : null; built.Add(name, bvh); }
                    TargetOf[t] = bvh;
                }
            }
            static string NameOf(MeshBakeInput input, int t) => input.RendererNames != null && input.Renderers[t] < input.RendererNames.Length ? input.RendererNames[input.Renderers[t]] ?? "" : "";
        }
        /// <summary>名前での対応づけの名前: 末尾の "_low" / "_high"（大文字小文字を区別しない）を外したもの（Substance の決まりと同じ）。</summary>
        public static string BaseName(string name)
        {
            name = (name ?? "").Trim();
            foreach (var suffix in new[] { "_low", "_high" })
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - suffix.Length);
            return name;
        }

        /// <summary>行ごとの作業領域（スレッドごとに 1 つ）。サブサンプルの持ち主は [サブサンプルの行][x][サブサンプルの列] の順。</summary>
        sealed class Scratch
        {
            public readonly int[] Owner; public readonly double[] B1, B2; public readonly bool[] Interior, Overlap;
            public readonly int[] Stack = new int[MeshRayBvh.StackSize]; public readonly double[] StackT = new double[MeshRayBvh.StackSize];
            public readonly double[] Sum = new double[Job.SumSize];
            public readonly int[] Ids = new int[MeshBakeSettings.MaxAntialiasing * MeshBakeSettings.MaxAntialiasing];
            public long Rays, Projected, Missed;
            /// <summary>GPU に回すとき、Sample が作ったレイの仕事（Row が受け取る）。</summary>
            public MeshBakeRayJob Pending; public bool HasPending;
            public Scratch(int width, int n)
            {
                int size = width * n * n;
                Owner = new int[size]; B1 = new double[size]; B2 = new double[size]; Interior = new bool[size]; Overlap = new bool[size];
            }
        }

        /// <summary>焼き込む三角形を UV のテクセル空間に置き、行の帯（8 行ずつ）ごとに三角形の番号を引けるようにする。範囲は
        /// サブサンプルの中心（x + (i + 0.5) / n）が入りうるテクセル。</summary>
        sealed class UvRaster
        {
            public readonly int[] Original;                // 焼き込む三角形 → 入力の三角形
            public readonly double[] Affine;               // 6 個ずつ: u0, v0, m00, m01, m10, m11（テクセル座標 → 重心座標 b1, b2）
            public readonly int[] X0, X1, Y0, Y1;
            public readonly int[] BandStart, BandItems;
            public UvRaster(MeshBakeInput input, List<int> receivers, int width, int height, int samples, long remainingBytes)
            {
                int n = receivers.Count; var uvs = input.Uvs;
                Original = receivers.ToArray(); Affine = new double[n * 6];
                X0 = new int[n]; X1 = new int[n]; Y0 = new int[n]; Y1 = new int[n];
                int bands = (height + BandRows - 1) / BandRows;
                var bandCount = new int[bands + 1];
                double first = 0.5 / samples, last = 1 - 0.5 / samples; // テクセルの中のサブサンプルの中心の範囲
                for (int r = 0; r < n; r++)
                {
                    int k = Original[r] * 6;
                    double ax = uvs[k] * (double)width, ay = uvs[k + 1] * (double)height, bx = uvs[k + 2] * (double)width, by = uvs[k + 3] * (double)height, cx = uvs[k + 4] * (double)width, cy = uvs[k + 5] * (double)height;
                    double m0 = bx - ax, m1 = cx - ax, m2 = by - ay, m3 = cy - ay, det = m0 * m3 - m1 * m2, inv = 1 / det;
                    int o = r * 6;
                    Affine[o] = ax; Affine[o + 1] = ay; Affine[o + 2] = m3 * inv; Affine[o + 3] = -m1 * inv; Affine[o + 4] = -m2 * inv; Affine[o + 5] = m0 * inv;
                    X0[r] = Math.Max(0, (int)Math.Ceiling(Math.Min(ax, Math.Min(bx, cx)) - last - 1e-9));
                    X1[r] = Math.Min(width - 1, (int)Math.Floor(Math.Max(ax, Math.Max(bx, cx)) - first + 1e-9));
                    Y0[r] = Math.Max(0, (int)Math.Ceiling(Math.Min(ay, Math.Min(by, cy)) - last - 1e-9));
                    Y1[r] = Math.Min(height - 1, (int)Math.Floor(Math.Max(ay, Math.Max(by, cy)) - first + 1e-9));
                    if (X1[r] < X0[r] || Y1[r] < Y0[r]) { Y0[r] = 1; Y1[r] = 0; continue; }
                    for (int band = Y0[r] / BandRows; band <= Y1[r] / BandRows; band++) bandCount[band + 1]++;
                }
                long items = 0;
                for (int b = 0; b < bands; b++) items += bandCount[b + 1];
                // 見積もりは 1 三角形 2 帯。大きな三角形が多いと帯の参照が増えるので、割り当てる前にもう一度確かめる
                if (items > int.MaxValue / 2 || (items - 2L * n) * 4 > remainingBytes)
                    throw new MeshBakeRefusedException("The UV layout needs " + items + " row-band references (many large triangles), over the bake memory budget. Lower the resolution or raise the budget.");
                BandStart = new int[bands + 1];
                for (int b = 0; b < bands; b++) BandStart[b + 1] = BandStart[b] + bandCount[b + 1];
                BandItems = new int[BandStart[bands]];
                var fill = (int[])BandStart.Clone();
                for (int r = 0; r < n; r++)
                {
                    if (Y1[r] < Y0[r]) continue;
                    for (int band = Y0[r] / BandRows; band <= Y1[r] / BandRows; band++) BandItems[fill[band]++] = r; // 帯の中は番号の小さい順
                }
            }
        }

        /// <summary>1 回のベイクの共有状態（読むだけの入力と、テクセルごとに 1 回だけ書く出力）。</summary>
        sealed class Job
        {
            // サブサンプルの和の並び
            const int SNormal = 0, SPosition = 3, SAo = 6, SCurvature = 7, SThickness = 8, STangent = 9, SHeight = 12, SBent = 13, SOpacity = 16;
            public const int SumSize = 17;
            readonly MeshBakeInput input; readonly MeshBakeSettings settings; readonly UvRaster raster;
            readonly SurfaceData low, high; readonly Projection projection; readonly LowFrames frames; readonly IdTable ids;
            readonly Control control;
            readonly int width, height, samples;
            public readonly byte[] Coverage;
            public readonly ushort[][] Outputs = new ushort[KindCount][];
            readonly bool wantNormal, wantPosition, wantAo, wantBent, wantCurvature, wantThickness, wantTangent, wantHeight, wantId, wantOpacity;
            readonly double rayOffset, aoMax, thicknessMax, frontal, rear, heightRange;
            public readonly double[] BoundsMin, BoundsMax; readonly double[] boundsScale;
            // 方向の点列（Hammersley）: u1 と、方位角の cos/sin
            readonly double[] aoU, aoCos, aoSin, thU, thCos, thSin;
            readonly double aoCos2, thCos2;
            readonly MeshRayBvh[] scenes;
            public long Rays, Projected, Missed;
            // GPU に回すときの行のまとまりの状態（テクセルの和・ID・覆うサブサンプルの数と、サンプルごとのレイの仕事）
            bool deferred; public int BatchRows; int batchY0;
            double[] batchSums; int[] batchIds, batchIdCount, batchCovered; bool[] batchOverlap; MeshBakeRayJob[] batchJobs; bool[] batchJobValid;
            MeshBakeRayJob[] compactJobs; MeshBakeRayResult[] compactResults; int[] compactSlot; readonly double[] writeSum = new double[SumSize];

            public Job(MeshBakeInput input, MeshBakeSettings settings, UvRaster raster, SurfaceData low, SurfaceData high, Projection projection, LowFrames frames, IdTable ids, Control control)
            {
                this.input = input; this.settings = settings; this.raster = raster; this.low = low; this.high = high; this.projection = projection; this.frames = frames; this.ids = ids; this.control = control;
                width = settings.Width; height = settings.Height; samples = settings.Antialiasing;
                long texels = (long)width * height;
                Coverage = new byte[texels];
                foreach (var kind in settings.Maps) Outputs[(int)kind] = new ushort[texels * BakedMeshMap.ChannelCount(kind)];
                wantNormal = settings.Includes(MeshMapKind.WorldNormal); wantPosition = settings.Includes(MeshMapKind.Position); wantAo = settings.Includes(MeshMapKind.AmbientOcclusion);
                wantBent = settings.Includes(MeshMapKind.BentNormal); wantCurvature = settings.Includes(MeshMapKind.Curvature); wantThickness = settings.Includes(MeshMapKind.Thickness);
                wantTangent = settings.Includes(MeshMapKind.TangentNormal); wantHeight = settings.Includes(MeshMapKind.Height); wantId = settings.Includes(MeshMapKind.Id); wantOpacity = settings.Includes(MeshMapKind.Opacity);
                double diagonal = input.Diagonal;
                rayOffset = RayOffset * diagonal;
                aoMax = settings.AoMaxDistance * diagonal; thicknessMax = settings.ThicknessMaxDistance * diagonal;
                frontal = settings.ReferenceFrontal * diagonal; rear = settings.ReferenceRear * diagonal; heightRange = Math.Max(frontal, rear);
                // 位置の正規化: 低ポリと高ポリを合わせた境界箱
                BoundsMin = input.BoundsMin; BoundsMax = input.BoundsMax;
                if (high != null)
                {
                    var hmin = high.Input.BoundsMin; var hmax = high.Input.BoundsMax;
                    for (int a = 0; a < 3; a++) { BoundsMin[a] = Math.Min(BoundsMin[a], hmin[a]); BoundsMax[a] = Math.Max(BoundsMax[a], hmax[a]); }
                }
                boundsScale = new double[3];
                for (int a = 0; a < 3; a++) boundsScale[a] = BoundsMax[a] - BoundsMin[a] > 1e-12 * diagonal ? 1 / (BoundsMax[a] - BoundsMin[a]) : 0;
                Sequence(settings.AoSamples, out aoU, out aoCos, out aoSin);
                Sequence(settings.ThicknessSamples, out thU, out thCos, out thSin);
                double aoHalf = settings.AoSpreadDegrees * 0.5 * Math.PI / 180, thHalf = settings.ThicknessSpreadDegrees * 0.5 * Math.PI / 180;
                aoCos2 = Math.Cos(aoHalf) * Math.Cos(aoHalf); thCos2 = Math.Cos(thHalf) * Math.Cos(thHalf);
                if (settings.AoSpreadDegrees >= 180) aoCos2 = 0; if (settings.ThicknessSpreadDegrees >= 180) thCos2 = 0;
                scenes = new[] { low.Occluders, high?.Occluders };
            }

            /// <summary>GPU などへ渡すシーン（BVH と方向の点列）。</summary>
            public MeshBakeRayScene Scene()
            {
                int count = high != null ? 2 : 1;
                var scene = new MeshBakeRayScene
                {
                    NodeBounds = new float[count][], NodeFirst = new int[count][], NodeCount = new int[count][], Triangles = new float[count][], TriangleOriginal = new int[count][],
                    AoU = aoU, AoCos = aoCos, AoSin = aoSin, ThicknessU = thU, ThicknessCos = thCos, ThicknessSin = thSin, AoCos2 = aoCos2, ThicknessCos2 = thCos2,
                    AoMax = aoMax, ThicknessMax = thicknessMax, RayOffset = rayOffset, GrazingLimit = MeshBaker.GrazingLimit,
                    AoAnyHit = settings.AoFalloff == MeshOcclusionFalloff.None, AoIgnoreBackfaces = settings.AoIgnoreBackfaces,
                    WantAo = wantAo || wantBent, WantThickness = wantThickness,
                };
                for (int i = 0; i < count; i++)
                {
                    scenes[i].Flatten(out var bounds, out var first, out var nodeCount, out var triangles, out var original);
                    scene.NodeBounds[i] = bounds; scene.NodeFirst[i] = first; scene.NodeCount[i] = nodeCount; scene.Triangles[i] = triangles; scene.TriangleOriginal[i] = original;
                }
                return scene;
            }
            /// <summary>レイを GPU に回す形にする（rows 行ずつ）。</summary>
            public void Defer(int rows)
            {
                deferred = true; BatchRows = rows;
                int texels = rows * width, jobs = texels * samples * samples, perTexel = samples * samples;
                batchSums = new double[texels * SumSize]; batchIds = new int[texels * perTexel]; batchIdCount = new int[texels]; batchCovered = new int[texels]; batchOverlap = new bool[texels];
                batchJobs = new MeshBakeRayJob[jobs]; batchJobValid = new bool[jobs];
                compactJobs = new MeshBakeRayJob[jobs]; compactResults = new MeshBakeRayResult[jobs]; compactSlot = new int[jobs];
            }
            public void BeginBatch(int y0)
            {
                if (!deferred) return;
                batchY0 = y0;
                Array.Clear(batchCovered, 0, batchCovered.Length); Array.Clear(batchJobValid, 0, batchJobValid.Length);
            }
            /// <summary>まとまりのレイの仕事を並び順に詰めて処理させ、テクセルの和に足して書く。</summary>
            public void FinishBatch(int y0, int y1, IMeshBakeRayTracer tracer)
            {
                int count = 0, perTexel = samples * samples, texels = (y1 - y0) * width;
                for (int i = 0; i < texels * perTexel; i++)
                    if (batchJobValid[i]) { compactJobs[count] = batchJobs[i]; compactSlot[count] = i / perTexel; count++; }
                if (count > 0) tracer.Trace(compactJobs, count, compactResults);
                for (int j = 0; j < count; j++) { AddRays(batchSums, compactSlot[j] * SumSize, ref compactResults[j]); Rays += compactResults[j].Rays; }
                for (int slot = 0; slot < texels; slot++)
                {
                    if (batchCovered[slot] == 0) continue;
                    int y = y0 + slot / width, x = slot % width;
                    Write(y * width + x, batchCovered[slot], batchSums, slot * SumSize, batchIds, slot * perTexel, batchIdCount[slot], batchOverlap[slot]);
                }
            }
            void AddRays(double[] sum, int at, ref MeshBakeRayResult r)
            {
                sum[at + SAo] += r.Ao; sum[at + SThickness] += r.Thickness;
                sum[at + SBent] += r.BentX; sum[at + SBent + 1] += r.BentY; sum[at + SBent + 2] += r.BentZ;
            }
            static void Sequence(int count, out double[] u, out double[] cos, out double[] sin)
            {
                u = new double[count]; cos = new double[count]; sin = new double[count];
                for (int i = 0; i < count; i++)
                {
                    u[i] = (i + 0.5) / count;
                    uint bits = (uint)i; bits = (bits << 16) | (bits >> 16); bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
                    bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2); bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
                    bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
                    double phi = 2 * Math.PI * (bits * 2.3283064365386963e-10);
                    cos[i] = Math.Cos(phi); sin[i] = Math.Sin(phi);
                }
            }
            static uint Hash(uint x) { x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16; return x; }
            static ushort Quantize(double value) => value <= 0 ? (ushort)0 : value >= 1 ? (ushort)65535 : (ushort)(value * 65535 + 0.5);

            public void Row(int y, Scratch s)
            {
                if (control.ShouldStop()) return;
                int n = samples, size = width * n * n;
                var owner = s.Owner;
                for (int i = 0; i < size; i++) { owner[i] = -1; s.Overlap[i] = false; }
                int band = y / BandRows;
                bool any = false;
                for (int j = 0; j < n; j++)
                {
                    double py = y + (j + 0.5) / n;
                    for (int item = raster.BandStart[band]; item < raster.BandStart[band + 1]; item++)
                    {
                        int r = raster.BandItems[item];
                        if (y < raster.Y0[r] || y > raster.Y1[r]) continue;
                        int o = r * 6; double u0 = raster.Affine[o], v0 = raster.Affine[o + 1], m00 = raster.Affine[o + 2], m01 = raster.Affine[o + 3], m10 = raster.Affine[o + 4], m11 = raster.Affine[o + 5];
                        double dy = py - v0, c1y = m01 * dy, c2y = m11 * dy;
                        for (int x = raster.X0[r]; x <= raster.X1[r]; x++)
                            for (int i = 0; i < n; i++)
                            {
                                double dx = x + (i + 0.5) / n - u0, b1 = m00 * dx + c1y, b2 = m10 * dx + c2y, b0 = 1 - b1 - b2;
                                double min = Math.Min(b0, Math.Min(b1, b2));
                                if (min < -1e-9) continue;
                                bool interior = min > 1e-6; int idx = (j * width + x) * n + i;
                                if (owner[idx] < 0) { owner[idx] = r; s.B1[idx] = b1; s.B2[idx] = b2; s.Interior[idx] = interior; any = true; }
                                else if (interior && s.Interior[idx]) s.Overlap[idx] = true;
                            }
                    }
                }
                if (!any) return;
                var sum = s.Sum; int perTexel = n * n;
                for (int x = 0; x < width; x++)
                {
                    int covered = 0, ids = 0; bool overlap = false;
                    int slot = (y - batchY0) * width + x;
                    for (int j = 0; j < n; j++)
                        for (int i = 0; i < n; i++)
                        {
                            int idx = (j * width + x) * n + i, r = owner[idx];
                            if (r < 0) continue;
                            if (covered == 0) Array.Clear(sum, 0, SumSize);
                            covered++; overlap |= s.Overlap[idx];
                            int id = Sample(x, y, j * n + i, r, s.B1[idx], s.B2[idx], s);
                            if (wantId) s.Ids[ids++] = id;
                            if (s.HasPending) { batchJobs[slot * perTexel + j * n + i] = s.Pending; batchJobValid[slot * perTexel + j * n + i] = true; s.HasPending = false; }
                        }
                    if (covered == 0) continue;
                    if (deferred)
                    {
                        Array.Copy(sum, 0, batchSums, slot * SumSize, SumSize); Array.Copy(s.Ids, 0, batchIds, slot * perTexel, ids);
                        batchIdCount[slot] = ids; batchCovered[slot] = covered; batchOverlap[slot] = overlap;
                        continue;
                    }
                    Write(y * width + x, covered, sum, 0, s.Ids, 0, ids, overlap);
                }
            }

            /// <summary>サブサンプル 1 つ。和に足し、ID の色（0xRRGGBB）を返す。</summary>
            int Sample(int x, int y, int sub, int r, double b1, double b2, Scratch s)
            {
                var sum = s.Sum;
                int t = raster.Original[r];
                low.Point(t, b1, b2, out double px, out double py, out double pz, out double gx, out double gy, out double gz, out double nx, out double ny, out double nz);
                var surface = low; int st = t;
                double sx = px, sy = py, sz = pz, sgx = gx, sgy = gy, sgz = gz, snx = nx, sny = ny, snz = nz;
                bool hit = false; double rise = 0, sb1 = b1, sb2 = b2;
                if (projection != null)
                {
                    s.Projected++;
                    var target = projection.TargetOf[t];
                    if (target != null)
                    {
                        // ケージの向き（三角形の中で補間して正規化。0 なら頂点法線）
                        var cage = projection.Cage; int k = t * 9; double b0 = 1 - b1 - b2;
                        double cx = b0 * cage[k] + b1 * cage[k + 3] + b2 * cage[k + 6], cy = b0 * cage[k + 1] + b1 * cage[k + 4] + b2 * cage[k + 7], cz = b0 * cage[k + 2] + b1 * cage[k + 5] + b2 * cage[k + 8];
                        double cl = Math.Sqrt(cx * cx + cy * cy + cz * cz);
                        if (cl > 1e-9) { cx /= cl; cy /= cl; cz /= cl; } else { cx = nx; cy = ny; cz = nz; }
                        double distance = target.Trace(px + cx * frontal, py + cy * frontal, pz + cz * frontal, -cx, -cy, -cz, frontal + rear, -1, false, false, s.Stack, s.StackT, out int ht, out double hu, out double hv);
                        if (ht >= 0 && distance < double.PositiveInfinity)
                        {
                            hit = true; surface = high; st = ht; rise = frontal - distance; sb1 = hu; sb2 = hv;
                            high.Point(ht, hu, hv, out sx, out sy, out sz, out sgx, out sgy, out sgz, out snx, out sny, out snz);
                        }
                    }
                    if (!hit) s.Missed++;
                }
                if (wantNormal) { sum[SNormal] += snx; sum[SNormal + 1] += sny; sum[SNormal + 2] += snz; }
                if (wantPosition)
                {
                    sum[SPosition] += boundsScale[0] > 0 ? (sx - BoundsMin[0]) * boundsScale[0] : 0.5;
                    sum[SPosition + 1] += boundsScale[1] > 0 ? (sy - BoundsMin[1]) * boundsScale[1] : 0.5;
                    sum[SPosition + 2] += boundsScale[2] > 0 ? (sz - BoundsMin[2]) * boundsScale[2] : 0.5;
                }
                if (wantCurvature) sum[SCurvature] += 0.5 + 0.5 * surface.Curvature.Evaluate(sx, sy, sz, surface.Curvature.TriangleComponent[st]);
                if (wantTangent)
                {
                    if (hit) TangentSpace(t, b1, b2, snx, sny, snz, sum);
                    else sum[STangent + 2] += 1;
                }
                if (wantHeight) sum[SHeight] += 0.5 + 0.5 * Math.Max(-1, Math.Min(1, rise / heightRange));
                if (wantOpacity) sum[SOpacity] += projection == null || hit ? 1 : 0;
                if (wantAo || wantBent || wantThickness)
                {
                    // サブサンプルごとの決まった回転（並列の分け方に依らない。1×1 なら版 1 と同じ乱数）
                    uint h = Hash((uint)x * 0x9E3779B1u ^ Hash((uint)y + 0x68E31DA4u) ^ (uint)sub * 0x85EBCA6Bu);
                    double shift = (h >> 8) * (1.0 / 16777216); uint h2 = Hash(h ^ 0xB5297A4Du);
                    double angle = 2 * Math.PI * ((h2 >> 8) * (1.0 / 16777216));
                    var job = new MeshBakeRayJob { Ox = sx, Oy = sy, Oz = sz, Nx = snx, Ny = sny, Nz = snz, Gx = sgx, Gy = sgy, Gz = sgz, Shift = shift, Rc = Math.Cos(angle), Rs = Math.Sin(angle), Ignore = st, Scene = surface == high ? 1 : 0 };
                    if (deferred) { s.Pending = job; s.HasPending = true; }
                    else { TraceJob(ref job, s.Stack, s.StackT, out var result); s.Rays += result.Rays; AddRays(sum, 0, ref result); }
                }
                return wantId ? IdColor(surface, st, t) : 0;
            }

            /// <summary>1 サンプル分の AO・ベントノーマル・厚みのレイ（CPU の基準。GPU の計算シェーダーは同じ式を float で行う）。</summary>
            public void TraceJob(ref MeshBakeRayJob job, int[] stack, double[] stackT, out MeshBakeRayResult result)
            {
                result = default;
                double snx = job.Nx, sny = job.Ny, snz = job.Nz, sgx = job.Gx, sgy = job.Gy, sgz = job.Gz, rc = job.Rc, rs = job.Rs, shift = job.Shift;
                // 法線のまわりの正規直交基底（Duff ほか 2017）
                double sign = snz >= 0 ? 1.0 : -1.0, a = -1 / (sign + snz), bb = snx * sny * a;
                double tx = 1 + sign * snx * snx * a, ty = sign * bb, tz = -sign * snx;
                double qx = bb, qy = sign + sny * sny * a, qz = -sny;
                var bvh = scenes[job.Scene]; int st = job.Ignore;
                if (wantAo || wantBent)
                {
                    double ox = job.Ox + sgx * rayOffset, oy = job.Oy + sgy * rayOffset, oz = job.Oz + sgz * rayOffset;
                    double occlusion = 0, bx = 0, by = 0, bz = 0; int validRays = 0; bool any1 = settings.AoFalloff == MeshOcclusionFalloff.None;
                    for (int j = 0; j < aoU.Length; j++)
                    {
                        double u1 = aoU[j] + shift; if (u1 >= 1) u1 -= 1;
                        double cos2 = 1 - u1 * (1 - aoCos2), cosT = Math.Sqrt(cos2), sinT = cos2 < 1 ? Math.Sqrt(1 - cos2) : 0;
                        double cp = aoCos[j] * rc - aoSin[j] * rs, sp = aoSin[j] * rc + aoCos[j] * rs;
                        double lx = sinT * cp, ly = sinT * sp;
                        double dx = tx * lx + qx * ly + snx * cosT, dy = ty * lx + qy * ly + sny * cosT, dz = tz * lx + qz * ly + snz * cosT;
                        if (dx * sgx + dy * sgy + dz * sgz <= GrazingLimit) continue; // 面の下へ向かうレイは環境を見ない
                        validRays++;
                        double distance = bvh.Trace(ox, oy, oz, dx, dy, dz, aoMax, st, any1, settings.AoIgnoreBackfaces, stack, stackT, out _, out _, out _);
                        if (distance < double.PositiveInfinity) occlusion += any1 ? 1 : 1 - distance / aoMax;
                        else { bx += dx; by += dy; bz += dz; }
                    }
                    result.Rays += validRays;
                    result.Ao = validRays > 0 ? 1 - occlusion / validRays : 1;
                    double bl = Math.Sqrt(bx * bx + by * by + bz * bz);
                    if (bl > 1e-12) { result.BentX = bx / bl; result.BentY = by / bl; result.BentZ = bz / bl; }
                    else { result.BentX = snx; result.BentY = sny; result.BentZ = snz; }
                }
                if (wantThickness)
                {
                    double ox = job.Ox - sgx * rayOffset, oy = job.Oy - sgy * rayOffset, oz = job.Oz - sgz * rayOffset;
                    double distance = 0; int validRays = 0;
                    for (int j = 0; j < thU.Length; j++)
                    {
                        double u1 = thU[j] + shift; if (u1 >= 1) u1 -= 1;
                        double cos2 = 1 - u1 * (1 - thCos2), cosT = Math.Sqrt(cos2), sinT = cos2 < 1 ? Math.Sqrt(1 - cos2) : 0;
                        double cp = thCos[j] * rc - thSin[j] * rs, sp = thSin[j] * rc + thCos[j] * rs;
                        double lx = sinT * cp, ly = sinT * sp;
                        // 内向き: 法線の反対側の半球
                        double dx = tx * lx + qx * ly - snx * cosT, dy = ty * lx + qy * ly - sny * cosT, dz = tz * lx + qz * ly - snz * cosT;
                        if (-(dx * sgx + dy * sgy + dz * sgz) <= GrazingLimit) continue;
                        validRays++;
                        double hitDistance = bvh.Trace(ox, oy, oz, dx, dy, dz, thicknessMax, st, false, false, stack, stackT, out _, out _, out _);
                        distance += hitDistance < thicknessMax ? hitDistance : thicknessMax;
                    }
                    result.Rays += validRays;
                    result.Thickness = validRays > 0 ? distance / validRays / thicknessMax : 1;
                }
            }

            /// <summary>高ポリの法線 (mx, my, mz) を、低ポリの三角形 t の補間した接線 T・従法線 B・法線 N で表す。Unity は
            /// normalize(T·x + B·y + N·z) で読むので、その一次式を解く（T・B・N が直交しない所でも読んだ向きが合う）。</summary>
            void TangentSpace(int t, double b1, double b2, double mx, double my, double mz, double[] sum)
            {
                var T = frames.Tangents; var B = frames.Bitangents; var N = frames.Normals; int k = t * 9; double b0 = 1 - b1 - b2;
                double tx = b0 * T[k] + b1 * T[k + 3] + b2 * T[k + 6], ty = b0 * T[k + 1] + b1 * T[k + 4] + b2 * T[k + 7], tz = b0 * T[k + 2] + b1 * T[k + 5] + b2 * T[k + 8];
                double bx = b0 * B[k] + b1 * B[k + 3] + b2 * B[k + 6], by = b0 * B[k + 1] + b1 * B[k + 4] + b2 * B[k + 7], bz = b0 * B[k + 2] + b1 * B[k + 5] + b2 * B[k + 8];
                double nx = b0 * N[k] + b1 * N[k + 3] + b2 * N[k + 6], ny = b0 * N[k + 1] + b1 * N[k + 4] + b2 * N[k + 7], nz = b0 * N[k + 2] + b1 * N[k + 5] + b2 * N[k + 8];
                // det(T, B, N) = T·(B×N)
                double bnx = by * nz - bz * ny, bny = bz * nx - bx * nz, bnz = bx * ny - by * nx;
                double det = tx * bnx + ty * bny + tz * bnz, a, b, c;
                if (Math.Abs(det) > 1e-12)
                {
                    a = (mx * bnx + my * bny + mz * bnz) / det;                                                  // det(M, B, N)
                    b = (tx * (my * nz - mz * ny) + ty * (mz * nx - mx * nz) + tz * (mx * ny - my * nx)) / det;    // det(T, M, N)
                    c = (tx * (by * mz - bz * my) + ty * (bz * mx - bx * mz) + tz * (bx * my - by * mx)) / det;    // det(T, B, M)
                }
                else { a = mx * tx + my * ty + mz * tz; b = mx * bx + my * by + mz * bz; c = mx * nx + my * ny + mz * nz; }
                double l = Math.Sqrt(a * a + b * b + c * c);
                if (l > 1e-12) { sum[STangent] += a / l; sum[STangent + 1] += b / l; sum[STangent + 2] += c / l; }
                else sum[STangent + 2] += 1;
            }

            /// <summary>ID の色（0xRRGGBB）。スロット・メッシュ・塊・頂点カラーは面のある方（高ポリに当たれば高ポリ）の三角形の色、UV アイランドは
            /// 低ポリの島（<see cref="IdTable"/>）。頂点カラーの無いメッシュは白。</summary>
            int IdColor(SurfaceData surface, int st, int t)
            {
                if (ids.Manual != null && ids.Manual[t] >= 0) return ids.Manual[t];
                if (settings.IdSource == MeshIdSource.UvIsland) return ids.Low[t];
                var table = surface == low ? ids.Low : ids.High;
                return table == null ? 0xFFFFFF : table[st];
            }

            void Write(int i, int covered, double[] sumArray, int at, int[] idArray, int idAt, int ids, bool overlap)
            {
                double inv = 1.0 / covered;
                double[] sum = sumArray;
                if (at != 0) { sum = writeSum; Array.Copy(sumArray, at, sum, 0, SumSize); } // GPU の後の書き込みは 1 スレッド
                Coverage[i] = (byte)(overlap ? MeshTexelCoverage.Overlap : MeshTexelCoverage.Covered);
                if (wantNormal) Unit(Outputs[(int)MeshMapKind.WorldNormal], i, sum, SNormal);
                if (wantPosition) { var o = Outputs[(int)MeshMapKind.Position]; for (int c = 0; c < 3; c++) o[i * 3 + c] = Quantize(sum[SPosition + c] * inv); }
                if (wantAo) Outputs[(int)MeshMapKind.AmbientOcclusion][i] = Quantize(sum[SAo] * inv);
                if (wantCurvature) Outputs[(int)MeshMapKind.Curvature][i] = Quantize(sum[SCurvature] * inv);
                if (wantThickness) Outputs[(int)MeshMapKind.Thickness][i] = Quantize(sum[SThickness] * inv);
                if (wantTangent) Unit(Outputs[(int)MeshMapKind.TangentNormal], i, sum, STangent);
                if (wantHeight) Outputs[(int)MeshMapKind.Height][i] = Quantize(sum[SHeight] * inv);
                if (wantBent) Unit(Outputs[(int)MeshMapKind.BentNormal], i, sum, SBent);
                if (wantOpacity) Outputs[(int)MeshMapKind.Opacity][i] = Quantize(sum[SOpacity] * inv);
                if (wantId)
                {
                    // 多数決（色を混ぜると、どの ID でもない色になって選択に使えない）。同数なら先のサブサンプル
                    int best = idArray[idAt], bestCount = 0;
                    for (int a = 0; a < ids; a++)
                    {
                        int count = 0; for (int b = 0; b < ids; b++) if (idArray[idAt + b] == idArray[idAt + a]) count++;
                        if (count > bestCount) { bestCount = count; best = idArray[idAt + a]; }
                    }
                    var o = Outputs[(int)MeshMapKind.Id];
                    o[i * 3] = (ushort)((best >> 16 & 255) * 257); o[i * 3 + 1] = (ushort)((best >> 8 & 255) * 257); o[i * 3 + 2] = (ushort)((best & 255) * 257);
                }
            }
            /// <summary>単位ベクトルの和を正規化して n×0.5+0.5 で書く（和が 0 なら +Z）。</summary>
            static void Unit(ushort[] output, int i, double[] sum, int at)
            {
                double x = sum[at], y = sum[at + 1], z = sum[at + 2], l = Math.Sqrt(x * x + y * y + z * z);
                if (l > 1e-12) { x /= l; y /= l; z /= l; } else { x = 0; y = 0; z = 1; }
                output[i * 3] = Quantize(x * 0.5 + 0.5); output[i * 3 + 1] = Quantize(y * 0.5 + 0.5); output[i * 3 + 2] = Quantize(z * 0.5 + 0.5);
            }

            /// <summary>余白。空のテクセルを 8 近傍で 1 段ずつ広げ、前の段までに決まった近傍が指す元のテクセルのうち、いちばん近いもの
            /// （同じ距離なら番号の小さいもの）を写す。段ごとに並列で、前の段の結果だけを読む（決まった結果）。</summary>
            public bool Pad(int padding, ParallelOptions options, Func<double, string, bool> progress)
            {
                long texels = Coverage.LongLength;
                var source = new int[texels]; var pass = new byte[texels];
                Parallel.For(0, height, options, y =>
                {
                    for (int x = 0, i = y * width; x < width; x++, i++)
                    { bool filled = Coverage[i] != (byte)MeshTexelCoverage.Empty; source[i] = filled ? i : -1; pass[i] = filled ? (byte)0 : (byte)255; }
                });
                for (int p = 1; p <= padding; p++)
                {
                    int changed = 0; byte step = (byte)p;
                    Parallel.For(0, height, options, () => 0, (y, state, local) =>
                    {
                        for (int x = 0, i = y * width; x < width; x++, i++)
                        {
                            if (pass[i] != 255) continue;
                            int best = -1; long bestDistance = long.MaxValue;
                            for (int oy = -1; oy <= 1; oy++)
                            {
                                int ny = y + oy; if ((uint)ny >= (uint)height) continue;
                                for (int ox = -1; ox <= 1; ox++)
                                {
                                    int nx = x + ox; if ((ox == 0 && oy == 0) || (uint)nx >= (uint)width) continue;
                                    int j = ny * width + nx;
                                    if (pass[j] >= step) continue; // この段で決まったもの・空はまだ読まない
                                    int from = source[j]; long fx = from % width - x, fy = from / width - y, d = fx * fx + fy * fy;
                                    if (d < bestDistance || (d == bestDistance && from < best)) { bestDistance = d; best = from; }
                                }
                            }
                            if (best < 0) continue;
                            source[i] = best; pass[i] = step; local++;
                        }
                        return local;
                    }, local => Interlocked.Add(ref changed, local));
                    if (control.ShouldStop()) return false;
                    if (progress != null && !progress(0.9 + 0.1 * p / padding, "Padding")) { control.Cancel(); return false; }
                    if (changed == 0) break;
                }
                Parallel.For(0, height, options, y =>
                {
                    for (int x = 0, i = y * width; x < width; x++, i++)
                    {
                        if (pass[i] == 0 || pass[i] == 255) continue;
                        int from = source[i];
                        Coverage[i] = (byte)MeshTexelCoverage.Padding;
                        for (int kind = 0; kind < KindCount; kind++)
                        {
                            var o = Outputs[kind]; if (o == null) continue;
                            int channels = BakedMeshMap.ChannelCount((MeshMapKind)kind);
                            for (int c = 0; c < channels; c++) o[i * channels + c] = o[from * channels + c];
                        }
                    }
                });
                return true;
            }
        }
    }
}
