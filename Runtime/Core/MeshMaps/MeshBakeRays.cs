using System;

namespace Yozolab.YoluPainter.Core.MeshMaps
{
    /// <summary>
    /// AO・ベントノーマル・厚みのレイを飛ばす 1 サンプル分の仕事。面の点 (O)、頂点法線 (N)、面の法線 (G)、サンプルごとの回転
    /// （Shift は Hammersley の u1 のずらし、Rc / Rs は方位角の回転の cos / sin）、無視する三角形、どの形（0 = 低ポリ、1 = 高ポリ）に飛ばすか。
    /// CPU（基準）はこれをその場で <see cref="MeshBaker"/> の中で処理し、GPU などの <see cref="IMeshBakeRayTracer"/> はまとめて処理する。
    /// </summary>
    public struct MeshBakeRayJob
    {
        public double Ox, Oy, Oz, Nx, Ny, Nz, Gx, Gy, Gz, Shift, Rc, Rs;
        public int Ignore, Scene;
    }

    /// <summary>1 サンプル分の結果。AO（0〜1）、ベントノーマル（単位ベクトル。全部遮られたら N）、厚み（0〜1）、数えたレイの数。</summary>
    public struct MeshBakeRayResult
    {
        public double Ao, BentX, BentY, BentZ, Thickness;
        public int Rays;
    }

    /// <summary>
    /// レイを飛ばす相手と条件（読むだけ）。BVH は形ごとに、節が 6 個の境界（min xyz, max xyz）と最初の子／三角形・数（数 0 は内側の節で、
    /// 子は First と First + 1）、三角形が 10 個（A xyz, e1 xyz, e2 xyz, 平行とみなす det）と元の番号。方向の点列と距離は
    /// スナップショットの単位（対角線比から直したもの）。
    /// </summary>
    public sealed class MeshBakeRayScene
    {
        public int SceneCount => NodeBounds.Length;
        public float[][] NodeBounds { get; internal set; }
        public int[][] NodeFirst { get; internal set; }
        public int[][] NodeCount { get; internal set; }
        public float[][] Triangles { get; internal set; }
        public int[][] TriangleOriginal { get; internal set; }
        public double[] AoU { get; internal set; }
        public double[] AoCos { get; internal set; }
        public double[] AoSin { get; internal set; }
        public double[] ThicknessU { get; internal set; }
        public double[] ThicknessCos { get; internal set; }
        public double[] ThicknessSin { get; internal set; }
        public double AoCos2 { get; internal set; }
        public double ThicknessCos2 { get; internal set; }
        public double AoMax { get; internal set; }
        public double ThicknessMax { get; internal set; }
        /// <summary>レイの始点を面から浮かせる量（スナップショットの単位）。</summary>
        public double RayOffset { get; internal set; }
        public double GrazingLimit { get; internal set; }
        /// <summary>AO の減衰が無い（最初の当たりで止めてよい）。</summary>
        public bool AoAnyHit { get; internal set; }
        public bool AoIgnoreBackfaces { get; internal set; }
        public bool WantAo { get; internal set; }
        public bool WantThickness { get; internal set; }
        /// <summary>BVH の大きさの合計（バイト、上げる前の見積もり）。</summary>
        public long Bytes
        {
            get
            {
                long bytes = 0;
                for (int i = 0; i < SceneCount; i++) bytes += NodeBounds[i].LongLength * 4 + NodeFirst[i].LongLength * 8 + Triangles[i].LongLength * 4 + TriangleOriginal[i].LongLength * 4;
                return bytes;
            }
        }
    }

    /// <summary>
    /// AO・ベントノーマル・厚みのレイを、CPU の代わりにまとめて処理するもの（GPU の計算シェーダーなど）。結果は CPU の基準と同じ式で、
    /// 精度（float など）の違いだけを許す。失敗したら例外を投げる（呼び出し側は CPU で焼き直す）。
    /// </summary>
    public interface IMeshBakeRayTracer
    {
        /// <summary>結果の記録に出す名前（"GPU (…)" など）。</summary>
        string Name { get; }
        /// <summary>このシーンの準備をする。使えないときは理由を返し、そのときは CPU で焼く。使えれば null。</summary>
        string Prepare(MeshBakeRayScene scene);
        /// <summary>jobs[0..count) を処理して results に書く。</summary>
        void Trace(MeshBakeRayJob[] jobs, int count, MeshBakeRayResult[] results);
        /// <summary>準備したものを手放す（失敗・取消でも呼ぶ）。</summary>
        void Release();
    }
}
