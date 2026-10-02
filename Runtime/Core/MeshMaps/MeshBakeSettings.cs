using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Yozolab.YoluPainter.Core.MeshMaps
{
    /// <summary>ベイクする mesh map の種類。値は保存形式に入るので、並べ替えず末尾に足す。</summary>
    public enum MeshMapKind
    {
        /// <summary>頂点法線（スムーズの法線）を補間した向き。スナップショットの空間（ワールドの軸、原点はモデルのルート）で n×0.5+0.5。</summary>
        WorldNormal = 0,
        /// <summary>位置。モデル全体の境界箱で軸ごとに 0〜1 に正規化（厚みの無い軸は 0.5）。</summary>
        Position = 1,
        /// <summary>環境遮蔽。1 = 何にも遮られない、0 = 全方向が近くで遮られる。</summary>
        AmbientOcclusion = 2,
        /// <summary>曲率。0.5 = 平ら、1 に近いほど凸（外の角）、0 に近いほど凹（内の角）。</summary>
        Curvature = 3,
        /// <summary>厚み。内向きのレイが反対側に届くまでの平均距離 ÷ 最大距離。0 = 薄い、1 = 最大距離以上（または抜けた）。</summary>
        Thickness = 4,
    }

    /// <summary>テクセルの由来。Empty は三角形も余白も届かない所で、値は 0（データを作らない）。Overlap は UV が
    /// 別の三角形とも重なっていた所（添字の小さい三角形の値を持つ）。Padding は島の外の余白で、いちばん近い島のテクセルの写し。</summary>
    public enum MeshTexelCoverage : byte { Empty = 0, Covered = 1, Overlap = 2, Padding = 3 }

    /// <summary>AO と厚みのレイを遮るもの。</summary>
    public enum MeshOccluders { WholeModel = 0, TargetSlotOnly = 1 }

    /// <summary>AO の遮蔽の距離による減衰。None は最大距離の内側なら遮蔽 1、Linear は 1 − 距離 ÷ 最大距離。</summary>
    public enum MeshOcclusionFalloff { None = 0, Linear = 1 }

    /// <summary>ベイクの条件。距離はすべてモデル全体の境界箱の対角線を 1 とする相対値（モデルの大きさと単位に依らない）。
    /// 種類ごとの条件の文字列（<see cref="KindKey"/>）は由来に記録し、違う条件の結果を同じものとして使わない。</summary>
    public sealed class MeshBakeSettings
    {
        public const int MaxSize = 8192, MaxPadding = 64, MaxSamples = 1024;
        public const double MinCurvatureRadius = 0.001, MaxCurvatureRadius = 0.5, MaxDistance = 4;
        public static readonly IReadOnlyList<MeshMapKind> AllKinds = new[] { MeshMapKind.WorldNormal, MeshMapKind.Position, MeshMapKind.AmbientOcclusion, MeshMapKind.Curvature, MeshMapKind.Thickness };

        public int Width = 1024, Height = 1024;
        /// <summary>焼き込む三角形のマテリアルスロット（全体を平らにした番号）。-1 ならすべてのスロット。</summary>
        public int TargetSlot;
        /// <summary>島の外へ値を延ばす幅（テクセル）。隣の島の本体には書かない。</summary>
        public int Padding = 16;
        public MeshMapKind[] Maps = AllKinds.ToArray();
        public MeshOccluders Occluders = MeshOccluders.WholeModel;
        public int AoSamples = 64;
        public double AoMaxDistance = 0.1, AoSpreadDegrees = 180;
        public MeshOcclusionFalloff AoFalloff = MeshOcclusionFalloff.Linear;
        /// <summary>true なら裏面に当たったレイは遮蔽にしない（そのまま先へ進む）。</summary>
        public bool AoIgnoreBackfaces;
        public int ThicknessSamples = 64;
        public double ThicknessMaxDistance = 0.1, ThicknessSpreadDegrees = 90;
        /// <summary>曲率を集める球の半径。</summary>
        public double CurvatureRadius = 0.02;

        public MeshBakeSettings Clone()
        {
            var copy = (MeshBakeSettings)MemberwiseClone();
            copy.Maps = Maps == null ? null : (MeshMapKind[])Maps.Clone();
            return copy;
        }

        public bool Includes(MeshMapKind kind) => Maps != null && Array.IndexOf(Maps, kind) >= 0;

        /// <summary>値の範囲と型を確かめる。おかしければ何もせず例外。</summary>
        public void Validate()
        {
            if (Width < 1 || Height < 1 || Width > MaxSize || Height > MaxSize) throw new ArgumentOutOfRangeException(nameof(Width), "Mesh map size must be 1–" + MaxSize + " texels per side.");
            if (TargetSlot < -1) throw new ArgumentOutOfRangeException(nameof(TargetSlot));
            if (Padding < 0 || Padding > MaxPadding) throw new ArgumentOutOfRangeException(nameof(Padding), "Padding must be 0–" + MaxPadding + " texels.");
            if (Maps == null || Maps.Length == 0) throw new ArgumentException("Choose at least one mesh map to bake.", nameof(Maps));
            foreach (var kind in Maps) if (!Enum.IsDefined(typeof(MeshMapKind), kind)) throw new ArgumentOutOfRangeException(nameof(Maps), "Unknown mesh map kind " + (int)kind + ".");
            if (Maps.Distinct().Count() != Maps.Length) throw new ArgumentException("A mesh map kind is listed twice.", nameof(Maps));
            if (!Enum.IsDefined(typeof(MeshOccluders), Occluders)) throw new ArgumentOutOfRangeException(nameof(Occluders));
            if (!Enum.IsDefined(typeof(MeshOcclusionFalloff), AoFalloff)) throw new ArgumentOutOfRangeException(nameof(AoFalloff));
            CheckSamples(AoSamples, nameof(AoSamples)); CheckSamples(ThicknessSamples, nameof(ThicknessSamples));
            CheckDistance(AoMaxDistance, nameof(AoMaxDistance)); CheckDistance(ThicknessMaxDistance, nameof(ThicknessMaxDistance));
            CheckSpread(AoSpreadDegrees, nameof(AoSpreadDegrees)); CheckSpread(ThicknessSpreadDegrees, nameof(ThicknessSpreadDegrees));
            if (!(CurvatureRadius >= MinCurvatureRadius && CurvatureRadius <= MaxCurvatureRadius)) throw new ArgumentOutOfRangeException(nameof(CurvatureRadius), "Curvature radius must be " + MinCurvatureRadius + "–" + MaxCurvatureRadius + " of the model's bounding-box diagonal.");
        }
        static void CheckSamples(int value, string name) { if (value < 1 || value > MaxSamples) throw new ArgumentOutOfRangeException(name, "Samples must be 1–" + MaxSamples + "."); }
        static void CheckDistance(double value, string name) { if (!(value > 0 && value <= MaxDistance)) throw new ArgumentOutOfRangeException(name, "Distances are relative to the bounding-box diagonal and must be in (0, " + MaxDistance + "]."); }
        static void CheckSpread(double value, string name) { if (!(value >= 1 && value <= 180)) throw new ArgumentOutOfRangeException(name, "Spread angle must be 1–180 degrees."); }

        static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>その種類の結果を変える設定だけを並べた文字列（共通の大きさ・スロット・余白は由来の別の欄）。由来に記録し、
        /// 古さの判定とキャッシュの同一性に使う。</summary>
        public string KindKey(MeshMapKind kind)
        {
            switch (kind)
            {
                case MeshMapKind.WorldNormal: return "source=vertex-normals";
                case MeshMapKind.Position: return "normalize=bounding-box";
                case MeshMapKind.AmbientOcclusion:
                    return "samples=" + AoSamples + ";max=" + R(AoMaxDistance) + ";spread=" + R(AoSpreadDegrees) + ";falloff=" + AoFalloff
                        + ";backfaces=" + (AoIgnoreBackfaces ? "ignore" : "occlude") + ";occluders=" + Occluders;
                case MeshMapKind.Curvature: return "radius=" + R(CurvatureRadius);
                case MeshMapKind.Thickness:
                    return "samples=" + ThicknessSamples + ";max=" + R(ThicknessMaxDistance) + ";spread=" + R(ThicknessSpreadDegrees) + ";occluders=" + Occluders;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        /// <summary><see cref="KindKey"/> の文字列から設定を戻す（保存したマップを開いたときに UI の値をそろえるため）。読めない値は変えない。</summary>
        public void ApplyKindKey(MeshMapKind kind, string key, int padding)
        {
            if (padding >= 0 && padding <= MaxPadding) Padding = padding;
            if (string.IsNullOrEmpty(key)) return;
            foreach (var part in key.Split(';'))
            {
                int eq = part.IndexOf('='); if (eq <= 0) continue;
                string name = part.Substring(0, eq), value = part.Substring(eq + 1);
                bool ao = kind == MeshMapKind.AmbientOcclusion, thick = kind == MeshMapKind.Thickness;
                if (!ao && !thick && !(kind == MeshMapKind.Curvature && name == "radius")) continue;
                switch (name)
                {
                    case "samples": if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= MaxSamples) { if (ao) AoSamples = n; else ThicknessSamples = n; } break;
                    case "max": if (TryDouble(value, out double max) && max > 0 && max <= MaxDistance) { if (ao) AoMaxDistance = max; else ThicknessMaxDistance = max; } break;
                    case "spread": if (TryDouble(value, out double spread) && spread >= 1 && spread <= 180) { if (ao) AoSpreadDegrees = spread; else ThicknessSpreadDegrees = spread; } break;
                    case "falloff": if (ao && Enum.TryParse(value, false, out MeshOcclusionFalloff f) && Enum.IsDefined(typeof(MeshOcclusionFalloff), f)) AoFalloff = f; break;
                    case "backfaces": if (ao && (value == "ignore" || value == "occlude")) AoIgnoreBackfaces = value == "ignore"; break;
                    case "occluders": if (Enum.TryParse(value, false, out MeshOccluders o) && Enum.IsDefined(typeof(MeshOccluders), o)) Occluders = o; break;
                    case "radius": if (TryDouble(value, out double r) && r >= MinCurvatureRadius && r <= MaxCurvatureRadius) CurvatureRadius = r; break;
                }
            }
        }
        static bool TryDouble(string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>ベイクの予算。メモリは割り当てる前に見積もって断り、時間は超えた時点で止めて何も返さない。</summary>
    public sealed class MeshBakeBudget
    {
        /// <summary>出力・作業領域・BVH などの見積もりの上限（バイト）。</summary>
        public long MaxBytes = 512L * 1024 * 1024;
        /// <summary>0 以下で上限なし。</summary>
        public double MaxSeconds;
        /// <summary>0 以下で論理プロセッサの数。</summary>
        public int MaxDegreeOfParallelism;
    }

    /// <summary>予算・入力の都合でベイクを始めない（何も割り当てず、何も変えない）。</summary>
    public sealed class MeshBakeRefusedException : InvalidOperationException
    {
        public MeshBakeRefusedException(string message) : base(message) { }
    }

    public enum MeshBakeStatus { Completed, Canceled, TimedOut }

    /// <summary>ベイクの記録。テクセルの数・三角形の診断・時間。</summary>
    public sealed class MeshBakeReport
    {
        public long CoveredTexels, OverlapTexels, PaddedTexels, EmptyTexels, Rays, EstimatedBytes;
        public int ReceivingTriangles, ZeroUvAreaTriangles, DegenerateTriangles, OccluderTriangles;
        public int BoundaryEdges, NonManifoldEdges, InconsistentWindingEdges, CurvatureSegments;
        public double PrepareSeconds, RasterSeconds, PaddingSeconds, TotalSeconds;
        public readonly List<string> Diagnostics = new List<string>();

        public string Summary()
        {
            long texels = CoveredTexels + OverlapTexels;
            var text = new StringBuilder();
            text.Append(texels).Append(" texels on ").Append(ReceivingTriangles).Append(" triangles, ").Append(PaddedTexels).Append(" padding, ")
                .Append(EmptyTexels).Append(" empty; ").Append(TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)).Append(" s");
            if (OverlapTexels > 0) text.Append("; ").Append(OverlapTexels).Append(" texels have overlapping UVs (the lower triangle index wins)");
            return text.ToString();
        }
    }

    public sealed class MeshBakeResult
    {
        public MeshBakeStatus Status { get; }
        /// <summary>完了したときだけ中身がある。取消・時間切れでは空（途中の結果は返さない）。</summary>
        public IReadOnlyList<BakedMeshMap> Maps { get; }
        public MeshBakeReport Report { get; }
        internal MeshBakeResult(MeshBakeStatus status, IReadOnlyList<BakedMeshMap> maps, MeshBakeReport report)
        { Status = status; Maps = maps ?? Array.Empty<BakedMeshMap>(); Report = report; }
    }

    /// <summary>マップを使う側の今の条件。MeshHash が null なら照合できない（モデルが読み込まれていない）。
    /// Settings を渡すと、その種類の設定が違うマップも古いとみなす。</summary>
    public sealed class MeshMapExpectation
    {
        public string MeshHash, TopologyHash;
        public int Width, Height, TargetSlot, UvChannel;
        public MeshBakeSettings Settings;
    }

    public enum MeshMapState { Missing, Current, Stale, Unverified }

    public sealed class MeshMapCheck
    {
        public MeshMapState State { get; }
        public IReadOnlyList<string> Reasons { get; }
        internal MeshMapCheck(MeshMapState state, IReadOnlyList<string> reasons) { State = state; Reasons = reasons ?? Array.Empty<string>(); }
        public override string ToString() => State + (Reasons.Count > 0 ? ": " + string.Join(" ", Reasons) : "");
    }
}
