using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Yozolab.YoluPainter.Core.MeshMaps
{
    /// <summary>
    /// ベイクの入力: 静的なメッシュのスナップショットを三角形の並び（頂点を共有しない）で持つ。位置はスナップショットの空間
    /// （ワールドの軸、原点はモデルのルート）、UV は 1 つのチャンネル、頂点法線は任意（0 の法線は「無い」で、面の法線を使う）、
    /// 三角形ごとにマテリアルスロット。UV の面積が 0 の三角形（UV の無いメッシュ）は遮るだけで焼き込まれない。
    /// 作った後は変えない（配列は複写して持つ）。<see cref="Hash"/> は位置・法線・UV・スロット・UV チャンネルの SHA-256 で、
    /// ベイクした結果の由来と照合に使う。浮動小数はビット列のまま入れるので、値が 1 ビットでも違えば別のモデルになる。
    /// <see cref="TopologyHash"/> は三角形の数・UV・スロットだけの SHA-256 で、形（ポーズ・BlendShape・編集）だけが変わったのか、
    /// UV や三角形まで変わったのかを見分けて知らせるのに使う。
    /// </summary>
    public sealed class MeshBakeInput
    {
        public const int MaxTriangles = 4000000;
        internal readonly float[] Corners, Normals, Uvs;
        internal readonly int[] Slots;
        public int TriangleCount { get; }
        public int UvChannel { get; }
        public bool HasNormals { get; }
        public string Hash { get; }
        public string TopologyHash { get; }
        /// <summary>頂点法線の出どころ（"authored"、"reconstructed-crease-60" など。知らせるだけで、照合は法線の値そのもので行う）。</summary>
        public string NormalSource { get; }
        internal readonly double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        /// <summary>モデル全体の境界箱の対角線の長さ（相対の距離の基準）。</summary>
        public double Diagonal { get; }

        /// <param name="corners">三角形ごとに 3 頂点 × xyz（9 個）。表は Unity と同じく (B−A)×(C−A) の向き。</param>
        /// <param name="normals">corners と同じ並びの頂点法線、または null。</param>
        /// <param name="uvs">三角形ごとに 3 頂点 × uv（6 個）。</param>
        /// <param name="slots">三角形ごとのマテリアルスロット（0 以上。-1 は「どのスロットでもない」遮るだけの面）。</param>
        public MeshBakeInput(float[] corners, float[] normals, float[] uvs, int[] slots, int uvChannel = 0, string normalSource = null)
        {
            if (corners == null) throw new ArgumentNullException(nameof(corners));
            if (uvs == null) throw new ArgumentNullException(nameof(uvs));
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (corners.Length % 9 != 0) throw new ArgumentException("Corners must hold 9 floats per triangle.", nameof(corners));
            int count = corners.Length / 9;
            if (count == 0) throw new ArgumentException("The mesh has no triangles.", nameof(corners));
            if (count > MaxTriangles) throw new MeshBakeRefusedException("The mesh has " + count + " triangles; mesh-map baking accepts at most " + MaxTriangles + ".");
            if (uvs.Length != count * 6) throw new ArgumentException("UVs must hold 6 floats per triangle.", nameof(uvs));
            if (slots.Length != count) throw new ArgumentException("Slots must hold one value per triangle.", nameof(slots));
            if (normals != null && normals.Length != corners.Length) throw new ArgumentException("Normals must match the corners.", nameof(normals));
            if (uvChannel < 0 || uvChannel > 7) throw new ArgumentOutOfRangeException(nameof(uvChannel));
            foreach (float v in corners) if (float.IsNaN(v) || float.IsInfinity(v)) throw new ArgumentException("The mesh contains non-finite positions.", nameof(corners));
            foreach (float v in uvs) if (float.IsNaN(v) || float.IsInfinity(v)) throw new ArgumentException("The mesh contains non-finite UVs.", nameof(uvs));
            if (normals != null) foreach (float v in normals) if (float.IsNaN(v) || float.IsInfinity(v)) throw new ArgumentException("The mesh contains non-finite normals.", nameof(normals));
            foreach (int s in slots) if (s < -1) throw new ArgumentOutOfRangeException(nameof(slots), "Material slots must be -1 or more.");
            Corners = (float[])corners.Clone(); Uvs = (float[])uvs.Clone(); Slots = (int[])slots.Clone();
            Normals = normals == null ? null : (float[])normals.Clone();
            TriangleCount = count; UvChannel = uvChannel; HasNormals = normals != null;
            NormalSource = normals == null ? "face" : string.IsNullOrEmpty(normalSource) ? "authored" : normalSource;
            MinX = MinY = MinZ = double.MaxValue; MaxX = MaxY = MaxZ = double.MinValue;
            for (int i = 0; i < Corners.Length; i += 3)
            {
                double x = Corners[i], y = Corners[i + 1], z = Corners[i + 2];
                if (x < MinX) MinX = x; if (x > MaxX) MaxX = x;
                if (y < MinY) MinY = y; if (y > MaxY) MaxY = y;
                if (z < MinZ) MinZ = z; if (z > MaxZ) MaxZ = z;
            }
            double dx = MaxX - MinX, dy = MaxY - MinY, dz = MaxZ - MinZ;
            Diagonal = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (!(Diagonal > 0) || double.IsInfinity(Diagonal)) throw new MeshBakeRefusedException("The mesh has no extent (all vertices coincide) or exceeds the numeric range.");
            Hash = ComputeHash(true); TopologyHash = ComputeHash(false);
        }

        /// <summary>
        /// 頂点法線を形から作り直す（元のメッシュの頂点法線が手に入らないときの代わり）。位置で溶接した頂点ごとに、その角の面と
        /// 法線の角度が crease 以下の面の法線を、角の大きさで重み付けして平均する（Unity の取り込みの「スムージング角度」と同じ考え方。
        /// 既定 60°）。手で編集した法線（トゥーン用の顔の法線など）は戻らない。面積の無い三角形は 0。
        /// </summary>
        public static float[] ReconstructNormals(float[] corners, double creaseDegrees = 60)
        {
            if (corners == null) throw new ArgumentNullException(nameof(corners));
            if (corners.Length % 9 != 0) throw new ArgumentException("Corners must hold 9 floats per triangle.", nameof(corners));
            if (!(creaseDegrees >= 0 && creaseDegrees <= 180)) throw new ArgumentOutOfRangeException(nameof(creaseDegrees));
            int count = corners.Length / 9;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int i = 0; i < corners.Length; i += 3)
            {
                minX = Math.Min(minX, corners[i]); minY = Math.Min(minY, corners[i + 1]); minZ = Math.Min(minZ, corners[i + 2]);
                maxX = Math.Max(maxX, corners[i]); maxY = Math.Max(maxY, corners[i + 1]); maxZ = Math.Max(maxZ, corners[i + 2]);
            }
            double diagonal = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY) + (maxZ - minZ) * (maxZ - minZ));
            double tolerance = Math.Max(1e-9, diagonal * 1e-6);
            var face = new double[count * 3]; var angle = new double[count * 3];
            for (int t = 0; t < count; t++)
            {
                int k = t * 9;
                double e1x = corners[k + 3] - corners[k], e1y = corners[k + 4] - corners[k + 1], e1z = corners[k + 5] - corners[k + 2];
                double e2x = corners[k + 6] - corners[k], e2y = corners[k + 7] - corners[k + 1], e2z = corners[k + 8] - corners[k + 2];
                double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x, length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (!(length > 1e-30)) continue;
                face[t * 3] = nx / length; face[t * 3 + 1] = ny / length; face[t * 3 + 2] = nz / length;
                for (int c = 0; c < 3; c++)
                {
                    int a = k + c * 3, b = k + (c + 1) % 3 * 3, d = k + (c + 2) % 3 * 3;
                    double ux = corners[b] - corners[a], uy = corners[b + 1] - corners[a + 1], uz = corners[b + 2] - corners[a + 2];
                    double vx = corners[d] - corners[a], vy = corners[d + 1] - corners[a + 1], vz = corners[d + 2] - corners[a + 2];
                    double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
                    angle[t * 3 + c] = Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), ux * vx + uy * vy + uz * vz);
                }
            }
            // 溶接した頂点 → その頂点を持つ角（三角形 × 3 + 角）の一覧
            var weld = new Dictionary<(long, long, long), int>(count);
            var vertexOf = new int[count * 3];
            for (int i = 0; i < count * 3; i++)
            {
                var key = ((long)Math.Round(corners[i * 3] / tolerance), (long)Math.Round(corners[i * 3 + 1] / tolerance), (long)Math.Round(corners[i * 3 + 2] / tolerance));
                if (!weld.TryGetValue(key, out int id)) { id = weld.Count; weld.Add(key, id); }
                vertexOf[i] = id;
            }
            var start = new int[weld.Count + 1];
            foreach (int id in vertexOf) start[id + 1]++;
            for (int i = 0; i < weld.Count; i++) start[i + 1] += start[i];
            var fill = (int[])start.Clone(); var cornersAt = new int[count * 3];
            for (int i = 0; i < count * 3; i++) cornersAt[fill[vertexOf[i]]++] = i;
            double limit = Math.Cos(creaseDegrees * Math.PI / 180) - 1e-9;
            var result = new float[count * 9];
            for (int i = 0; i < count * 3; i++)
            {
                int t = i / 3; double fx = face[t * 3], fy = face[t * 3 + 1], fz = face[t * 3 + 2];
                if (fx == 0 && fy == 0 && fz == 0) continue;
                double sx = 0, sy = 0, sz = 0; int id = vertexOf[i];
                for (int j = start[id]; j < start[id + 1]; j++)
                {
                    int other = cornersAt[j], u = other / 3;
                    double ox = face[u * 3], oy = face[u * 3 + 1], oz = face[u * 3 + 2];
                    if (ox * fx + oy * fy + oz * fz < limit) continue;
                    double w = angle[other]; sx += ox * w; sy += oy * w; sz += oz * w;
                }
                double length = Math.Sqrt(sx * sx + sy * sy + sz * sz);
                if (!(length > 1e-30)) { sx = fx; sy = fy; sz = fz; length = 1; }
                result[i * 3] = (float)(sx / length); result[i * 3 + 1] = (float)(sy / length); result[i * 3 + 2] = (float)(sz / length);
            }
            return result;
        }

        public double[] BoundsMin => new[] { MinX, MinY, MinZ };
        public double[] BoundsMax => new[] { MaxX, MaxY, MaxZ };

        string ComputeHash(bool geometry)
        {
            using (var sha = SHA256.Create())
            {
                void Block(byte[] bytes) => sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                Block(Encoding.ASCII.GetBytes(geometry ? "YOLUPAINTER-MESHBAKE-INPUT-1\n" : "YOLUPAINTER-MESHBAKE-TOPOLOGY-1\n"));
                Block(BitConverter.GetBytes(TriangleCount)); Block(BitConverter.GetBytes(UvChannel));
                // 浮動小数のビット列（リトルエンディアンの機械で書く。ビッグエンディアンでは別の値になるが、照合は同じ機械の間）
                if (geometry) { Block(BitConverter.GetBytes(HasNormals ? 1 : 0)); Floats(sha, Corners); if (Normals != null) Floats(sha, Normals); }
                Floats(sha, Uvs);
                var slotBytes = new byte[Slots.Length * 4]; Buffer.BlockCopy(Slots, 0, slotBytes, 0, slotBytes.Length); Block(slotBytes);
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var hex = new StringBuilder(64);
                foreach (byte b in sha.Hash) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }
        static void Floats(SHA256 sha, float[] values)
        {
            const int chunk = 1 << 16;
            var bytes = new byte[Math.Min(values.Length, chunk) * 4];
            for (int start = 0; start < values.Length; start += chunk)
            {
                int n = Math.Min(chunk, values.Length - start);
                Buffer.BlockCopy(values, start * 4, bytes, 0, n * 4);
                sha.TransformBlock(bytes, 0, n * 4, null, 0);
            }
        }
    }
}
