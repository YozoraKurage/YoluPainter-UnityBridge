using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Psd;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>書き出し側を使わずに PSD のバイト列を組み立てる（読み手を自分の書き手だけで試さないため）。レコードは下から上の順。</summary>
    internal static class PsdFixture
    {
        internal sealed class Record
        {
            public int Top, Left, Width, Height, Id;
            public byte[] Rgba = new byte[0];          // 上から下、straight RGBA
            public string Blend = "norm", Name = "L";
            public byte Opacity = 255, Clipping, Flags;
            public byte[] MaskData, MaskValues; public int MaskWidth, MaskHeight;
            public byte[] Ranges;
            public bool Rle, NoLuni;
            public readonly List<KeyValuePair<string, byte[]>> Tags = new List<KeyValuePair<string, byte[]>>();
        }

        internal static void U16(Stream s, int n) { s.WriteByte((byte)(n >> 8)); s.WriteByte((byte)n); }
        internal static void U32(Stream s, long n) { U16(s, (int)(n >> 16)); U16(s, (int)n); }
        internal static void Key(Stream s, string key) { var b = Encoding.ASCII.GetBytes(key); if (b.Length != 4) throw new ArgumentException(key); s.Write(b, 0, 4); }
        internal static void Block(Stream s, byte[] bytes) { U32(s, bytes.Length); s.Write(bytes, 0, bytes.Length); }
        internal static byte[] Bytes(Action<Stream> write) { using (var s = new MemoryStream()) { write(s); return s.ToArray(); } }

        /// <summary>1 チャンネル（圧縮の種類 + データ）。RLE は行が全部同じ値なら繰り返し、他は素の並び（幅 128 以下）。</summary>
        internal static byte[] Plane(byte[] values, int width, int height, bool rle)
        {
            return Bytes(s =>
            {
                U16(s, rle ? 1 : 0);
                if (!rle) { s.Write(values, 0, values.Length); return; }
                var rows = new List<byte[]>();
                for (int y = 0; y < height; y++)
                {
                    var row = values.Skip(y * width).Take(width).ToArray();
                    rows.Add(row.Length > 1 && row.All(v => v == row[0]) ? new[] { (byte)(1 - row.Length), row[0] } : new[] { (byte)(row.Length - 1) }.Concat(row).ToArray());
                }
                foreach (var row in rows) U16(s, row.Length);
                foreach (var row in rows) s.Write(row, 0, row.Length);
            });
        }

        internal static byte[] Fill(int width, int height, Rgba32 c) => Enumerable.Range(0, width * height).SelectMany(_ => new[] { c.R, c.G, c.B, c.A }).ToArray();
        internal static byte[] Setting(int value) => new byte[] { (byte)value, 0, 0, 0 };
        /// <summary>lsct（区切りの設定）: 種類、必要なら "8BIM" + 描画モードのキー。</summary>
        internal static byte[] Section(int type, string key = null) => Bytes(s => { U32(s, type); if (key != null) { Key(s, "8BIM"); Key(s, key); } });

        internal static Record Raster(int id, string name, int left, int top, int width, int height, Rgba32 color, string blend = "norm")
            => new Record { Id = id, Name = name, Left = left, Top = top, Width = width, Height = height, Rgba = Fill(width, height, color), Blend = blend };
        /// <summary>フォルダーのレコード（グループの一番上）。通過は Photoshop と同じく、レコードに "norm"、lsct に "pass"。</summary>
        internal static Record Folder(int id, string name, string key, int type = 1, byte opacity = 255)
        {
            var r = new Record { Id = id, Name = name, Blend = key == "pass" ? "norm" : key, Opacity = opacity, Flags = 8 | 16 };
            r.Tags.Add(new KeyValuePair<string, byte[]>("lsct", Section(type, key)));
            return r;
        }
        /// <summary>グループの下端の区切り "&lt;/Layer group&gt;"。id 0 なら lyid を付けない。</summary>
        internal static Record Divider(int id = 0, string key = "lsct")
        {
            var r = new Record { Id = id, Name = "</Layer group>", Flags = 8 | 16 };
            r.Tags.Add(new KeyValuePair<string, byte[]>(key, Section(3)));
            return r;
        }

        static byte[] Matte(byte[] straight)
        {
            var m = new byte[straight.Length];
            for (int i = 0; i < straight.Length; i += 4)
            {
                for (int c = 0; c < 3; c++) m[i + c] = (byte)Math.Floor(straight[i + c] * straight[i + 3] / 255.0 + (255 - straight[i + 3]) + 0.5);
                m[i + 3] = straight[i + 3];
            }
            return m;
        }

        internal static byte[] Build(int width, int height, IList<Record> bottomUp, byte[] mergedStraight, byte[] resources = null, byte[] documentTags = null)
        {
            using (var info = new MemoryStream()) using (var layers = new MemoryStream()) using (var psd = new MemoryStream())
            {
                U16(info, -bottomUp.Count);
                var channelData = new List<byte[]>();
                foreach (var l in bottomUp)
                {
                    var channels = new List<KeyValuePair<short, byte[]>>();
                    for (int c = 0; c < 4; c++)
                    {
                        var plane = new byte[l.Width * l.Height]; for (int i = 0; i < plane.Length; i++) plane[i] = l.Rgba[i * 4 + c];
                        channels.Add(new KeyValuePair<short, byte[]>((short)(c == 3 ? -1 : c), Plane(plane, l.Width, l.Height, l.Rle)));
                    }
                    if (l.MaskValues != null) channels.Add(new KeyValuePair<short, byte[]>(-2, Plane(l.MaskValues, l.MaskWidth, l.MaskHeight, l.Rle)));
                    U32(info, l.Top); U32(info, l.Left); U32(info, l.Top + l.Height); U32(info, l.Left + l.Width); U16(info, channels.Count);
                    foreach (var c in channels) { U16(info, c.Key); U32(info, c.Value.Length); channelData.Add(c.Value); }
                    Key(info, "8BIM"); Key(info, l.Blend); info.WriteByte(l.Opacity); info.WriteByte(l.Clipping); info.WriteByte(l.Flags); info.WriteByte(0);
                    Block(info, Bytes(extra =>
                    {
                        Block(extra, l.MaskData ?? new byte[0]);
                        Block(extra, l.Ranges ?? new byte[0]);
                        var name = Encoding.ASCII.GetBytes(l.Name); extra.WriteByte((byte)name.Length); extra.Write(name, 0, name.Length);
                        for (int p = (name.Length + 1) % 4; p != 0 && p < 4; p++) extra.WriteByte(0);
                        if (l.Id != 0) { Key(extra, "8BIM"); Key(extra, "lyid"); U32(extra, 4); U32(extra, l.Id); }
                        foreach (var tag in l.Tags) { Key(extra, "8BIM"); Key(extra, tag.Key); Block(extra, tag.Value); if ((tag.Value.Length & 1) != 0) extra.WriteByte(0); }
                    }));
                }
                foreach (var data in channelData) info.Write(data, 0, data.Length);
                if ((info.Length & 1) != 0) info.WriteByte(0);
                Block(layers, info.ToArray()); U32(layers, 0);
                if (documentTags != null) layers.Write(documentTags, 0, documentTags.Length);
                Key(psd, "8BPS"); U16(psd, 1); U32(psd, 0); U16(psd, 0); U16(psd, 4); U32(psd, height); U32(psd, width); U16(psd, 8); U16(psd, 3);
                U32(psd, 0); Block(psd, resources ?? new byte[0]); Block(psd, layers.ToArray()); U16(psd, 0);
                var merged = Matte(mergedStraight);
                for (int c = 0; c < 4; c++) for (int i = 0; i < width * height; i++) psd.WriteByte(merged[i * 4 + c]);
                return psd.ToArray();
            }
        }

        internal static string Show(PsdReadResult r) => string.Join("; ", r.Diagnostics.Select(x => x.ToString()));
    }

    /// <summary>PSD のグループ（フォルダー）の読み書き。ネイティブのグループ → PSD → ネイティブの往復で構造と合成が変わらないこと、
    /// 書き出し側を使わずに組み立てたフォルダーを読めること、壊れた区切りを拒むことを確かめる。</summary>
    public sealed class PsdGroupTests
    {
        static PaintLayer Raster(PaintDocument d, string name, Func<int, int, Rgba32> pixel)
        {
            var layer = d.AddLayer(name); var surface = layer.GetChannel(PaintChannel.Color);
            for (int y = 0; y < d.Height; y++) for (int x = 0; x < d.Width; x++) { var c = pixel(x, y); if (c != Rgba32.Transparent) surface.SetPixel(x, y, c); }
            return layer;
        }
        static Rgba32 Gradient(int x, int y) => new Rgba32((byte)(x * 10 + 15), (byte)(y * 14 + 20), (byte)(200 - x * 5), 255);
        static Rgba32 Soft(int x, int y) => new Rgba32((byte)(255 - x * 9), (byte)(x * 7 + y * 5), (byte)(y * 13 + 40), (byte)(x < 3 ? 0 : 60 + x * 8 + y * 3));
        static Rgba32 Spots(int x, int y) => (x * 3 + y * 5) % 7 < 3 ? new Rgba32((byte)(40 + y * 9), 200, (byte)(x * 11), (byte)(90 + x * 6)) : Rgba32.Transparent;

        static PaintLayer Group(PaintDocument d, string name, LayerBlendMode mode, params PaintLayer[] members)
        {
            var g = d.GroupLayers(members.Select(m => m.Id).ToArray(), name);
            if (mode != LayerBlendMode.PassThrough) d.SetLayerBlendMode(g.Id, mode);
            return g;
        }

        static void HideRow(PaintDocument d, Guid layer, int y, byte hide) { var m = d.GetLayer(layer).Mask ?? d.AddLayerMask(layer); for (int x = 0; x < d.Width; x++) m.Surface.SetPixel(x, y, new Rgba32(0, 0, 0, hide)); }

        /// <summary>ネイティブの構造（下から上の並び、親、種類、描画モード、クリッピング、表示、不透明度、マスク）を文字列に。</summary>
        static string Structure(PaintDocument d)
        {
            var names = d.Layers.ToDictionary(l => l.Id, l => l.Name);
            return string.Join("\n", d.Layers.Select(l => l.Name + " parent=" + (l.ParentId == Guid.Empty ? "-" : names[l.ParentId]) + " kind=" + l.Kind + " mode=" + l.BlendMode
                + " clip=" + l.Clipping + " visible=" + l.Visible + " opacity=" + Math.Round(l.Opacity * 255) + " mask=" + (l.Mask == null ? "-" : l.Mask.Enabled + "/" + Math.Round(l.Mask.Density * 255))));
        }

        static PaintDocument RoundTrip(PaintDocument d, out PsdReadResult read, out byte[] bytes)
        {
            bytes = PsdCodec.Write(PsdBridge.Export(d, PaintChannel.Color));
            read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), PsdFixture.Show(read));
            Assert.That(read.Diagnostics, Is.Empty, "our own folders re-read without CompositeDiffers or anything else");
            var imported = PsdBridge.Import(read);
            imported.ValidateStructure();
            Assert.That(Structure(imported), Is.EqualTo(Structure(d)), "order, nesting and attributes");
            Assert.That(imported.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)), "byte-identical composite after re-import");
            Assert.That(PsdCodec.WriteEdited(read, read.Document), Is.EqualTo(bytes), "an unchanged editable import is written back identically");
            return imported;
        }

        [Test] public void NestedPassThroughAndIsolatedGroupsWithMasksAndClippingOntoGroupsRoundTrip()
        {
            var d = new PaintDocument(24, 16, 8);
            Raster(d, "bg", Gradient);
            var a1 = Raster(d, "a1", Soft); d.SetLayerBlendMode(a1.Id, LayerBlendMode.Multiply);
            var b1 = Raster(d, "b1", Spots); var b2 = Raster(d, "b2", (x, y) => new Rgba32(250, 30, 30, 200)); d.SetLayerClipping(b2.Id, true);
            var inner = Group(d, "inner", LayerBlendMode.Screen, b1, b2); d.SetLayerOpacity(inner.Id, 180 / 255.0);
            var a2 = Raster(d, "a2", (x, y) => x > 12 ? new Rgba32(0, 0, 255, 128) : Rgba32.Transparent);
            var outer = Group(d, "outer", LayerBlendMode.PassThrough, a1, inner, a2);
            d.SetLayerOpacity(outer.Id, 200 / 255.0); HideRow(d, outer.Id, 3, 255); HideRow(d, outer.Id, 4, 128);
            var c1 = Raster(d, "c1", Soft); var baseGroup = Group(d, "clip base", LayerBlendMode.Overlay, c1);
            var clipped = Raster(d, "clipped onto group", (x, y) => new Rgba32(10, 240, 10, 255)); d.SetLayerClipping(clipped.Id, true); d.SetLayerBlendMode(clipped.Id, LayerBlendMode.Color);
            var e1 = Raster(d, "e1", Spots); Group(d, "pass group", LayerBlendMode.PassThrough, e1); // クリッピングしたグループは書き出さない（下のテスト）
            var h1 = Raster(d, "h1", (x, y) => new Rgba32(255, 255, 0, 255)); var hidden = Group(d, "hidden", LayerBlendMode.Normal, h1); d.SetLayerVisibility(hidden.Id, false);
            d.AddGroup("empty");
            Raster(d, "top", (x, y) => x == y ? new Rgba32(255, 255, 255, 255) : Rgba32.Transparent);
            d.ValidateStructure();

            var imported = RoundTrip(d, out var read, out _);
            var top = read.Document.Layers;
            Assert.That(top.Select(l => l.Name), Is.EqualTo(new[] { "top", "empty", "hidden", "pass group", "clipped onto group", "clip base", "outer", "bg" }));
            var o = top[6]; Assert.That(o.IsGroup && o.BlendMode == LayerBlendMode.PassThrough && o.Opacity == 200 && o.Mask != null, Is.True);
            Assert.That(o.Children.Select(l => l.Name), Is.EqualTo(new[] { "a2", "inner", "a1" }));
            Assert.That(o.Children[1].Children.Select(l => l.Name), Is.EqualTo(new[] { "b2", "b1" }));
            Assert.That(o.Children[1].BlendMode, Is.EqualTo(LayerBlendMode.Screen));
            Assert.That(top[1].Children, Is.Empty, "an empty group stays a group");
            Assert.That(top[4].Clipping && !top[5].Clipping && top[5].IsGroup, Is.True, "a layer clipped onto a group");
            var again = PsdCodec.Read(PsdCodec.Write(PsdBridge.Export(imported, PaintChannel.Color)));
            Assert.That(Ids(again.Document.Layers), Is.EqualTo(Ids(read.Document.Layers)), "layer and divider IDs survive native import and re-export");
        }

        static IEnumerable<string> Ids(List<PsdRasterLayer> topDown)
        {
            foreach (var l in topDown)
            {
                yield return l.Name + "#" + l.Id + (l.IsGroup ? "/" + l.DividerId : "");
                if (l.IsGroup) foreach (var c in Ids(l.Children)) yield return "  " + c;
            }
        }

        [Test] public void EveryIsolatedModeAndPassThroughOnAGroupRoundTrips()
        {
            foreach (var mode in Enum.GetValues(typeof(LayerBlendMode)).Cast<LayerBlendMode>().Where(m => m == LayerBlendMode.PassThrough || PsdCodec.BlendKey(m) != null))
            {
                var d = new PaintDocument(16, 12, 8);
                Raster(d, "below", Gradient);
                var x1 = Raster(d, "x1", Soft); var x2 = Raster(d, "x2", Spots); d.SetLayerBlendMode(x2.Id, LayerBlendMode.Difference);
                var g = Group(d, "g", mode, x1, x2); d.SetLayerOpacity(g.Id, 170 / 255.0);
                RoundTrip(d, out var read, out var bytes);
                Assert.That(read.Document.Layers[0].BlendMode, Is.EqualTo(mode));
                string key = mode == LayerBlendMode.PassThrough ? "pass" : PsdCodec.BlendKey(mode);
                Assert.That(IndexOf(bytes, "lsct\0\0\0\u000c\0\0\0\u00018BIM" + key), Is.GreaterThan(0), mode + ": the folder's section divider carries '" + key + "'");
            }
        }

        [Test] public void DeepNestingRoundTripsAndTheDepthBudgetIsEnforced()
        {
            var d = new PaintDocument(8, 8, 8);
            var layer = Raster(d, "deep", Soft); Raster(d, "bg", Gradient); d.MoveLayer(layer.Id, 1);
            PaintLayer g = null;
            for (int i = 0; i < 12; i++) { g = d.GroupLayers(new[] { (g ?? layer).Id }, "g" + i); if (i % 3 == 1) d.SetLayerBlendMode(g.Id, LayerBlendMode.Multiply); }
            Assert.That(d.DepthOf(layer.Id), Is.EqualTo(12));
            RoundTrip(d, out _, out var bytes);
            var limited = PsdCodec.Read(bytes, new PsdLimits { MaxGroupDepth = 11 });
            Assert.That(limited.Mode, Is.EqualTo(PsdCompatibilityMode.Rejected));
            Assert.That(limited.Diagnostics.Single().Message, Does.Contain("depth budget"));
            Assert.That(limited.CopyOriginalBytes(), Is.EqualTo(bytes));
            Assert.That(() => PsdCodec.Write(PsdBridge.Export(d, PaintChannel.Color), new PsdLimits { MaxGroupDepth = 11 }), Throws.ArgumentException);
        }

        [Test] public void ExportStillRefusesFillLayersAndInexactAdjustmentsInsideGroups()
        {
            var d = new PaintDocument(8, 8, 8); var a = Raster(d, "a", Gradient);
            var fill = d.AddFillLayer("fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(1, 2, 3) } });
            Group(d, "g", LayerBlendMode.PassThrough, a, fill);
            Assert.That(() => PsdBridge.Export(d, PaintChannel.Color), Throws.InvalidOperationException.With.Message.Contains("fill layers"));
            var e = new PaintDocument(8, 8, 8); var b = Raster(e, "b", Gradient); var adj = e.AddAdjustmentLayer("levels", AdjustmentSettings.Levels(0.3));
            Group(e, "g", LayerBlendMode.Normal, b, adj);
            Assert.That(() => PsdBridge.Export(e, PaintChannel.Color), Throws.InvalidOperationException.With.Message.Contains("between them"), "Levels between PSD's whole steps are not rounded silently");
            var f = new PaintDocument(8, 8, 8); var c = Raster(f, "c", Gradient); var g2 = Group(f, "g", LayerBlendMode.PassThrough, c);
            f.AddLayerMask(g2.Id); f.SetLayerMaskInverted(g2.Id, true);
            Assert.That(() => PsdBridge.Export(f, PaintChannel.Color), Throws.InvalidOperationException.With.Message.Contains("inversion"));
        }

        [Test] public void ClippedGroupsAreReadButNotExported()
        {
            var d = new PaintDocument(8, 8, 8); Raster(d, "base", Gradient);
            var e1 = Raster(d, "e1", Spots); var g = Group(d, "clipped group", LayerBlendMode.PassThrough, e1); d.SetLayerClipping(g.Id, true);
            Assert.That(() => PsdBridge.Export(d, PaintChannel.Color), Throws.InvalidOperationException.With.Message.Contains("clipped"), "Photoshop's handling of a clipped folder is unverified");
            // CLIP STUDIO PAINT はフォルダーのクリッピングを使う。読み込みは受け入れる
            var records = Folders(); records[6].Clipping = 1;
            var read = PsdCodec.Read(PsdFixture.Build(2, 1, records, FoldersComposite));
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), PsdFixture.Show(read));
            Assert.That(read.Document.Layers[1].Clipping, Is.True);
            var native = PsdBridge.Import(read);
            Assert.That(native.Layers.Single(l => l.Name == "outer").Clipping, Is.True);
            Assert.That(read.Diagnostics.All(PsdCodec.IsInformational), Is.True, PsdFixture.Show(read));
        }

        // ---- 書き出し側を使わずに組み立てたフォルダー ----

        /// <summary>2x1 のキャンバス。下から: 背景 (100,100,100)、[区切り]、乗算の child (x=0 のみ)、[区切り (lsdk)]、inner の青 (x=1 のみ)、
        /// フォルダー inner（分離・通常・不透明度 128）、フォルダー outer（通過。レコードは "norm"、lsct は "pass"）、上の透明なレイヤー。</summary>
        static List<PsdFixture.Record> Folders()
        {
            var bg = PsdFixture.Raster(1, "bg", 0, 0, 2, 1, new Rgba32(100, 100, 100, 255));
            var child = PsdFixture.Raster(2, "child", 0, 0, 1, 1, new Rgba32(200, 100, 50, 255), "mul ");
            var blue = PsdFixture.Raster(3, "blue", 1, 0, 1, 1, new Rgba32(0, 0, 255, 255));
            var top = PsdFixture.Raster(4, "top", 0, 0, 2, 1, Rgba32.Transparent);
            return new List<PsdFixture.Record>
            {
                bg, PsdFixture.Divider(0), child, PsdFixture.Divider(20, "lsdk"), blue, PsdFixture.Folder(5, "inner", "norm", 1, 128), PsdFixture.Folder(6, "outer", "pass"), top,
            };
        }

        /// <summary>手計算: x=0 は (100×200, 100×100, 100×50)/255 = (78,39,20)。x=1 は青を 128/255 で重ねて
        /// (100×127/255, 同, 100×127/255 + 128) = (50,50,178)。</summary>
        static readonly byte[] FoldersComposite = { 78, 39, 20, 255, 50, 50, 178, 255 };

        [Test] public void AHandBuiltPsdWithNestedFoldersIsEditableAndCompositesAsComputedByHand()
        {
            byte[] bytes = PsdFixture.Build(2, 1, Folders(), FoldersComposite);
            var read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), PsdFixture.Show(read));
            Assert.That(read.Diagnostics, Is.Empty, "the hand-computed merged image matches");
            var top = read.Document.Layers;
            Assert.That(top.Select(l => l.Name), Is.EqualTo(new[] { "top", "outer", "bg" }));
            var outer = top[1]; Assert.That(outer.BlendMode, Is.EqualTo(LayerBlendMode.PassThrough)); Assert.That(outer.Id, Is.EqualTo(6)); Assert.That(outer.DividerId, Is.EqualTo(0));
            Assert.That(outer.Children.Select(l => l.Name), Is.EqualTo(new[] { "inner", "child" }));
            var inner = outer.Children[0]; Assert.That(inner.BlendMode, Is.EqualTo(LayerBlendMode.Normal)); Assert.That(inner.Opacity, Is.EqualTo(128)); Assert.That(inner.DividerId, Is.EqualTo(20));
            Assert.That(inner.Children.Single().Name, Is.EqualTo("blue"));
            var native = PsdBridge.Import(read);
            Assert.That(native.Layers.Select(l => l.Name), Is.EqualTo(new[] { "bg", "child", "blue", "inner", "outer", "top" }), "native order: a group's contents directly below it");
            Assert.That(native.Layers[2].ParentId, Is.EqualTo(native.Layers[3].Id)); Assert.That(native.Layers[3].ParentId, Is.EqualTo(native.Layers[4].Id)); Assert.That(native.Layers[1].ParentId, Is.EqualTo(native.Layers[4].Id));
            Assert.That(native.Layers[4].BlendMode, Is.EqualTo(LayerBlendMode.PassThrough));
            Assert.That(native.Composite(PaintChannel.Color), Is.EqualTo(FoldersComposite), "1 row: top-down equals bottom-up");
            Assert.That(PsdCodec.Read(PsdCodec.WriteEdited(read, read.Document)).Diagnostics, Is.Empty);
            var rewritten = PsdCodec.Read(PsdCodec.Write(PsdBridge.Export(native, PaintChannel.Color)));
            Assert.That(rewritten.Document.Layers[1].Id, Is.EqualTo(6), "PSD layer IDs are carried through the native document");
            Assert.That(rewritten.Document.Layers[1].Children[0].DividerId, Is.EqualTo(20), "and so are divider IDs");
        }

        [Test] public void AClosedFolderIsEditableAndReportedAsNotCarried()
        {
            var records = Folders(); records[6] = PsdFixture.Folder(6, "outer", "pass", 2);
            var read = PsdCodec.Read(PsdFixture.Build(2, 1, records, FoldersComposite));
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), PsdFixture.Show(read));
            var note = read.Diagnostics.Single();
            Assert.That(note.Code, Is.EqualTo(PsdCodec.NotCarriedIntoExport)); Assert.That(note.Message, Does.Contain("Closed folder")); Assert.That(PsdCodec.IsInformational(note), Is.True);
        }

        /// <summary>壊れた区切り: 理由付きで拒否、原本はそのまま。</summary>
        [TestCase("folder-without-divider", "without a bounding section divider")]
        [TestCase("divider-without-folder", "without a folder record")]
        [TestCase("bad-signature", "section divider blend signature")]
        public void MalformedFolderNestingIsRejected(string damage, string reason)
        {
            var records = Folders();
            switch (damage)
            {
                case "folder-without-divider": records.RemoveAt(1); break;
                case "divider-without-folder": records.RemoveAt(6); break;
                case "bad-signature": records[5].Tags[0] = new KeyValuePair<string, byte[]>("lsct", PsdFixture.Bytes(s => { PsdFixture.U32(s, 1); PsdFixture.Key(s, "XXXX"); PsdFixture.Key(s, "norm"); })); break;
            }
            byte[] bytes = PsdFixture.Build(2, 1, records, FoldersComposite);
            var read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.Rejected), PsdFixture.Show(read));
            Assert.That(read.Diagnostics.Single().Message, Does.Contain(reason));
            Assert.That(read.CopyOriginalBytes(), Is.EqualTo(bytes));
        }

        /// <summary>表現していないフォルダーの形: PreserveOnly と理由。</summary>
        [TestCase("unknown-type", "SectionDivider", "type 7")]
        [TestCase("odd-layout", "SectionDivider", "layout of 8 bytes")]
        [TestCase("folder-pixels", "GroupPixels", "own pixels")]
        [TestCase("divider-mask", "DividerMask", "mask")]
        [TestCase("blend-conflict", "GroupBlend", "contradicts")]
        [TestCase("unknown-folder-mode", "BlendMode", "diss")]
        [TestCase("two-settings", "SectionDivider", "More than one")]
        public void UnrepresentedFolderShapesArePreserveOnly(string feature, string code, string mention)
        {
            var records = Folders();
            switch (feature)
            {
                case "unknown-type": records[5].Tags[0] = new KeyValuePair<string, byte[]>("lsct", PsdFixture.Section(7)); break;
                case "odd-layout": records[5].Tags[0] = new KeyValuePair<string, byte[]>("lsct", new byte[] { 0, 0, 0, 1, 0, 0, 0, 0 }); break;
                case "folder-pixels": records[5].Width = 1; records[5].Height = 1; records[5].Rgba = new byte[] { 1, 2, 3, 4 }; break;
                case "divider-mask": records[3].MaskData = PsdFixture.Bytes(s => { PsdFixture.U32(s, 0); PsdFixture.U32(s, 0); PsdFixture.U32(s, 1); PsdFixture.U32(s, 1); s.WriteByte(255); s.WriteByte(0); s.WriteByte(0); s.WriteByte(0); });
                    records[3].MaskValues = new byte[] { 0 }; records[3].MaskWidth = 1; records[3].MaskHeight = 1; break;
                case "blend-conflict": records[5].Blend = "mul "; records[5].Tags[0] = new KeyValuePair<string, byte[]>("lsct", PsdFixture.Section(1, "scrn")); break;
                case "unknown-folder-mode": records[5].Blend = "diss"; records[5].Tags[0] = new KeyValuePair<string, byte[]>("lsct", PsdFixture.Section(1, "diss")); break;
                case "two-settings": records[5].Tags.Add(new KeyValuePair<string, byte[]>("lsdk", PsdFixture.Section(1, "norm"))); break;
            }
            byte[] bytes = PsdFixture.Build(2, 1, records, FoldersComposite);
            var read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly), PsdFixture.Show(read));
            Assert.That(read.Diagnostics.Any(x => x.Code == code && x.Message.Contains(mention)), Is.True, PsdFixture.Show(read));
            Assert.That(read.CopyOriginalBytes(), Is.EqualTo(bytes));
            Assert.That(() => PsdCodec.WriteEdited(read, new PsdDocument()), Throws.InvalidOperationException);
        }

        [Test] public void MutatedGroupFilesNeverEscapeTheParser()
        {
            var d = new PaintDocument(8, 6, 8);
            Raster(d, "bg", Gradient);
            var a = Raster(d, "a", Soft); var b = Raster(d, "b", Spots); d.SetLayerClipping(b.Id, true);
            var inner = Group(d, "inner", LayerBlendMode.Multiply, a, b); HideRow(d, inner.Id, 2, 200);
            var outer = Group(d, "outer", LayerBlendMode.PassThrough, inner); d.SetLayerOpacity(outer.Id, 0.5);
            byte[][] originals = { PsdCodec.Write(PsdBridge.Export(d, PaintChannel.Color)), PsdFixture.Build(2, 1, Folders(), FoldersComposite) };
            var random = new Random(9127);
            for (int trial = 0; trial < 500; trial++)
            {
                byte[] bytes = (byte[])originals[trial % 2].Clone();
                for (int n = 0; n < 1 + trial % 4; n++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                PsdReadResult read = null;
                Assert.DoesNotThrow(() => read = PsdCodec.Read(bytes, new PsdLimits { MaxCanvasPixels = 4096, MaxDecodedBytes = 1 << 20, MaxGroupDepth = 8 }), "trial " + trial);
                if (read.Mode == PsdCompatibilityMode.EditableRaster)
                {
                    Assert.DoesNotThrow(() => PsdCodec.WriteEdited(read, read.Document), "trial " + trial);
                    if (read.Document.Width <= 4096 && read.Document.Height <= 4096 && read.Document.Layers.Count > 0)
                        try { PsdBridge.Import(read).ValidateStructure(); }
                        catch (InvalidOperationException) { } // 範囲外の画素などの明示的な拒否は可
                }
                Assert.That(read.CopyOriginalBytes(), Is.EqualTo(bytes));
            }
        }

        static int IndexOf(byte[] data, string pattern)
        {
            var p = pattern.Select(c => (byte)c).ToArray();
            for (int i = 0; i <= data.Length - p.Length; i++) { int k = 0; while (k < p.Length && data[i + k] == p[k]) k++; if (k == p.Length) return i; }
            return -1;
        }
    }
}
