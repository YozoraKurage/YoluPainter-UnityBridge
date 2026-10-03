using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Yozolab.YoluPainter.Core.MeshMaps
{
    /// <summary>
    /// 1 枚の mesh map の由来。モデルの指紋（<see cref="MeshBakeInput.Hash"/>）、UV チャンネル、大きさ、焼き込んだスロット、余白、
    /// 種類ごとの設定（<see cref="MeshBakeSettings.KindKey"/>）、エンジンの版、空間と基準の姿勢。<see cref="ConditionKey"/> はこれらの
    /// SHA-256 で、同じ条件の結果だけが同じ鍵を持つ（違う条件の結果をキャッシュとして共有しない）。
    /// </summary>
    public sealed class MeshMapProvenance
    {
        public MeshMapKind Kind { get; }
        public int EngineVersion { get; }
        public string MeshHash { get; }
        /// <summary>三角形の数・UV・スロットだけの指紋（<see cref="MeshBakeInput.TopologyHash"/>）。</summary>
        public string TopologyHash { get; }
        public int UvChannel { get; }
        public int Width { get; }
        public int Height { get; }
        public int TargetSlot { get; }
        public int Padding { get; }
        /// <summary>テクセルあたり n×n のサブサンプル。</summary>
        public int Antialiasing { get; }
        public string SettingsKey { get; }
        /// <summary>座標の空間。今は "SnapshotWorld"（ワールドの軸、原点はモデルのルート）だけ。</summary>
        public string Space { get; }
        /// <summary>基準の姿勢。今は "StaticSnapshot"（静的なメッシュをそのまま。表示用のポーズでは焼き直さない）だけ。</summary>
        public string Pose { get; }
        /// <summary>焼く元。"Self"（低ポリをそのまま高ポリとして使う自己ベイク）か、"Reference:" に高ポリの指紋と投影の設定
        /// （<see cref="MeshBakeSettings.SourceKey"/>）。</summary>
        public string Source { get; }
        readonly double[] boundsMin, boundsMax;
        public string ConditionKey { get; }

        internal MeshMapProvenance(MeshMapKind kind, int engineVersion, string meshHash, string topologyHash, int uvChannel, int width, int height, int targetSlot, int padding,
            int antialiasing, string settingsKey, string space, string pose, string source, double[] boundsMin, double[] boundsMax)
        {
            Antialiasing = antialiasing;
            Kind = kind; EngineVersion = engineVersion; MeshHash = meshHash ?? ""; TopologyHash = topologyHash ?? ""; UvChannel = uvChannel; Width = width; Height = height;
            TargetSlot = targetSlot; Padding = padding; SettingsKey = settingsKey ?? ""; Space = space ?? ""; Pose = pose ?? ""; Source = source ?? "";
            this.boundsMin = (double[])boundsMin.Clone(); this.boundsMax = (double[])boundsMax.Clone();
            ConditionKey = ComputeConditionKey(kind, engineVersion, MeshHash, uvChannel, width, height, targetSlot, padding, antialiasing, SettingsKey, Space, Pose, Source);
        }

        /// <summary>位置のマップの正規化に使った境界箱（スナップショットの空間）。</summary>
        public double BoundsMin(int axis) => boundsMin[axis];
        public double BoundsMax(int axis) => boundsMax[axis];

        internal static string ComputeConditionKey(MeshMapKind kind, int engineVersion, string meshHash, int uvChannel, int width, int height, int targetSlot,
            int padding, int antialiasing, string settingsKey, string space, string pose, string source)
        {
            string text = "kind=" + kind + "\nengine=" + engineVersion + "\nmesh=" + meshHash + "\nuv=" + uvChannel + "\nsize=" + width + "x" + height
                + "\nslot=" + targetSlot + "\npadding=" + padding + "\nantialiasing=" + antialiasing + "\nsettings=" + settingsKey + "\nspace=" + space + "\npose=" + pose + "\nsource=" + source + "\n";
            using (var sha = SHA256.Create())
            {
                var hex = new StringBuilder(64);
                foreach (byte b in sha.ComputeHash(Encoding.UTF8.GetBytes(text))) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }

        /// <summary>今の条件に対して古い理由（空なら今の条件で焼いたもの）。モデルの指紋が無ければ照合できない。</summary>
        public MeshMapCheck Check(MeshMapExpectation expected)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            var reasons = new List<string>();
            if (EngineVersion != MeshBaker.EngineVersion) reasons.Add("baked by mesh-map engine version " + EngineVersion + ", this version is " + MeshBaker.EngineVersion + ".");
            if (Space != MeshBaker.Space || Pose != MeshBaker.Pose) reasons.Add("baked in space '" + Space + "' / pose '" + Pose + "', which this version does not produce.");
            bool sourceDiffers = expected.Settings != null ? Source != expected.Settings.SourceKey(expected.ReferenceHash)
                : expected.ReferenceHash == null ? Source != MeshBaker.Source : !Source.Contains(expected.ReferenceHash);
            if (sourceDiffers)
                reasons.Add(Source == MeshBaker.Source ? "baked without a high-poly reference; one is chosen now." : expected.ReferenceHash == null ? "baked from a high-poly reference; none is chosen now."
                    : "the high-poly reference or its projection settings changed since the bake.");
            if (Width != expected.Width || Height != expected.Height) reasons.Add("baked at " + Width + "×" + Height + ", the document is " + expected.Width + "×" + expected.Height + ".");
            if (TargetSlot != expected.TargetSlot) reasons.Add("baked for material slot " + TargetSlot + ", the document targets slot " + expected.TargetSlot + ".");
            if (UvChannel != expected.UvChannel) reasons.Add("baked from UV" + UvChannel + ", the document uses UV" + expected.UvChannel + ".");
            if (expected.Settings != null)
            {
                if (Padding != expected.Settings.Padding) reasons.Add("baked with " + Padding + " texels of padding, the settings ask for " + expected.Settings.Padding + ".");
                if (Antialiasing != expected.Settings.Antialiasing) reasons.Add("baked with " + Antialiasing + "×" + Antialiasing + " antialiasing, the settings ask for " + expected.Settings.Antialiasing + "×" + expected.Settings.Antialiasing + ".");
                string wanted = expected.Settings.KindKey(Kind);
                if (wanted != SettingsKey) reasons.Add("bake settings changed (" + SettingsKey + " → " + wanted + ").");
            }
            if (expected.MeshHash != null && expected.MeshHash != MeshHash)
                reasons.Add(expected.TopologyHash != null && expected.TopologyHash == TopologyHash
                    ? "the model's shape changed since the bake (pose, BlendShape or an edit moved vertices or normals; UVs and triangles are the same). Maps are baked in the static base pose and are not re-baked automatically."
                    : "the model changed since the bake (triangles, UVs or material slots differ).");
            if (reasons.Count > 0) return new MeshMapCheck(MeshMapState.Stale, reasons);
            if (expected.MeshHash == null) return new MeshMapCheck(MeshMapState.Unverified, new[] { "no model is loaded to check it against; load the model it was baked from." });
            return new MeshMapCheck(MeshMapState.Current, null);
        }
    }

    /// <summary>
    /// ベイクした 1 枚の mesh map。値は 16 bit の正規化整数（0〜65535 → 0〜1）、チャンネルは並べて持つ（法線・位置は 3、ほかは 1）。
    /// 並びは左下原点の行優先で、ドキュメントの画素と同じ向き。テクセルごとの由来（<see cref="MeshTexelCoverage"/>）も持ち、
    /// Empty のテクセルの値は 0。描くレイヤーではなく派生物で、作った後は変えない（読むだけ）。
    /// </summary>
    public sealed class BakedMeshMap
    {
        internal readonly ushort[] Data;
        internal readonly byte[] Coverage;
        public MeshMapProvenance Provenance { get; }
        public MeshMapKind Kind => Provenance.Kind;
        public int Width => Provenance.Width;
        public int Height => Provenance.Height;
        public int Channels { get; }
        public long PayloadBytes => (long)Data.Length * 2 + Coverage.Length;

        internal BakedMeshMap(MeshMapProvenance provenance, ushort[] data, byte[] coverage)
        {
            Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
            Channels = ChannelCount(provenance.Kind);
            long texels = (long)provenance.Width * provenance.Height;
            if (coverage == null || coverage.LongLength != texels) throw new ArgumentException("Coverage does not match the map size.", nameof(coverage));
            if (data == null || data.LongLength != texels * Channels) throw new ArgumentException("Data does not match the map size.", nameof(data));
            Data = data; Coverage = coverage;
        }

        public static int ChannelCount(MeshMapKind kind) =>
            kind == MeshMapKind.WorldNormal || kind == MeshMapKind.Position || kind == MeshMapKind.TangentNormal || kind == MeshMapKind.Id || kind == MeshMapKind.BentNormal ? 3 : 1;

        public MeshTexelCoverage CoverageAt(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return MeshTexelCoverage.Empty;
            return (MeshTexelCoverage)Coverage[y * Width + x];
        }
        /// <summary>0〜1 の値。法線は <see cref="Signed"/> で −1〜1 に戻す。範囲外と Empty は 0。</summary>
        public float Value(int x, int y, int channel = 0)
        {
            if ((uint)channel >= (uint)Channels) throw new ArgumentOutOfRangeException(nameof(channel));
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return 0;
            return Data[(y * Width + x) * Channels + channel] * (1f / 65535);
        }
        public ushort RawValue(int x, int y, int channel = 0)
        {
            if ((uint)channel >= (uint)Channels) throw new ArgumentOutOfRangeException(nameof(channel));
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return 0;
            return Data[(y * Width + x) * Channels + channel];
        }
        public static float Signed(float value) => value * 2 - 1;

        /// <summary>矩形を 0〜1 の float で読む（Generator がタイルごとに使う）。values は width×height×Channels 以上、左下原点の行優先で
        /// チャンネルを並べる。マップの外は 0 と Empty。</summary>
        public void ReadRegion(int x0, int y0, int width, int height, float[] values, MeshTexelCoverage[] coverage = null)
        {
            if (width < 0 || height < 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (values == null || values.LongLength < (long)width * height * Channels) throw new ArgumentException("The value buffer is too small.", nameof(values));
            if (coverage != null && coverage.LongLength < (long)width * height) throw new ArgumentException("The coverage buffer is too small.", nameof(coverage));
            const float scale = 1f / 65535;
            for (int y = 0; y < height; y++)
            {
                int sy = y0 + y;
                for (int x = 0; x < width; x++)
                {
                    int sx = x0 + x, o = (y * width + x) * Channels;
                    bool inside = (uint)sx < (uint)Width && (uint)sy < (uint)Height;
                    int i = sy * Width + sx;
                    for (int c = 0; c < Channels; c++) values[o + c] = inside ? Data[i * Channels + c] * scale : 0;
                    if (coverage != null) coverage[y * width + x] = inside ? (MeshTexelCoverage)Coverage[i] : MeshTexelCoverage.Empty;
                }
            }
        }
        /// <summary>ドキュメントのタイル（tileSize 四方）を読む。</summary>
        public void ReadTile(TileCoord tile, int tileSize, float[] values, MeshTexelCoverage[] coverage = null)
        {
            if (tileSize <= 0) throw new ArgumentOutOfRangeException(nameof(tileSize));
            ReadRegion(tile.X * tileSize, tile.Y * tileSize, tileSize, tileSize, values, coverage);
        }

        /// <summary>表示用の RGBA8（左下原点）。1 チャンネルは灰色、3 チャンネルは RGB。Empty は透明。coverageView なら値でなく由来を
        /// 色で（覆う = 緑、UV の重なり = 赤、余白 = 青、空 = 透明）。保存や計算には使わない（8 bit に丸める）。</summary>
        public byte[] ToRgba8(bool coverageView = false)
        {
            var result = new byte[Coverage.Length * 4];
            for (int i = 0; i < Coverage.Length; i++)
            {
                var state = (MeshTexelCoverage)Coverage[i]; int o = i * 4;
                if (state == MeshTexelCoverage.Empty) continue;
                if (coverageView)
                {
                    switch (state)
                    {
                        case MeshTexelCoverage.Covered: result[o] = 40; result[o + 1] = 170; result[o + 2] = 70; break;
                        case MeshTexelCoverage.Overlap: result[o] = 230; result[o + 1] = 40; result[o + 2] = 40; break;
                        default: result[o] = 60; result[o + 1] = 100; result[o + 2] = 220; break;
                    }
                    result[o + 3] = 255; continue;
                }
                if (Channels == 1) { byte v = (byte)((Data[i] * 255 + 32767) / 65535); result[o] = result[o + 1] = result[o + 2] = v; }
                else for (int c = 0; c < 3; c++) result[o + c] = (byte)((Data[i * 3 + c] * 255 + 32767) / 65535);
                result[o + 3] = 255;
            }
            return result;
        }

        /// <summary>由来ごとのテクセルの数。</summary>
        public long CountCoverage(MeshTexelCoverage state)
        {
            long n = 0; byte value = (byte)state;
            foreach (byte b in Coverage) if (b == value) n++;
            return n;
        }
    }

    /// <summary>
    /// ドキュメントが持つ mesh map の組（種類ごとに 1 枚、最後に焼いたもの）。照合は <see cref="Check"/>、使う側は
    /// <see cref="TryGetUsable"/>（今の条件で焼いたものだけ返す）か <see cref="TryGetExact"/>（条件の鍵が完全に同じものだけ）を使い、
    /// 古いマップを黙って使わない。履歴（Undo）には入らない派生物。
    /// </summary>
    public sealed class MeshMapSet
    {
        readonly Dictionary<MeshMapKind, BakedMeshMap> maps = new Dictionary<MeshMapKind, BakedMeshMap>();
        /// <summary>中身が変わるたびに増える（表示の作り直しに使う）。</summary>
        public long Revision { get; private set; }
        public int Count => maps.Count;
        public IReadOnlyList<BakedMeshMap> Maps => maps.Keys.OrderBy(k => k).Select(k => maps[k]).ToList();

        public bool TryGet(MeshMapKind kind, out BakedMeshMap map) => maps.TryGetValue(kind, out map);

        /// <summary>同じ種類のマップを置き換える（ほかの種類は残す）。</summary>
        public void Put(IEnumerable<BakedMeshMap> baked)
        {
            if (baked == null) throw new ArgumentNullException(nameof(baked));
            var list = baked.ToList();
            if (list.Any(m => m == null)) throw new ArgumentException("Null mesh map.", nameof(baked));
            if (list.Select(m => m.Kind).Distinct().Count() != list.Count) throw new ArgumentException("Two maps of the same kind.", nameof(baked));
            if (list.Count == 0) return;
            foreach (var map in list) maps[map.Kind] = map;
            Revision++;
        }
        public bool Remove(MeshMapKind kind) { bool removed = maps.Remove(kind); if (removed) Revision++; return removed; }
        public void Clear() { if (maps.Count == 0) return; maps.Clear(); Revision++; }

        public MeshMapCheck Check(MeshMapKind kind, MeshMapExpectation expected)
        {
            if (!maps.TryGetValue(kind, out var map)) return new MeshMapCheck(MeshMapState.Missing, new[] { kind + " has not been baked." });
            return map.Provenance.Check(expected);
        }
        /// <summary>今の条件で焼いたものだけを返す。古い・照合できない・無いときは false と理由。</summary>
        public bool TryGetUsable(MeshMapKind kind, MeshMapExpectation expected, out BakedMeshMap map, out string reason)
        {
            var check = Check(kind, expected);
            if (check.State == MeshMapState.Current) { map = maps[kind]; reason = null; return true; }
            map = null; reason = kind + " is " + check.State.ToString().ToLowerInvariant() + ": " + string.Join(" ", check.Reasons);
            return false;
        }
        /// <summary>条件の鍵（<see cref="MeshBaker.ConditionKey"/>）が完全に同じマップだけを返す。</summary>
        public bool TryGetExact(MeshMapKind kind, string conditionKey, out BakedMeshMap map)
        {
            if (maps.TryGetValue(kind, out map) && map.Provenance.ConditionKey == conditionKey) return true;
            map = null; return false;
        }
    }
}
