using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Yozolab.YoluPainter.Core.MeshMaps
{
    /// <summary>
    /// mesh map のベイク（純 C#、CPU。行ごとに Parallel.For）。
    /// <list type="number">
    /// <item>UV 空間でテクセルの中心を三角形に当てる（中心が三角形の内側か辺の上のテクセルだけ。端の部分的な覆いは数えない）。
    /// UV が重なるテクセルは添字の小さい三角形の値にし、Overlap と印を付けて数える（重なった面を別々の値にはできない: 仕様 12 章）。</item>
    /// <item>テクセルごとに位置・面の法線・頂点法線を重心座標で補間し、法線・位置・曲率は式で、AO と厚みは BVH にレイを飛ばして求める。
    /// レイの向きは Hammersley の点列をテクセルごとの決まった乱数で回したもの（同じ入力なら並列の分け方に依らず同じバイト列）。</item>
    /// <item>余白: 島の外の空のテクセルを 8 近傍で 1 段ずつ広げ、いちばん近い島のテクセルの値を写す。島の本体には書かない。
    /// どの段にも届かない所は Empty のまま値 0（データを作らない）。</item>
    /// </list>
    /// メモリは割り当てる前に見積もって予算を超えれば断る。取消・時間切れは途中の結果を返さない（呼び出し側の持つマップは変わらない）。
    /// 進み具合のコールバックは呼んだスレッドで、行のまとまりごとに呼ぶ（false を返すと取消）。
    /// </summary>
    public static class MeshBaker
    {
        /// <summary>結果を変える変更をしたら上げる（由来に入り、古いマップを見分ける）。</summary>
        public const int EngineVersion = 1;
        /// <summary>空間・基準の姿勢・焼く元（今は自己ベイクだけ。参照メッシュ（ハイポリ）からのベイクは別の値になる: 仕様 6 章）。</summary>
        public const string Space = "SnapshotWorld", Pose = "StaticSnapshot", Source = "Self";
        /// <summary>レイの始点を面から浮かせる量（対角線比）。</summary>
        const double RayOffset = 1e-5;
        /// <summary>面すれすれのレイ（面の法線との内積がこれ以下）は数えない。</summary>
        const double GrazingLimit = 1e-4;
        const int BandRows = 8;

        /// <summary>この条件で焼いたときの由来の鍵（<see cref="MeshMapProvenance.ConditionKey"/>）。キャッシュの照合に使う。</summary>
        public static string ConditionKey(MeshBakeInput input, MeshBakeSettings settings, MeshMapKind kind)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return MeshMapProvenance.ComputeConditionKey(kind, EngineVersion, input.Hash, input.UvChannel, settings.Width, settings.Height, settings.TargetSlot,
                settings.Padding, settings.KindKey(kind), Space, Pose, Source);
        }

        static int Threads(MeshBakeBudget budget) => budget != null && budget.MaxDegreeOfParallelism > 0 ? budget.MaxDegreeOfParallelism : Math.Max(1, Environment.ProcessorCount);
        static bool NeedsRays(MeshBakeSettings s) => s.Includes(MeshMapKind.AmbientOcclusion) || s.Includes(MeshMapKind.Thickness);

        /// <summary>割り当てる量の見積もり（出力・由来・余白の作業・BVH・UV の帯・行の作業領域・曲率の辺の表。曲率の線分は別に確かめる）。</summary>
        public static long EstimateBytes(MeshBakeInput input, MeshBakeSettings settings, MeshBakeBudget budget = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            long texels = (long)settings.Width * settings.Height, triangles = input.TriangleCount;
            long bytes = texels; // 由来
            foreach (var kind in settings.Maps) bytes += texels * BakedMeshMap.ChannelCount(kind) * 2;
            if (settings.Padding > 0) bytes += texels * 5;
            bytes += triangles * (3 * 4 + 1);                             // 面の法線・有効の印
            if (NeedsRays(settings)) bytes += triangles * MeshRayBvh.BytesPerTriangle;
            bytes += triangles * (6 * 8 + 4 * 4 + 4 + 2 * 4);             // UV の三角形と帯の参照（1 つあたり 2 帯と見る）
            bytes += ((long)settings.Height / BandRows + 2) * 4;
            if (settings.Includes(MeshMapKind.Curvature)) bytes += triangles * (3 * 4 + 3 * 32 + 4 + 2 * 40); // 溶接・辺の表・成分
            bytes += (long)Threads(budget) * settings.Width * (4 + 8 + 8 + 1 + 1) + Threads(budget) * 1024L;
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
        /// <exception cref="MeshBakeRefusedException">予算を超える、焼き込む三角形が無い、UV が 0〜1 の外。何も割り当てない。</exception>
        public static MeshBakeResult Bake(MeshBakeInput input, MeshBakeSettings settings, MeshBakeBudget budget = null,
            Func<double, string, bool> progress = null, CancellationToken cancellation = default)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings = settings.Clone(); settings.Validate();
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
            long estimate = EstimateBytes(input, settings, budget);
            report.EstimatedBytes = estimate;
            if (estimate > budget.MaxBytes)
                throw new MeshBakeRefusedException("Baking " + settings.Maps.Length + " map(s) at " + width + "×" + height + " needs about " + (estimate >> 20) + " MiB, over the " + (budget.MaxBytes >> 20) + " MiB budget. Bake fewer maps at once or raise the memory budget (Project Settings > YoluPainter).");
            report.ReceivingTriangles = receivers.Count;
            if (!Report(progress, control, 0, "Preparing")) return Stopped(control, report);

            // 面の法線（面積の無い三角形は使わない）
            var faceNormals = new float[triangles * 3]; var valid = new bool[triangles];
            for (int t = 0; t < triangles; t++)
            {
                int k = t * 9;
                double e1x = corners[k + 3] - corners[k], e1y = corners[k + 4] - corners[k + 1], e1z = corners[k + 5] - corners[k + 2];
                double e2x = corners[k + 6] - corners[k], e2y = corners[k + 7] - corners[k + 1], e2z = corners[k + 8] - corners[k + 2];
                double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
                double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (!(length > 1e-30)) { report.DegenerateTriangles++; continue; }
                valid[t] = true; faceNormals[t * 3] = (float)(nx / length); faceNormals[t * 3 + 1] = (float)(ny / length); faceNormals[t * 3 + 2] = (float)(nz / length);
            }
            MeshRayBvh bvh = null;
            if (NeedsRays(settings))
            {
                var occluders = new List<int>();
                for (int t = 0; t < triangles; t++)
                    if (valid[t] && (settings.Occluders == MeshOccluders.WholeModel || input.Slots[t] == settings.TargetSlot || settings.TargetSlot < 0)) occluders.Add(t);
                bvh = new MeshRayBvh(corners, occluders);
                report.OccluderTriangles = occluders.Count;
            }
            if (!Report(progress, control, 0.02, "Preparing")) return Stopped(control, report);
            MeshCurvatureField curvature = null;
            if (settings.Includes(MeshMapKind.Curvature))
            {
                curvature = new MeshCurvatureField(input, valid, faceNormals, settings.CurvatureRadius * input.Diagonal, budget.MaxBytes - estimate);
                report.BoundaryEdges = curvature.BoundaryEdges; report.NonManifoldEdges = curvature.NonManifoldEdges;
                report.InconsistentWindingEdges = curvature.InconsistentEdges; report.CurvatureSegments = curvature.SegmentCount;
            }
            var raster = new UvRaster(input, receivers, width, height, budget.MaxBytes - estimate);
            report.PrepareSeconds = control.Clock.Elapsed.TotalSeconds;
            if (!Report(progress, control, 0.05, "Baking")) return Stopped(control, report);

            var job = new Job(input, settings, raster, bvh, curvature, faceNormals, control);
            int threads = Threads(budget);
            var options = new ParallelOptions { MaxDegreeOfParallelism = threads };
            int batch = Math.Max(16, Math.Min(256, threads * 4));
            for (int y0 = 0; y0 < height; y0 += batch)
            {
                int y1 = Math.Min(height, y0 + batch);
                Parallel.For(y0, y1, options, () => new Scratch(width), (y, state, scratch) => { job.Row(y, scratch); return scratch; }, scratch => Interlocked.Add(ref job.Rays, scratch.Rays));
                if (control.ShouldStop()) return Stopped(control, report);
                if (!Report(progress, control, 0.05 + 0.85 * y1 / height, "Baking")) return Stopped(control, report);
            }
            report.Rays = job.Rays;
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
            if (report.NonManifoldEdges > 0 || report.InconsistentWindingEdges > 0) report.Diagnostics.Add((report.NonManifoldEdges + report.InconsistentWindingEdges) + " edges are non-manifold or have flipped winding; curvature ignores them.");
            var maps = new List<BakedMeshMap>();
            foreach (var kind in settings.Maps)
            {
                var provenance = new MeshMapProvenance(kind, EngineVersion, input.Hash, input.TopologyHash, input.UvChannel, width, height, settings.TargetSlot, settings.Padding,
                    settings.KindKey(kind), Space, Pose, Source, input.BoundsMin, input.BoundsMax);
                maps.Add(new BakedMeshMap(provenance, job.Output(kind), job.Coverage));
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

        /// <summary>行ごとの作業領域（スレッドごとに 1 つ）。</summary>
        sealed class Scratch
        {
            public readonly int[] Owner; public readonly double[] B1, B2; public readonly bool[] Interior, Overlap;
            public readonly int[] Stack = new int[MeshRayBvh.StackSize]; public readonly double[] StackT = new double[MeshRayBvh.StackSize];
            public long Rays;
            public Scratch(int width) { Owner = new int[width]; B1 = new double[width]; B2 = new double[width]; Interior = new bool[width]; Overlap = new bool[width]; }
        }

        /// <summary>焼き込む三角形を UV のテクセル空間に置き、行の帯（8 行ずつ）ごとに三角形の番号を引けるようにする。</summary>
        sealed class UvRaster
        {
            public readonly int[] Original;                // 焼き込む三角形 → 入力の三角形
            public readonly double[] Affine;               // 6 個ずつ: u0, v0, m00, m01, m10, m11（テクセル座標 → 重心座標 b1, b2）
            public readonly int[] X0, X1, Y0, Y1;
            public readonly int[] BandStart, BandItems;
            public UvRaster(MeshBakeInput input, List<int> receivers, int width, int height, long remainingBytes)
            {
                int n = receivers.Count; var uvs = input.Uvs;
                Original = receivers.ToArray(); Affine = new double[n * 6];
                X0 = new int[n]; X1 = new int[n]; Y0 = new int[n]; Y1 = new int[n];
                int bands = (height + BandRows - 1) / BandRows;
                var bandCount = new int[bands + 1];
                for (int r = 0; r < n; r++)
                {
                    int k = Original[r] * 6;
                    double ax = uvs[k] * (double)width, ay = uvs[k + 1] * (double)height, bx = uvs[k + 2] * (double)width, by = uvs[k + 3] * (double)height, cx = uvs[k + 4] * (double)width, cy = uvs[k + 5] * (double)height;
                    double m0 = bx - ax, m1 = cx - ax, m2 = by - ay, m3 = cy - ay, det = m0 * m3 - m1 * m2, inv = 1 / det;
                    int o = r * 6;
                    Affine[o] = ax; Affine[o + 1] = ay; Affine[o + 2] = m3 * inv; Affine[o + 3] = -m1 * inv; Affine[o + 4] = -m2 * inv; Affine[o + 5] = m0 * inv;
                    // 中心 (x + 0.5) が範囲に入るテクセル
                    X0[r] = Math.Max(0, (int)Math.Ceiling(Math.Min(ax, Math.Min(bx, cx)) - 0.5 - 1e-9));
                    X1[r] = Math.Min(width - 1, (int)Math.Floor(Math.Max(ax, Math.Max(bx, cx)) - 0.5 + 1e-9));
                    Y0[r] = Math.Max(0, (int)Math.Ceiling(Math.Min(ay, Math.Min(by, cy)) - 0.5 - 1e-9));
                    Y1[r] = Math.Min(height - 1, (int)Math.Floor(Math.Max(ay, Math.Max(by, cy)) - 0.5 + 1e-9));
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
            readonly MeshBakeInput input; readonly MeshBakeSettings settings; readonly UvRaster raster; readonly MeshRayBvh bvh; readonly MeshCurvatureField curvature;
            readonly float[] faceNormals; readonly Control control;
            readonly int width, height;
            public readonly byte[] Coverage;
            readonly ushort[] normalOut, positionOut, aoOut, curvatureOut, thicknessOut;
            readonly double rayOffset, aoMax, thicknessMax;
            readonly double[] boundsMin, boundsScale;
            // 方向の点列（Hammersley）: u1 と、方位角の cos/sin
            readonly double[] aoU, aoCos, aoSin, thU, thCos, thSin;
            readonly double aoCos2, thCos2;
            public long Rays;

            public Job(MeshBakeInput input, MeshBakeSettings settings, UvRaster raster, MeshRayBvh bvh, MeshCurvatureField curvature, float[] faceNormals, Control control)
            {
                this.input = input; this.settings = settings; this.raster = raster; this.bvh = bvh; this.curvature = curvature; this.faceNormals = faceNormals; this.control = control;
                width = settings.Width; height = settings.Height;
                long texels = (long)width * height;
                Coverage = new byte[texels];
                if (settings.Includes(MeshMapKind.WorldNormal)) normalOut = new ushort[texels * 3];
                if (settings.Includes(MeshMapKind.Position)) positionOut = new ushort[texels * 3];
                if (settings.Includes(MeshMapKind.AmbientOcclusion)) aoOut = new ushort[texels];
                if (settings.Includes(MeshMapKind.Curvature)) curvatureOut = new ushort[texels];
                if (settings.Includes(MeshMapKind.Thickness)) thicknessOut = new ushort[texels];
                rayOffset = RayOffset * input.Diagonal;
                aoMax = settings.AoMaxDistance * input.Diagonal; thicknessMax = settings.ThicknessMaxDistance * input.Diagonal;
                boundsMin = input.BoundsMin; var max = input.BoundsMax; boundsScale = new double[3];
                for (int a = 0; a < 3; a++) boundsScale[a] = max[a] - boundsMin[a] > 1e-12 * input.Diagonal ? 1 / (max[a] - boundsMin[a]) : 0;
                Sequence(settings.AoSamples, out aoU, out aoCos, out aoSin);
                Sequence(settings.ThicknessSamples, out thU, out thCos, out thSin);
                double aoHalf = settings.AoSpreadDegrees * 0.5 * Math.PI / 180, thHalf = settings.ThicknessSpreadDegrees * 0.5 * Math.PI / 180;
                aoCos2 = Math.Cos(aoHalf) * Math.Cos(aoHalf); thCos2 = Math.Cos(thHalf) * Math.Cos(thHalf);
                if (settings.AoSpreadDegrees >= 180) aoCos2 = 0; if (settings.ThicknessSpreadDegrees >= 180) thCos2 = 0;
            }
            static void Sequence(int count, out double[] u, out double[] cos, out double[] sin)
            {
                u = new double[count]; cos = new double[count]; sin = new double[count];
                for (int i = 0; i < count; i++)
                {
                    u[i] = ((i + 0.5) / count);
                    uint bits = (uint)i; bits = (bits << 16) | (bits >> 16); bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
                    bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2); bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
                    bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
                    double phi = 2 * Math.PI * (bits * 2.3283064365386963e-10);
                    cos[i] = Math.Cos(phi); sin[i] = Math.Sin(phi);
                }
            }
            public ushort[] Output(MeshMapKind kind)
            {
                switch (kind)
                {
                    case MeshMapKind.WorldNormal: return normalOut;
                    case MeshMapKind.Position: return positionOut;
                    case MeshMapKind.AmbientOcclusion: return aoOut;
                    case MeshMapKind.Curvature: return curvatureOut;
                    default: return thicknessOut;
                }
            }
            static uint Hash(uint x) { x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16; return x; }
            static ushort Quantize(double value) => value <= 0 ? (ushort)0 : value >= 1 ? (ushort)65535 : (ushort)(value * 65535 + 0.5);

            public void Row(int y, Scratch s)
            {
                if (control.ShouldStop()) return;
                var owner = s.Owner;
                for (int x = 0; x < width; x++) { owner[x] = -1; s.Overlap[x] = false; }
                int band = y / BandRows; double py = y + 0.5;
                bool any = false;
                for (int item = raster.BandStart[band]; item < raster.BandStart[band + 1]; item++)
                {
                    int r = raster.BandItems[item];
                    if (y < raster.Y0[r] || y > raster.Y1[r]) continue;
                    int o = r * 6; double u0 = raster.Affine[o], v0 = raster.Affine[o + 1], m00 = raster.Affine[o + 2], m01 = raster.Affine[o + 3], m10 = raster.Affine[o + 4], m11 = raster.Affine[o + 5];
                    double dy = py - v0, c1y = m01 * dy, c2y = m11 * dy;
                    for (int x = raster.X0[r]; x <= raster.X1[r]; x++)
                    {
                        double dx = x + 0.5 - u0, b1 = m00 * dx + c1y, b2 = m10 * dx + c2y, b0 = 1 - b1 - b2;
                        double min = Math.Min(b0, Math.Min(b1, b2));
                        if (min < -1e-9) continue;
                        bool interior = min > 1e-6;
                        if (owner[x] < 0) { owner[x] = r; s.B1[x] = b1; s.B2[x] = b2; s.Interior[x] = interior; any = true; }
                        else if (interior && s.Interior[x]) s.Overlap[x] = true;
                    }
                }
                if (!any) return;
                var corners = input.Corners; var normals = input.Normals;
                for (int x = 0; x < width; x++)
                {
                    int r = owner[x]; if (r < 0) continue;
                    int t = raster.Original[r], k = t * 9, i = y * width + x;
                    double b1 = s.B1[x], b2 = s.B2[x], b0 = 1 - b1 - b2;
                    double px = b0 * corners[k] + b1 * corners[k + 3] + b2 * corners[k + 6];
                    double py3 = b0 * corners[k + 1] + b1 * corners[k + 4] + b2 * corners[k + 7];
                    double pz = b0 * corners[k + 2] + b1 * corners[k + 5] + b2 * corners[k + 8];
                    double gx = faceNormals[t * 3], gy = faceNormals[t * 3 + 1], gz = faceNormals[t * 3 + 2];
                    double nx = gx, ny = gy, nz = gz;
                    if (normals != null)
                    {
                        double sx = b0 * normals[k] + b1 * normals[k + 3] + b2 * normals[k + 6];
                        double sy = b0 * normals[k + 1] + b1 * normals[k + 4] + b2 * normals[k + 7];
                        double sz = b0 * normals[k + 2] + b1 * normals[k + 5] + b2 * normals[k + 8];
                        double length = Math.Sqrt(sx * sx + sy * sy + sz * sz);
                        // 頂点法線が無い（0）か面の裏を向くなら、面の法線を使う
                        if (length > 1e-6 && (sx * gx + sy * gy + sz * gz) > 0) { nx = sx / length; ny = sy / length; nz = sz / length; }
                    }
                    Coverage[i] = (byte)(s.Overlap[x] ? MeshTexelCoverage.Overlap : MeshTexelCoverage.Covered);
                    if (normalOut != null) { normalOut[i * 3] = Quantize(nx * 0.5 + 0.5); normalOut[i * 3 + 1] = Quantize(ny * 0.5 + 0.5); normalOut[i * 3 + 2] = Quantize(nz * 0.5 + 0.5); }
                    if (positionOut != null)
                    {
                        positionOut[i * 3] = Quantize(boundsScale[0] > 0 ? (px - boundsMin[0]) * boundsScale[0] : 0.5);
                        positionOut[i * 3 + 1] = Quantize(boundsScale[1] > 0 ? (py3 - boundsMin[1]) * boundsScale[1] : 0.5);
                        positionOut[i * 3 + 2] = Quantize(boundsScale[2] > 0 ? (pz - boundsMin[2]) * boundsScale[2] : 0.5);
                    }
                    if (curvatureOut != null) curvatureOut[i] = Quantize(0.5 + 0.5 * curvature.Evaluate(px, py3, pz, curvature.TriangleComponent[t]));
                    if (aoOut == null && thicknessOut == null) continue;
                    // テクセルごとの決まった回転（並列の分け方に依らない）
                    uint h = Hash((uint)x * 0x9E3779B1u ^ Hash((uint)y + 0x68E31DA4u));
                    double shift = (h >> 8) * (1.0 / 16777216); uint h2 = Hash(h ^ 0xB5297A4Du);
                    double angle = 2 * Math.PI * ((h2 >> 8) * (1.0 / 16777216));
                    double rc = Math.Cos(angle), rs = Math.Sin(angle);
                    // 法線のまわりの正規直交基底（Duff ほか 2017）
                    double sign = nz >= 0 ? 1.0 : -1.0, a = -1 / (sign + nz), bb = nx * ny * a;
                    double tx = 1 + sign * nx * nx * a, ty = sign * bb, tz = -sign * nx;
                    double qx = bb, qy = sign + ny * ny * a, qz = -ny;
                    if (aoOut != null)
                    {
                        double ox = px + gx * rayOffset, oy = py3 + gy * rayOffset, oz = pz + gz * rayOffset;
                        double occlusion = 0; int validRays = 0; bool any1 = settings.AoFalloff == MeshOcclusionFalloff.None;
                        for (int j = 0; j < aoU.Length; j++)
                        {
                            double u1 = aoU[j] + shift; if (u1 >= 1) u1 -= 1;
                            double cos2 = 1 - u1 * (1 - aoCos2), cosT = Math.Sqrt(cos2), sinT = cos2 < 1 ? Math.Sqrt(1 - cos2) : 0;
                            double cp = aoCos[j] * rc - aoSin[j] * rs, sp = aoSin[j] * rc + aoCos[j] * rs;
                            double lx = sinT * cp, ly = sinT * sp;
                            double dx = tx * lx + qx * ly + nx * cosT, dy = ty * lx + qy * ly + ny * cosT, dz = tz * lx + qz * ly + nz * cosT;
                            if (dx * gx + dy * gy + dz * gz <= GrazingLimit) continue; // 面の下へ向かうレイは環境を見ない
                            validRays++; s.Rays++;
                            double hit = bvh.Trace(ox, oy, oz, dx, dy, dz, aoMax, t, any1, settings.AoIgnoreBackfaces, s.Stack, s.StackT);
                            if (hit < double.PositiveInfinity) occlusion += any1 ? 1 : 1 - hit / aoMax;
                        }
                        aoOut[i] = Quantize(validRays > 0 ? 1 - occlusion / validRays : 1);
                    }
                    if (thicknessOut != null)
                    {
                        double ox = px - gx * rayOffset, oy = py3 - gy * rayOffset, oz = pz - gz * rayOffset;
                        double distance = 0; int validRays = 0;
                        for (int j = 0; j < thU.Length; j++)
                        {
                            double u1 = thU[j] + shift; if (u1 >= 1) u1 -= 1;
                            double cos2 = 1 - u1 * (1 - thCos2), cosT = Math.Sqrt(cos2), sinT = cos2 < 1 ? Math.Sqrt(1 - cos2) : 0;
                            double cp = thCos[j] * rc - thSin[j] * rs, sp = thSin[j] * rc + thCos[j] * rs;
                            double lx = sinT * cp, ly = sinT * sp;
                            // 内向き: 法線の反対側の半球
                            double dx = tx * lx + qx * ly - nx * cosT, dy = ty * lx + qy * ly - ny * cosT, dz = tz * lx + qz * ly - nz * cosT;
                            if (-(dx * gx + dy * gy + dz * gz) <= GrazingLimit) continue;
                            validRays++; s.Rays++;
                            double hit = bvh.Trace(ox, oy, oz, dx, dy, dz, thicknessMax, t, false, false, s.Stack, s.StackT);
                            distance += hit < thicknessMax ? hit : thicknessMax;
                        }
                        thicknessOut[i] = Quantize(validRays > 0 ? distance / validRays / thicknessMax : 1);
                    }
                }
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
                        if (normalOut != null) { normalOut[i * 3] = normalOut[from * 3]; normalOut[i * 3 + 1] = normalOut[from * 3 + 1]; normalOut[i * 3 + 2] = normalOut[from * 3 + 2]; }
                        if (positionOut != null) { positionOut[i * 3] = positionOut[from * 3]; positionOut[i * 3 + 1] = positionOut[from * 3 + 1]; positionOut[i * 3 + 2] = positionOut[from * 3 + 2]; }
                        if (aoOut != null) aoOut[i] = aoOut[from];
                        if (curvatureOut != null) curvatureOut[i] = curvatureOut[from];
                        if (thicknessOut != null) thicknessOut[i] = thicknessOut[from];
                    }
                });
                return true;
            }
        }
    }
}
