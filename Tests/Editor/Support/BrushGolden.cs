using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>ダイナミクスを足す前のブラシで描いた結果を固定するための描き方の一覧。同じコードをコミット済みの Core（ダイナミクス
    /// 導入前）と今の Core の両方で（Unity の外の mono で）走らせて SHA-256 が一致することを確かめ、その値を
    /// BrushDynamicsTests.DefaultSettingsKeepEveryStrokeByteIdentical に書いた。ここは古い API だけを使う。</summary>
    internal static class BrushGolden
    {
        static BrushSample[] Line(double x0, double y0, double x1, double y1, int n, double p0 = 1, double p1 = 1)
        {
            var s = new BrushSample[n + 1];
            for (int i = 0; i <= n; i++) { double t = i / (double)n; s[i] = new BrushSample(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t + 6 * Math.Sin(t * 6), p0 + (p1 - p0) * t, i * .01); }
            return s;
        }
        static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static void Draw(PaintDocument d, Guid layer, PaintChannel channel, BrushSettings s, BrushSample[] samples)
        { using (var stroke = d.BeginStroke(layer, channel, s)) { foreach (var p in samples) stroke.Add(p); stroke.Commit(); } }

        public static IEnumerable<KeyValuePair<string, string>> Run()
        {
            var ink = new Rgba32(200, 60, 30, 255);
            var cases = new List<KeyValuePair<string, BrushSettings>>
            {
                new KeyValuePair<string, BrushSettings>("round", new BrushSettings { Radius = 6, Hardness = 1, Spacing = .2, Opacity = .8, Flow = .5, Color = ink }),
                new KeyValuePair<string, BrushSettings>("soft-angled", new BrushSettings { Radius = 9, Hardness = .3, Spacing = .1, Color = ink, Angle = 30, Roundness = .5, FollowDirection = true, PressureFlow = true }),
                new KeyValuePair<string, BrushSettings>("tip-jitter", new BrushSettings { Radius = 8, Spacing = .25, Color = ink, Tip = BuiltInBrushes.Tip("charcoal"), SizeJitter = .5, AngleJitter = .3, Scatter = .5, Count = 3, OpacityJitter = .2, FlowJitter = .3, RoundnessJitter = .4, Seed = 7 }),
                new KeyValuePair<string, BrushSettings>("tips-sequential", new BrushSettings { Radius = 7, Spacing = .3, Color = ink, Tips = new[] { BuiltInBrushes.Tip("dots"), BuiltInBrushes.Tip("noisy-disc") }, TipSelection = TipSelection.Sequential }),
                new KeyValuePair<string, BrushSettings>("texture", new BrushSettings { Radius = 10, Spacing = .15, Color = ink, Texture = BuiltInBrushes.Tip("grain"), TextureDepth = .7, TextureScale = 2 }),
                new KeyValuePair<string, BrushSettings>("assist", new BrushSettings { Radius = 5, Hardness = .6, Spacing = .1, Color = ink, TaperIn = 10, TaperOut = 15, Stabilizer = 4 }),
            };
            foreach (var c in cases)
            {
                var d = new PaintDocument(96, 64, 32); var l = d.AddLayer("L");
                Draw(d, l.Id, PaintChannel.Color, c.Value, Line(8, 30, 88, 34, 23, .3, 1));
                yield return new KeyValuePair<string, string>(c.Key, Hash(d.Composite(PaintChannel.Color)));
            }
            {
                // 選択範囲の中で消す、マスク、Emission
                var d = new PaintDocument(96, 64, 32); var l = d.AddLayer("L");
                Draw(d, l.Id, PaintChannel.Color, new BrushSettings { Radius = 12, Hardness = .5, Spacing = .1, Color = ink }, Line(8, 30, 88, 30, 10));
                d.SetSelection(SelectionMask.Ellipse(d, 48, 32, 20, 14));
                Draw(d, l.Id, PaintChannel.Color, new BrushSettings { Radius = 8, Spacing = .1, Erase = true, Opacity = .7 }, Line(20, 20, 80, 44, 12));
                d.ClearSelection();
                d.AddLayerMask(l.Id);
                using (var stroke = d.BeginMaskStroke(l.Id, new BrushSettings { Radius = 6, Spacing = .1, Color = ink })) { foreach (var p in Line(10, 50, 90, 10, 9)) stroke.Add(p); stroke.Commit(); }
                d.SetChannelEnabled(l.Id, PaintChannel.Emission, true);
                Draw(d, l.Id, PaintChannel.Emission, new BrushSettings { Radius = 7, Spacing = .2, Color = new Rgba32(10, 220, 90, 128) }, Line(5, 5, 90, 60, 7));
                yield return new KeyValuePair<string, string>("erase-mask-emission", Hash(d.Composite(PaintChannel.Color)) + Hash(d.Composite(PaintChannel.Emission)).Substring(0, 16));
            }
        }
    }
}
