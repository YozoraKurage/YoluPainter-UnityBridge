using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core.MeshMaps
{
    /// <summary>
    /// 三角形の範囲（UV アイランドとメッシュの塊）の分け方。ID マップのベイク（<see cref="MeshBaker"/>）と、ポリゴン塗りつぶし・3D ビューの選択の
    /// 範囲の索引（Editor の SurfaceRegionIndex）が同じ分け方をするよう、決まりはここ 1 か所にある。
    /// <list type="bullet">
    /// <item>同じマテリアルスロットの三角形だけをつなぐ（頂点の鍵にスロットが入る）。</item>
    /// <item>UV アイランドは UV の端点を <see cref="UvQuantum"/> 倍して丸めた値、メッシュの塊は 3D の位置（スナップショットの空間）を
    /// <see cref="PositionQuantum"/> 倍して丸めた値で頂点を溶接し、同じ辺（端点の組）を持つ三角形をつなぐ。三角形が 3 つ以上集まる辺も、
    /// 面積の無い三角形もつなぐ。</item>
    /// <item>成分の番号は、成分のいちばん小さい三角形の順（0 から）。同じ入力なら並べ替えの順によらず同じ番号。</item>
    /// </list>
    /// 辺は (小さい頂点, 大きい頂点) の 64 ビットにして並べ、同じ辺の三角形を union-find でまとめる（辞書に辺を入れるより少ない記憶で
    /// O(n log n)）。読むだけで、渡された配列を変えない。
    /// </summary>
    public static class MeshRegions
    {
        /// <summary>UV の端点の丸め（UV × 1e6 を四捨五入）。</summary>
        public const double UvQuantum = 1e6;
        /// <summary>3D の位置の丸め（位置 × 1e5 を四捨五入。スナップショットの単位で 0.01 mm 相当）。</summary>
        public const double PositionQuantum = 1e5;

        /// <summary>分け方を求めるときに一時に使う記憶の見積もり（三角形あたり。頂点の辞書・辺の鍵・持ち主・親・番号）。</summary>
        public static long EstimateBytes(long triangles) => triangles * 200;

        /// <summary>UV アイランド。uvs は三角形ごとに 3 頂点 × uv（6 個）、slots は三角形ごとのスロット。</summary>
        public static int[] UvIslands(float[] uvs, int[] slots, out int count) => UvIslands(uvs, slots, out count, out _, out _);
        /// <summary>UV アイランドと、角ごとの溶接した UV の頂点の番号（三角形 × 3 + 角。番号は現れた順）と頂点の数（範囲の UV の輪郭に使う）。</summary>
        public static int[] UvIslands(float[] uvs, int[] slots, out int count, out int[] cornerVertices, out int vertexCount)
        {
            if (uvs == null) throw new ArgumentNullException(nameof(uvs));
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (uvs.Length != slots.Length * 6) throw new ArgumentException("UVs must hold 6 floats per triangle.", nameof(uvs));
            return Connect(slots, uvs, true, out count, out cornerVertices, out vertexCount);
        }

        /// <summary>メッシュの塊（3D の位置でつながった三角形。UV の継ぎ目をまたぐ）。corners は三角形ごとに 3 頂点 × xyz（9 個）。</summary>
        public static int[] MeshParts(float[] corners, int[] slots, out int count)
        {
            if (corners == null) throw new ArgumentNullException(nameof(corners));
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (corners.Length != slots.Length * 9) throw new ArgumentException("Corners must hold 9 floats per triangle.", nameof(corners));
            return Connect(slots, corners, false, out count, out _, out _);
        }

        readonly struct VertexKey : IEquatable<VertexKey>
        {
            readonly int slot; readonly long x, y, z;
            public VertexKey(int slot, long x, long y, long z) { this.slot = slot; this.x = x; this.y = y; this.z = z; }
            public bool Equals(VertexKey o) => slot == o.slot && x == o.x && y == o.y && z == o.z;
            public override bool Equals(object obj) => obj is VertexKey o && Equals(o);
            public override int GetHashCode() { unchecked { return (((slot * 397) ^ x.GetHashCode()) * 397 ^ y.GetHashCode()) * 397 ^ z.GetHashCode(); } }
        }

        static int[] Connect(int[] slots, float[] values, bool uv, out int count, out int[] cornerVertices, out int vertexCount)
        {
            int n = slots.Length;
            var vertices = new Dictionary<VertexKey, int>();
            int Vertex(int slot, int corner)
            {
                // float × double は double で掛ける（SurfaceRegionIndex が Vector2・Vector3 の成分で掛けるのと同じ値）
                var key = uv ? new VertexKey(slot, (long)Math.Round(values[corner * 2] * UvQuantum), (long)Math.Round(values[corner * 2 + 1] * UvQuantum), 0)
                    : new VertexKey(slot, (long)Math.Round(values[corner * 3] * PositionQuantum), (long)Math.Round(values[corner * 3 + 1] * PositionQuantum), (long)Math.Round(values[corner * 3 + 2] * PositionQuantum));
                if (!vertices.TryGetValue(key, out int id)) vertices.Add(key, id = vertices.Count);
                return id;
            }
            var keys = new long[3 * n]; var owners = new int[3 * n]; var corners = new int[3 * n];
            for (int i = 0; i < n; i++)
            {
                int slot = slots[i];
                int a = Vertex(slot, 3 * i), b = Vertex(slot, 3 * i + 1), c = Vertex(slot, 3 * i + 2);
                keys[3 * i] = Edge(a, b); keys[3 * i + 1] = Edge(b, c); keys[3 * i + 2] = Edge(c, a);
                owners[3 * i] = owners[3 * i + 1] = owners[3 * i + 2] = i;
                corners[3 * i] = a; corners[3 * i + 1] = b; corners[3 * i + 2] = c;
            }
            vertexCount = vertices.Count; cornerVertices = corners;
            Array.Sort(keys, owners);
            var parent = new int[n]; for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int v) { while (parent[v] != v) { parent[v] = parent[parent[v]]; v = parent[v]; } return v; }
            for (int k = 1; k < keys.Length; k++)
                if (keys[k] == keys[k - 1]) { int r1 = Find(owners[k]), r2 = Find(owners[k - 1]); if (r1 != r2) parent[Math.Max(r1, r2)] = Math.Min(r1, r2); }
            // 根はいつも成分のいちばん小さい三角形なので、現れた順の番号は「いちばん小さい三角形の順」
            var of = new int[n]; var ids = new Dictionary<int, int>();
            for (int i = 0; i < n; i++) { int root = Find(i); if (!ids.TryGetValue(root, out int id)) ids.Add(root, id = ids.Count); of[i] = id; }
            count = ids.Count;
            return of;
        }

        static long Edge(int a, int b) => a <= b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
    }

    /// <summary>
    /// ID マップの色の並び（部品の番号 → 0xRRGGBB）。部品が count 個なら、各チャンネル q 段（0 から 255 を q − 1 等分して四捨五入した値）の
    /// 格子のうち灰色（R = G = B。黒・白を含む）を除いた q³ − q 色から count 色を選ぶ。q は count が収まるいちばん小さい数（2 以上）。
    /// <list type="bullet">
    /// <item>どの 2 色も、どれかのチャンネルで <see cref="MinimumSeparation"/>（段の幅。6 色までは 255、24 色までは 127、60 色までは 85、
    /// 120 色までは 63、4080 色までは 17）以上違う。ID の色で選ぶときの許容の幅がこれより小さければ、1 つの色は 1 つの部品だけを選ぶ。</item>
    /// <item>黒（焼いていないテクセルの値）と灰色は使わない。</item>
    /// <item>番号 o の色は、格子の並び（R が上の桁）の (o × s) mod (q³ − q) 番目。s は (q³ − q) / φ² に近い、q³ − q と互いに素な数で、
    /// 続く番号（いちばん小さい三角形の番号が近い部品。隣り合うことが多い）の色が格子の離れた所に来る。</item>
    /// <item>同じ count と番号なら、いつも同じ色（乱数を使わない）。部品の数が変わると色の並びが変わる。</item>
    /// </list>
    /// </summary>
    public static class IdPalette
    {
        /// <summary>count 色を収める格子の段の数 q（2〜256）。</summary>
        public static int Levels(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            for (int q = 2; q <= 256; q++) if ((long)q * q * q - q >= count) return q;
            throw new ArgumentOutOfRangeException(nameof(count), "At most " + (256L * 256 * 256 - 256) + " parts can have their own ID colour.");
        }
        /// <summary>段 i（0〜q − 1）の 8 bit の値。</summary>
        public static int Level(int i, int q) => (i * 255 + (q - 1) / 2) / (q - 1);
        /// <summary>count 色のどの 2 色も、どれかのチャンネルでこれ以上違う（8 bit）。</summary>
        public static int MinimumSeparation(int count)
        {
            int q = Levels(count), min = 255;
            for (int i = 0; i + 1 < q; i++) min = Math.Min(min, Level(i + 1, q) - Level(i, q));
            return min;
        }

        /// <summary>count 個の部品の色（番号の順）。</summary>
        public static int[] Colors(int count)
        {
            int q = Levels(count); long block = (long)q * q + q + 1, candidates = (long)q * q * q - q;
            long stride = Stride(candidates);
            var colors = new int[count];
            for (int o = 0; o < count; o++)
            {
                long m = o * stride % candidates;
                long c = m / (block - 1) * block + 1 + m % (block - 1); // m 番目の灰色でない格子点（灰色は block おき）
                int r = (int)(c / ((long)q * q)), g = (int)(c / q % q), b = (int)(c % q);
                colors[o] = Level(r, q) << 16 | Level(g, q) << 8 | Level(b, q);
            }
            return colors;
        }

        static long Stride(long candidates)
        {
            if (candidates <= 1) return 1;
            long s = Math.Max(1, (long)Math.Round(candidates * 0.38196601125010515)); // 1 / φ²
            while (Gcd(s, candidates) != 1) s++;
            return s;
        }
        static long Gcd(long a, long b) { while (b != 0) { long t = a % b; a = b; b = t; } return a; }
    }
}
