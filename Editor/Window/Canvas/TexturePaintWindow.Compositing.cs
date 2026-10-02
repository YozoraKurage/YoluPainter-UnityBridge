namespace Yozolab.YoluPainter.Editor
{
    /// <summary>表示の合成器（2D の表示と 3D のプレビュー）を個人の設定「Display compositing」から作る。設定が変わったら作り直す。
    /// ストロークの最中は、そのストロークが終わってから Tick が作り直す（描いている途中で表示の経路を替えない）。照明用の Normal の
    /// 合成器と、Normal の出力の Height の合成器も同じ設定に従う（作り直すときに捨て、次の表示の更新で作られる）。</summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>今の合成器を作ったときの「表示の合成」。</summary>
        CompositorBackend compositorBackend;
        bool compositorRebuildPending;
        /// <summary>設定が変わり、ストロークが終わるのを待って合成器を作り直すところか（テスト用）。</summary>
        internal bool CompositorRebuildPending => compositorRebuildPending;

        TileGpuCompositor CreateCompositor()
        {
            compositorBackend = PainterSettings.DisplayCompositing;
            return new TileGpuCompositor(compositorBackend) { ResidentBudgetBytes = PainterSettings.GpuCacheBytes };
        }
        /// <summary>ほかの表示用の合成器（照明用の Normal など）。今の合成器と同じ「表示の合成」で作る。</summary>
        internal TileGpuCompositor NewDisplayCompositor(long residentBudgetBytes) => new TileGpuCompositor(compositorBackend) { ResidentBudgetBytes = residentBudgetBytes };
        internal NormalOutputView NewNormalOutputView() => new NormalOutputView(heightBackend: compositorBackend);

        /// <summary>設定が変わったとき: 「表示の合成」が今の合成器と違えば作り直す。ストロークの最中なら印だけ付けて待つ。</summary>
        void ApplyCompositorSettings()
        {
            if (compositor == null) return;
            compositorRebuildPending = PainterSettings.DisplayCompositing != compositorBackend;
            RebuildCompositorIfPending();
        }
        /// <summary>待っている作り直しを、ストロークの外ならする（設定の変更と Tick から）。</summary>
        void RebuildCompositorIfPending()
        {
            if (!compositorRebuildPending || stroke != null || compositor == null) return;
            compositorRebuildPending = false;
            DisposeNormalOutput(); DisposeLighting();
            compositor.Dispose();
            compositor = CreateCompositor();
            repaintPixels = true; Repaint();
        }
    }
}
