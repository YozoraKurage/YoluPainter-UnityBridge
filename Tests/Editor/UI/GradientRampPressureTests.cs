using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class GradientRampPressureTests
    {
        [Test] public void ValueCurveMatchesThePressureEditorAndNeverOvershoots()
        {
            var points = new[] { new Vector2(0, .2f), new Vector2(.3f, .9f), new Vector2(.55f, .1f), new Vector2(1, .7f) };
            var ui = PressureCurve.FromPoints(points); var ramp = GradientRamp.Default.WithCurve(points.Select(p => new GradientCurvePoint(p.x, p.y)));
            for (int k = 0; k <= 1000; k++)
            { double x = k / 1000.0; Assert.That(ramp.CurveValue(x), Is.EqualTo(ui.Evaluate((float)x)).Within(1e-6)); Assert.That(ramp.CurveValue(x), Is.InRange(.1 - 1e-6, .9 + 1e-6)); }
        }
    }
}
