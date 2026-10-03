using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Brushes;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>Photoshop の .abr の読み込み。テスト用のファイルは公開された並びどおりにここで組み立てる。</summary>
    public sealed class PhotoshopBrushTests
    {
        /// <summary>ビッグエンディアンの書き出し器（ABR と ActionDescriptor の組み立て用）。</summary>
        internal sealed class W
        {
            public readonly MemoryStream S = new MemoryStream();
            public W I16(int v) { S.WriteByte((byte)(v >> 8)); S.WriteByte((byte)v); return this; }
            public W I32(long v) { S.WriteByte((byte)(v >> 24)); S.WriteByte((byte)(v >> 16)); S.WriteByte((byte)(v >> 8)); S.WriteByte((byte)v); return this; }
            public W U8(int v) { S.WriteByte((byte)v); return this; }
            public W Bytes(byte[] b) { S.Write(b, 0, b.Length); return this; }
            public W Ascii(string a) { return Bytes(Encoding.ASCII.GetBytes(a)); }
            public W Double(double v) { var b = BitConverter.GetBytes(v); if (BitConverter.IsLittleEndian) Array.Reverse(b); return Bytes(b); }
            public W Unicode(string t) { I32(t.Length + 1); Bytes(Encoding.BigEndianUnicode.GetBytes(t + "\0")); return this; }
            public W Key(string k) { if (k.Length == 4) { I32(0); Ascii(k); } else { I32(k.Length); Ascii(k); } return this; }
            public byte[] ToArray() => S.ToArray();
        }
        /// <summary>ActionDescriptor の値。(key, type, writer) の並びで組む。</summary>
        internal static void Descriptor(W w, string classId, params (string key, Action<W> value)[] items)
        {
            w.Unicode("").Key(classId).I32(items.Length);
            foreach (var (key, value) in items) { w.Key(key); value(w); }
        }
        internal static Action<W> Unit(string unit, double v) => w => w.Ascii("UntF").Ascii(unit).Double(v);
        internal static Action<W> Bool(bool v) => w => w.Ascii("bool").U8(v ? 1 : 0);
        internal static Action<W> Long(int v) => w => w.Ascii("long").I32(v);
        internal static Action<W> Text(string t) => w => w.Ascii("TEXT").Unicode(t);
        internal static Action<W> Obj(string classId, params (string, Action<W>)[] items) => w => { w.Ascii("Objc"); Descriptor(w, classId, items); };
        internal static Action<W> Dynamics(int control, double jitter) => Obj("brVr", ("bVTy", Long(control)), ("fStp", Long(25)), ("jitter", Unit("#Prc", jitter)));

        /// <summary>上の行から並んだ 8 bit の筆先（3x2: 上 = 255,0,0 / 下 = 0,0,128）。</summary>
        internal static readonly byte[] TipRows = { 255, 0, 0, 0, 0, 128 };
        internal static void RawBitmap(W w) { w.U8(0).Bytes(TipRows); }
        static void RleBitmap(W w)
        {
            // 行ごとの PackBits: 上の行 = 「255 を 1 個、0 を 2 個」、下の行 = 「0 を 2 個、128 を 1 個」
            var rows = new[] { new byte[] { 0x00, 255, 0xFF, 0 }, new byte[] { 0xFF, 0, 0x00, 128 } };
            w.U8(1); foreach (var row in rows) w.I16(row.Length); foreach (var row in rows) w.Bytes(row);
        }
        static void AssertTip(BrushTip tip)
        {
            Assert.That(tip.Width, Is.EqualTo(3)); Assert.That(tip.Height, Is.EqualTo(2));
            Assert.That(tip[0, 1], Is.EqualTo(255), "the file's top row is the tip's top row"); Assert.That(tip[2, 0], Is.EqualTo(128));
        }

        [Test] public void Version1ComputedAndSampledBrushes()
        {
            var computed = new W().I32(0).I16(30).I16(40).I16(50).I16(-30).I16(80).ToArray(); // misc, spacing, diameter, roundness, angle, hardness
            var sampled = new W().I32(0).I16(25).U8(1).I16(0).I16(0).I16(2).I16(3).I32(0).I32(0).I32(2).I32(3).I16(8);
            RawBitmap(sampled);
            var file = new W().I16(1).I16(2).I16(1).I32(computed.Length).Bytes(computed).I16(2).I32(sampled.ToArray().Length).Bytes(sampled.ToArray()).ToArray();
            var brushes = PhotoshopBrushReader.Read(file, "Old");
            Assert.That(brushes.Count, Is.EqualTo(2));
            var c = brushes[0].Settings;
            Assert.That(c.Tip, Is.Null); Assert.That(c.Radius, Is.EqualTo(20)); Assert.That(c.Spacing, Is.EqualTo(.3)); Assert.That(c.Roundness, Is.EqualTo(.5));
            Assert.That(c.Angle, Is.EqualTo(-30)); Assert.That(c.Hardness, Is.EqualTo(.8));
            AssertTip(brushes[1].Settings.Tip); Assert.That(brushes[1].Settings.Spacing, Is.EqualTo(.25));
        }

        [Test] public void Version2SampledBrushWithNameAndPackBits()
        {
            var sampled = new W().I32(0).I16(10).Unicode("Grass 草").U8(1).I16(0).I16(0).I16(2).I16(3).I32(0).I32(0).I32(2).I32(3).I16(8);
            RleBitmap(sampled);
            var bytes = sampled.ToArray();
            var brushes = PhotoshopBrushReader.Read(new W().I16(2).I16(1).I16(2).I32(bytes.Length).Bytes(bytes).ToArray());
            Assert.That(brushes.Single().Name, Is.EqualTo("Grass 草")); AssertTip(brushes[0].Settings.Tip);
        }

        internal static byte[] Section(string key, byte[] data)
        {
            var w = new W().Ascii("8BIM").Ascii(key).I32(data.Length).Bytes(data);
            while (w.S.Length % 4 != 0) w.U8(0);
            return w.ToArray();
        }
        internal static byte[] Samp(int subversion, string id, bool rle)
        {
            var record = new W().U8(id.Length).Ascii(id).Bytes(new byte[subversion == 1 ? 10 : 264]).I32(0).I32(0).I32(2).I32(3).I16(8);
            if (rle) RleBitmap(record); else RawBitmap(record);
            var data = record.ToArray();
            var w = new W().I32(data.Length).Bytes(data); while (w.S.Length % 4 != 0) w.U8(0);
            return w.ToArray();
        }

        [Test] public void Version6TipsWithPresetDynamics()
        {
            const string id = "3f1c5c3e-0000-4000-8000-0123456789ab";
            var desc = new W().I32(16);
            Descriptor(desc, "null", ("Brsh", w =>
            {
                w.Ascii("VlLs").I32(1).Ascii("Objc");
                Descriptor(w, "brushPreset",
                    ("Nm  ", Text("Leaves")),
                    ("Brsh", Obj("sampledBrush", ("Dmtr", Unit("#Pxl", 60)), ("Angl", Unit("#Ang", 45)), ("Rndn", Unit("#Prc", 70)), ("Spcn", Unit("#Prc", 35)), ("sampledData", Text(id)))),
                    ("useTipDynamics", Bool(true)), ("szVr", Dynamics(2, 40)), ("angleDynamics", Dynamics(0, 100)), ("roundnessDynamics", Dynamics(0, 20)),
                    ("useScatter", Bool(true)), ("scatterDynamics", Dynamics(0, 150)), ("bothAxes", Bool(true)), ("Cnt ", Long(3)),
                    ("usePaintDynamics", Bool(true)), ("opVr", Dynamics(2, 10)), ("prVr", Dynamics(1, 30)),
                    ("useTexture", Bool(true)), ("Wtdg", Bool(true)));
            }));
            var file = new W().I16(6).I16(1).Bytes(Section("samp", Samp(1, id, false))).Bytes(Section("desc", desc.ToArray())).Bytes(Section("patt", new byte[8])).ToArray();
            var brush = PhotoshopBrushReader.Read(file).Single();
            var s = brush.Settings;
            Assert.That(brush.Name, Is.EqualTo("Leaves")); AssertTip(s.Tip);
            Assert.That(s.Radius, Is.EqualTo(30)); Assert.That(s.Angle, Is.EqualTo(45)); Assert.That(s.Roundness, Is.EqualTo(.7)); Assert.That(s.Spacing, Is.EqualTo(.35));
            Assert.That(s.SizeJitter, Is.EqualTo(.4)); Assert.That(s.PressureSize, Is.True, "size controlled by pen pressure");
            Assert.That(s.AngleJitter, Is.EqualTo(1)); Assert.That(s.RoundnessJitter, Is.EqualTo(.2));
            Assert.That(s.Scatter, Is.EqualTo(1.5)); Assert.That(s.Count, Is.EqualTo(3));
            Assert.That(s.OpacityJitter, Is.EqualTo(.1)); Assert.That(s.PressureOpacity, Is.True);
            Assert.That(s.FlowJitter, Is.EqualTo(.3)); Assert.That(s.PressureFlow, Is.False, "fade control is not pressure");
            Assert.That(s.FadeFlow, Is.EqualTo(25), "control 1 is fade over fStp steps");
            Assert.That(brush.Warnings, Has.Some.Contains("Texture")); Assert.That(brush.Warnings, Has.Some.Contains("Wet edges"));
            Assert.That(brush.Warnings, Has.None.Contains("Flow")); Assert.That(brush.Warnings, Has.Some.Contains("patterns"));
        }

        [Test] public void Version6Subversion2TipsWithoutPresetsStillImport()
        {
            var file = new W().I16(10).I16(2).Bytes(Section("samp", Samp(2, "a", true).Concat(Samp(2, "b", false)).ToArray())).ToArray();
            var brushes = PhotoshopBrushReader.Read(file, "Set");
            Assert.That(brushes.Count, Is.EqualTo(2));
            foreach (var b in brushes) AssertTip(b.Settings.Tip);
        }

        [Test] public void AnUnreadablePresetSectionFallsBackToTheTipsWithAWarning()
        {
            var badDesc = new W().I32(16).Unicode("").Key("null").I32(1).Key("Brsh").Ascii("ObAr").ToArray(); // 未対応の型
            var file = new W().I16(6).I16(1).Bytes(Section("samp", Samp(1, "x", false))).Bytes(Section("desc", badDesc)).ToArray();
            var brush = PhotoshopBrushReader.Read(file).Single();
            AssertTip(brush.Settings.Tip);
            Assert.That(brush.Warnings, Has.Some.Contains("Brush settings could not be read"));
        }

        [Test] public void MalformedFilesAreRefusedWithAReason()
        {
            Assert.That(() => PhotoshopBrushReader.Read(new W().I16(3).ToArray()), Throws.TypeOf<BrushImportException>().With.Message.Contains("version 3"));
            Assert.That(() => PhotoshopBrushReader.Read(new W().I16(1).I16(1).I16(2).I32(100).ToArray()), Throws.TypeOf<BrushImportException>().With.Message.Contains("truncated"));
            var huge = new W().I16(6).I16(1).Bytes(Section("samp", new W().I32(30).U8(1).Ascii("z").Bytes(new byte[10]).I32(0).I32(0).I32(5000).I32(5000).I16(8).ToArray())).ToArray();
            Assert.That(() => PhotoshopBrushReader.Read(huge), Throws.TypeOf<BrushImportException>());
            Assert.That(() => PhotoshopBrushReader.Read(new W().I16(6).I16(1).ToArray()), Throws.TypeOf<BrushImportException>().With.Message.Contains("no brushes"));
        }
    }
}
