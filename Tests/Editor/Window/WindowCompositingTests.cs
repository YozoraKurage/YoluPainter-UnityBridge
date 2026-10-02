using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウが個人の設定「Display compositing」に従って合成器を作り、設定が変わったら作り直すこと（ストロークの
    /// 最中はストロークが終わってから）、GPU を選んでも使えなければ CPU で合成してそう示すこと。</summary>
    public sealed partial class WindowTests
    {
        static byte[] ReadComposite(TexturePaintWindow w)
        { Repaint(w); return w.Compositor.Texture is RenderTexture ? GpuTests.Read(w.Compositor.Texture) : GpuTests.ReadCpu(w.Compositor.Texture); }

        static void ChooseCompositing(CompositorBackend choice)
        { var personal = PainterSettings.PersonalSettings; personal.displayCompositing = choice; PainterSettings.Save(null, personal); }

        [Test] public void ChangingDisplayCompositingRebuildsTheCompositorOnlyAfterTheStroke()
        {
            var first = window.Compositor;
            Assert.That(first.Preference, Is.EqualTo(CompositorBackend.Automatic), "a new window follows the settings (automatic by default)");
            BeginLine(300, 300);
            ChooseCompositing(CompositorBackend.Cpu);
            Assert.That(window.Compositor, Is.SameAs(first), "not while a stroke is in progress");
            Assert.That(window.CompositorRebuildPending, Is.True);
            Invoke(window, "Tick");
            Assert.That(window.Compositor, Is.SameAs(first), "still stroking");
            Mouse(window, EventType.MouseDrag, At(window, 420, 330));
            Mouse(window, EventType.MouseUp, At(window, 420, 330));
            Assert.That(window.IsStroking, Is.False);
            Invoke(window, "Tick"); // 次の Editor の update で作り直す
            Assert.That(window.Compositor, Is.Not.SameAs(first)); Assert.That(window.CompositorRebuildPending, Is.False);
            Assert.That(window.Compositor.Preference, Is.EqualTo(CompositorBackend.Cpu));
            CpuCompositingTests.AssertSameBytes(window.Document.Composite(PaintChannel.Color), ReadComposite(window), "the new compositor shows the stroke");
            Assert.That(window.Compositor.Path, Is.Not.EqualTo(TileGpuCompositor.CompositePath.Gpu), window.Compositor.Backend);
            Assert.That(window.Compositor.FellBackToCpu, Is.False);
            Assert.That(window.Compositor.ResidentBudgetBytes, Is.EqualTo(PainterSettings.GpuCacheBytes), "the GPU cache budget is kept");

            // ストロークの外なら、すぐに作り直す。同じ選択の保存では作り直さない
            var cpu = window.Compositor;
            PainterSettings.UpdatePersonal(p => p.recoveryIntervalSeconds = 30);
            Assert.That(window.Compositor, Is.SameAs(cpu), "an unrelated change keeps the compositor");
            ChooseCompositing(CompositorBackend.Automatic);
            Assert.That(window.Compositor, Is.Not.SameAs(cpu)); Assert.That(window.Compositor.Preference, Is.EqualTo(CompositorBackend.Automatic));
            BeginLine(500, 520); Mouse(window, EventType.MouseUp, At(window, 560, 520));
            if (window.Compositor.Path == TileGpuCompositor.CompositePath.Gpu) GpuTests.AssertMatches(window.Document.Composite(PaintChannel.Color), ReadComposite(window), "back to automatic (GPU)");
            else CpuCompositingTests.AssertSameBytes(window.Document.Composite(PaintChannel.Color), ReadComposite(window), "back to automatic (CPU)");
        }

        [Test] public void ChoosingTheGpuWhereItCannotBeUsedCompositesOnTheCpuAndSaysSo()
        {
            TileGpuCompositor.SimulatedGpuUnavailable = "simulated for the test";
            try
            {
                ChooseCompositing(CompositorBackend.Gpu);
                BeginLine(200, 640); Mouse(window, EventType.MouseUp, At(window, 260, 640));
                Repaint(window);
                Assert.That(window.Compositor.Preference, Is.EqualTo(CompositorBackend.Gpu));
                Assert.That(window.Compositor.FellBackToCpu, Is.True, "the status bar says \"CPU compositing (GPU unavailable)\"");
                Assert.That(window.Compositor.Backend, Does.StartWith("CPU composite fallback: simulated for the test"), "the status bar's tooltip gives the reason");
                CpuCompositingTests.AssertSameBytes(window.Document.Composite(PaintChannel.Color), ReadComposite(window), "CPU after the fallback");
                L.OverrideLanguage(PainterLanguage.Japanese);
                try { Assert.That(L.Tr("CPU compositing (GPU unavailable)"), Is.EqualTo("CPU で合成（GPU を使えない）")); }
                finally { L.OverrideLanguage(PainterLanguage.English); }
            }
            finally { TileGpuCompositor.SimulatedGpuUnavailable = null; }
        }
    }
}
