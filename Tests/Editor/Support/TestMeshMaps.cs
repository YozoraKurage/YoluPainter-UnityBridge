using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>試験用の mesh map: 値を式で決めた合成のマップを、保存形式（<see cref="MeshMapBinary"/> の版 2）のバイト列に書いてから
    /// 公開の読み手で読む（Core の internal はテストから見えない。読み手の確かめもそのまま通る）。</summary>
    internal static class TestMeshMaps
    {
        /// <summary>value(x, y, channel) は 0〜1（16 bit に丸める）。coverage が null なら全部 Covered。settingsKey を変えると条件の鍵が変わる
        /// （同じ種類の別のベイク）。境界箱は既定で 0〜1 の立方体。</summary>
        public static BakedMeshMap Make(MeshMapKind kind, int width, int height, Func<int, int, int, double> value, Func<int, int, MeshTexelCoverage> coverage = null,
            string settingsKey = "test", double[] min = null, double[] max = null)
        {
            min = min ?? new double[] { 0, 0, 0 }; max = max ?? new double[] { 1, 1, 1 };
            int channels = BakedMeshMap.ChannelCount(kind); long texels = (long)width * height;
            var raw = new byte[texels * (1 + 2 * channels)];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) raw[(long)y * width + x] = (byte)(coverage == null ? MeshTexelCoverage.Covered : coverage(x, y));
            for (int c = 0; c < channels; c++)
            {
                long high = texels * (1 + 2 * c), low = high + texels;
                for (int y = 0; y < height; y++)
                {
                    int previous = 0;
                    for (int x = 0; x < width; x++)
                    {
                        long i = (long)y * width + x;
                        int v = (int)Math.Round(Math.Max(0, Math.Min(1, value(x, y, c))) * 65535);
                        if (raw[i] == (byte)MeshTexelCoverage.Empty) v = 0; // 焼いたマップと同じく、空のテクセルの値は 0
                        int delta = (v - previous) & 0xFFFF; previous = v;
                        raw[high + i] = (byte)(delta >> 8); raw[low + i] = (byte)delta;
                    }
                }
            }
            byte[] payload;
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, true)) deflate.Write(raw, 0, raw.Length);
                payload = output.ToArray();
            }
            using (var stream = new MemoryStream())
            {
                using (var w = new BinaryWriter(stream, new UTF8Encoding(false), true))
                {
                    w.Write(Encoding.ASCII.GetBytes("YLPMMAP\0")); w.Write(2); w.Write((int)kind); w.Write(MeshBaker.EngineVersion);
                    Str(w, "mesh-" + width); Str(w, "topology"); w.Write(0); w.Write(width); w.Write(height); w.Write(0); w.Write(4); w.Write(1); w.Write(channels);
                    Str(w, settingsKey); Str(w, MeshBaker.Space); Str(w, MeshBaker.Pose); Str(w, MeshBaker.Source);
                    foreach (var v in min) w.Write(v);
                    foreach (var v in max) w.Write(v);
                    w.Write(payload.Length); w.Write(payload);
                }
                return MeshMapBinary.Read(stream.ToArray());
            }
        }
        static void Str(BinaryWriter w, string s) { var b = Encoding.UTF8.GetBytes(s); w.Write(b.Length); w.Write(b); }

        /// <summary>16 bit に丸めた値（マップが持つ値と同じ）。</summary>
        public static double Q(double v) => Math.Round(Math.Max(0, Math.Min(1, v)) * 65535) / 65535.0;
    }

    /// <summary>試験用の Generator の入力: 種類ごとのマップか理由を持ち、変えるたびに版を上げる。</summary>
    internal sealed class TestGeneratorInputs : IGeneratorInputs
    {
        readonly Dictionary<MeshMapKind, BakedMeshMap> maps = new Dictionary<MeshMapKind, BakedMeshMap>();
        readonly Dictionary<MeshMapKind, string> reasons = new Dictionary<MeshMapKind, string>();
        public long Revision { get; private set; } = 1;
        public int Calls { get; private set; }
        public TestGeneratorInputs Put(BakedMeshMap map) { maps[map.Kind] = map; reasons.Remove(map.Kind); Revision++; return this; }
        public TestGeneratorInputs Refuse(MeshMapKind kind, string reason) { maps.Remove(kind); reasons[kind] = reason; Revision++; return this; }
        public void Touch() => Revision++;
        public bool TryGetMap(MeshMapKind kind, out BakedMeshMap map, out string reason)
        {
            Calls++;
            if (maps.TryGetValue(kind, out map)) { reason = null; return true; }
            reason = reasons.TryGetValue(kind, out var r) ? r : kind + " has not been baked."; return false;
        }
    }
}
