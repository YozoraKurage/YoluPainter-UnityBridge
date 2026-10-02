using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Paths;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>描いた結果と、描けなかった所の数。</summary>
    public sealed class SurfacePathRender
    {
        /// <summary>パスのチャンネルの画素（文書と同じ大きさ）。</summary>
        public SparseTileSurface Surface { get; internal set; }
        public int Dabs { get; internal set; }
        /// <summary>面へ投影できなかった（穴・面の外・反対の面へ飛ぶ）サンプルの数。そこは描かない。</summary>
        public int Gaps { get; internal set; }
    }

    /// <summary>
    /// 編集できるパス（<see cref="SurfacePath"/>）を今のスナップショットの面に描く。制御点を今の三角形から 3D の位置に戻し、
    /// centripetal Catmull-Rom で結んだ曲線を、ブラシの間隔ごとに法線の向きのレイで面へ投影して、面のダブ（3D ビューのブラシと同じ）で塗る。
    /// 見えるかどうかはカメラではなく、その点の法線の上から判定するので、どの向きから見ていても同じ結果になる。
    /// </summary>
    public static class SurfacePathRenderer
    {
        /// <summary>スナップショットの指紋: 三角形の並び・UV・レンダラーとスロット（位置は含めない。ポーズでは変わらない）。</summary>
        public static string Fingerprint(SurfaceGeometry geometry)
        {
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[4];
                void Int(int v) { buffer[0] = (byte)v; buffer[1] = (byte)(v >> 8); buffer[2] = (byte)(v >> 16); buffer[3] = (byte)(v >> 24); sha.TransformBlock(buffer, 0, 4, null, 0); }
                void Float(float f) => Int(BitConverter.ToInt32(BitConverter.GetBytes(f), 0));
                Int(geometry.TriangleCount);
                foreach (var t in geometry.Triangles) { Int(t.RendererIndex); Int(t.MaterialSlot); Float(t.UvA.x); Float(t.UvA.y); Float(t.UvB.x); Float(t.UvB.y); Float(t.UvC.x); Float(t.UvC.y); }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                var sb = new StringBuilder(); for (int i = 0; i < 16; i++) sb.Append(sha.Hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>レイのヒットを制御点にする。</summary>
        public static PathPoint PointOf(SurfaceHit hit, double pressure = 1) => new PathPoint(hit.TriangleIndex, hit.Barycentric.y, hit.Barycentric.z, pressure);

        /// <summary>制御点の今の 3D の位置と面の法線。</summary>
        public static Vector3 Position(SurfaceGeometry geometry, PathPoint p, out Vector3 normal)
        {
            var t = geometry.Triangles[p.Triangle]; normal = t.Normal;
            return t.A * (float)(1 - p.U - p.V) + t.B * (float)p.U + t.C * (float)p.V;
        }

        public static SurfacePathRender Render(PaintDocument document, SurfaceGeometry geometry, SurfacePath path, SurfaceBrushBudget budget = null)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (path.ModelFingerprint != Fingerprint(geometry)) throw new InvalidOperationException("This path belongs to another model snapshot (its triangles or UVs differ). Load that model, or rasterize the layer.");
            foreach (var p in path.Points) if (p.Triangle >= geometry.TriangleCount) throw new InvalidOperationException("A path point refers to a triangle the snapshot does not have.");
            var brush = path.Brush;
            var scratch = new PaintDocument(document.Width, document.Height, document.TileSize, 0) { SourceBudgetBytes = document.SourceBudgetBytes, ActiveStrokeBudgetBytes = document.ActiveStrokeBudgetBytes };
            var layer = scratch.AddLayer("path");
            if (!layer.IsChannelEnabled(path.Channel)) scratch.SetChannelEnabled(layer.Id, path.Channel, true);
            var result = new SurfacePathRender();
            using (var stroke = scratch.BeginStroke(layer.Id, path.Channel, brush.StrokeSettings()))
            {
                void Dab(SurfaceHit hit, double pressure)
                {
                    if (brush.PressureSize && pressure <= 0) return;
                    float radius = (float)(brush.RadiusWorld * (brush.PressureSize ? Math.Max(.001, pressure) : 1));
                    // 見えるかどうかは、この点の法線の上（ブラシの 4 倍の高さ）から判定する（カメラに依らない）
                    var dab = geometry.BuildSurfaceDabs(hit, radius, document.Width, document.Height, hit.Position + hit.Normal * Mathf.Max(radius * 4, 1e-5f), (float)brush.Hardness, budget);
                    if (dab.WasClipped) throw new InvalidOperationException(dab.Diagnostic);
                    foreach (var pixel in dab.Pixels) stroke.ApplyPixel(pixel.X, pixel.Y, pixel.Coverage, pressure);
                    result.Dabs++;
                }
                var points = path.Points; int n = points.Count;
                var positions = new Vector3[n]; var normals = new Vector3[n];
                for (int i = 0; i < n; i++) positions[i] = Position(geometry, points[i], out normals[i]);
                if (n == 1)
                {
                    var t = geometry.Triangles[points[0].Triangle];
                    Dab(new SurfaceHit { SnapshotRevision = geometry.SnapshotRevision, RendererIndex = t.RendererIndex, MaterialSlot = t.MaterialSlot, TriangleIndex = points[0].Triangle,
                        Position = positions[0], Normal = normals[0], Barycentric = new Vector3((float)(1 - points[0].U - points[0].V), (float)points[0].U, (float)points[0].V) }, points[0].Pressure);
                }
                else if (n > 1)
                {
                    double step = Math.Max(1e-6, brush.RadiusWorld * 2 * brush.Spacing), carried = step; // 最初の点にもダブを置く
                    for (int s = 0; s + 1 < n; s++)
                    {
                        Vector3 p0 = positions[Math.Max(0, s - 1)], p1 = positions[s], p2 = positions[s + 1], p3 = positions[Math.Min(n - 1, s + 2)];
                        float chord = Vector3.Distance(p1, p2);
                        int substeps = Math.Max(1, (int)Math.Ceiling(chord / (step * .25)));
                        if (substeps > 1000000) throw new InvalidOperationException("The path is too long for its brush spacing.");
                        Vector3 previous = p1;
                        for (int k = 0; k <= substeps; k++)
                        {
                            float t = k / (float)substeps;
                            var q = k == 0 ? p1 : CatmullRom(p0, p1, p2, p3, t);
                            carried += k == 0 ? 0 : Vector3.Distance(previous, q); previous = q;
                            if (carried < step) continue;
                            carried = 0;
                            var normal = Vector3.Slerp(normals[s], normals[s + 1], t).normalized;
                            double pressure = points[s].Pressure + (points[s + 1].Pressure - points[s].Pressure) * t;
                            if (Project(geometry, q, normal, Mathf.Max((float)step * 4, chord * .25f, (float)brush.RadiusWorld * 4), out var hit)) Dab(hit, pressure);
                            else result.Gaps++;
                        }
                    }
                }
                stroke.Commit();
            }
            result.Surface = layer.GetChannel(path.Channel);
            return result;
        }

        /// <summary>曲線の点 q を、法線 normal の向きのレイで面へ落とす。上から見つからなければ下から。反対の面（法線が逆）に当たったら失敗。</summary>
        static bool Project(SurfaceGeometry geometry, Vector3 q, Vector3 normal, float reach, out SurfaceHit hit)
        {
            if (geometry.TryRaycast(new Ray(q + normal * reach, -normal), out hit, true, reach * 2) && Vector3.Dot(hit.Normal, normal) > .2f) return true;
            if (geometry.TryRaycast(new Ray(q - normal * reach, normal), out hit, false, reach * 2) && Vector3.Dot(hit.Normal, normal) > .2f) return true;
            return false;
        }

        /// <summary>centripetal Catmull-Rom（α = 0.5。尖りやループが出にくい）の p1 → p2 の区間の t。</summary>
        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float Knot(Vector3 a, Vector3 b) => Mathf.Max(1e-6f, Mathf.Sqrt(Vector3.Distance(a, b)));
            float t0 = 0, t1 = t0 + Knot(p0, p1), t2 = t1 + Knot(p1, p2), t3 = t2 + Knot(p2, p3);
            float u = Mathf.Lerp(t1, t2, t);
            Vector3 a1 = (t1 - u) / (t1 - t0) * p0 + (u - t0) / (t1 - t0) * p1;
            Vector3 a2 = (t2 - u) / (t2 - t1) * p1 + (u - t1) / (t2 - t1) * p2;
            Vector3 a3 = (t3 - u) / (t3 - t2) * p2 + (u - t2) / (t3 - t2) * p3;
            Vector3 b1 = (t2 - u) / (t2 - t0) * a1 + (u - t0) / (t2 - t0) * a2;
            Vector3 b2 = (t3 - u) / (t3 - t1) * a2 + (u - t1) / (t3 - t1) * a3;
            return (t2 - u) / (t2 - t1) * b1 + (u - t1) / (t2 - t1) * b2;
        }
    }
}
