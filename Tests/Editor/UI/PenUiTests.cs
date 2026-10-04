using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class PenUiTests
    {
        [Test] public void ScrollGeometryReservesAFullHitAreaAndKeepsATinyThumbGrabbable()
        {
            var viewport = new Rect(10, 20, 200, 100);
            Assert.That(PaintGui.ScrollTrack(viewport).width, Is.EqualTo(18));
            Assert.That(PaintGui.ScrollContentWidth(viewport, 100000), Is.EqualTo(182));
            var top = PaintGui.ScrollThumb(viewport, 100000, -100);
            var bottom = PaintGui.ScrollThumb(viewport, 100000, 100000);
            Assert.That(top.height, Is.EqualTo(PaintGui.MinimumScrollThumb));
            Assert.That(top.y, Is.EqualTo(viewport.y)); Assert.That(bottom.yMax, Is.EqualTo(viewport.yMax));
            Assert.That(PaintGui.ScrollThumb(new Rect(0, 0, 40, 12), 1000, 1000).height, Is.EqualTo(12));
            Assert.That(PaintGui.ScrollContentWidth(viewport, 50), Is.EqualTo(viewport.width));
        }
        [TestCase(0)] [TestCase(64)] [TestCase(128)] [TestCase(255)]
        public void LegacyAlphaMovesOnceAndRoundTripsWithItsNotice(int alpha)
        {
            var b = new TexturePaintWindow.BrushState { color = new Color(.2f, .6f, 1, alpha / 255f), opacity = .8f };
            Assert.That(TexturePaintWindow.NormalizeBrushAlpha(b), Is.EqualTo(alpha != 255));
            Assert.That(b.color.a, Is.EqualTo(1)); Assert.That(b.opacity, Is.EqualTo(.8f * alpha / 255f).Within(1e-6));
            float opacity = b.opacity;
            var restored = TexturePaintWindow.ReadBrushState(JsonUtility.ToJson(b));
            Assert.That(restored.opacity, Is.EqualTo(opacity)); Assert.That(restored.colorAlphaMigrated, Is.EqualTo(alpha != 255));
            Assert.That(TexturePaintWindow.NormalizeBrushAlpha(restored), Is.False);
        }
        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(-.1f)] [TestCase(1.1f)]
        public void InvalidAlphaIsRefusedBeforeChangingOpacity(float alpha)
        {
            var b = new TexturePaintWindow.BrushState { color = new Color(0, 0, 0, alpha), opacity = .7f };
            Assert.That(() => TexturePaintWindow.NormalizeBrushAlpha(b), Throws.TypeOf<InvalidDataException>());
            Assert.That(b.opacity, Is.EqualTo(.7f)); Assert.That(b.colorAlphaMigrated, Is.False);
        }
        [TestCase(64)] [TestCase(128)] [TestCase(255)]
        public void MigratedOpacityKeepsThePaintedRgbaWithinOneByte(int alpha)
        {
            var b = new TexturePaintWindow.BrushState { color = new Color(.2f, .6f, 1, alpha / 255f), opacity = .8f };
            var old = new BrushSettings { Radius = 5, Hardness = .7, Flow = .35, Opacity = b.opacity, Color = new Rgba32(51, 153, 255, (byte)alpha), PressureSize = false, PressureOpacity = false };
            TexturePaintWindow.NormalizeBrushAlpha(b);
            var next = old.Clone(); next.Opacity = b.opacity; next.Color = new Rgba32(51, 153, 255, 255);
            byte[] Draw(BrushSettings settings)
            {
                var d = new PaintDocument(32, 32, 16); var layer = d.AddLayer("Paint");
                using (var stroke = d.BeginStroke(layer.Id, PaintChannel.Color, settings))
                { stroke.Add(new BrushSample(8, 16, 1, 0)); stroke.Add(new BrushSample(24, 16, 1, .1)); stroke.Commit(); }
                return d.Composite(PaintChannel.Color);
            }
            byte[] before = Draw(old), after = Draw(next);
            for (int i = 0; i < before.Length; i++) Assert.That(Math.Abs(before[i] - after[i]), Is.LessThanOrEqualTo(1), "RGBA byte " + i);
        }
    }
}
