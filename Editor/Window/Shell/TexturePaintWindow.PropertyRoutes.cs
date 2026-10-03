using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>プロパティの欄に出すものの文脈（Substance Painter のプロパティの題「プロパティ - ペイント」「- 塗りつぶし」…に当たる）。
    /// 描くツールの文脈・描かないツールの文脈・層の種類の文脈・効果の段の文脈。<see cref="TexturePaintWindow.PropertyContextNow"/> が決める。</summary>
    internal enum PropertyContext
    {
        /// <summary>ブラシ・消しゴム（ペイントの層かマスクに描く）。</summary>
        Brush,
        /// <summary>ぼかし・指先・コピースタンプ。</summary>
        BrushEffect,
        /// <summary>バケツ（塗りつぶしのツール）。</summary>
        Bucket,
        Gradient,
        PolygonFill,
        /// <summary>矩形・楕円・投げ縄・自動選択。</summary>
        Selection,
        IdSelect,
        Move,
        Path,
        Eyedropper,
        /// <summary>塗りつぶしの層（中身。マスクを選んでいればマスクに描くツールの文脈）。</summary>
        FillLayer,
        Adjustment,
        Group,
        /// <summary>層の効果の段（フィルター・Generator）。レイヤーの重なりの子の行で選ぶ。</summary>
        Effect,
        /// <summary>層かマスクの Anchor（アンカーポイント）。レイヤーの重なりの子の行で選ぶ。</summary>
        Anchor,
    }

    /// <summary>
    /// プロパティの欄の振り分けの表（文脈 → 出す欄の並び）。欄の中身を描く関数（BrushTipSection・JitterSection・DrawFilterParameters …）は
    /// 変えず、どの文脈で・どのタブに・どの見出しの段（大見出し / 小見出し）で呼ぶかだけをここに書く。新しい欄を足すときはこの表に 1 行足す:
    /// <code>R(PaintContexts, TabStencil, "brush-stencil", StencilSection);                                   // 中身の関数が ToolSection で見出しを描く（タブの大見出し）
    /// R(Paint, TabBrush, "brush-wet", WetSection, parent: "brush", closedAtFirst: true, reset: ResetWet);  // 「ブラシ」の中の小見出し
    /// R(Layers, null, "anchor", DrawAnchor, title: AnchorTitle, icon: "target", when: () => …);  // 表の側で見出しを描く（題は訳した文字を返す関数）</code>
    /// 並びは表の順。タブは最初に出てきた順に並ぶ（タブが 1 つの文脈はタブの帯を出さない）。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>表の 1 行。</summary>
        internal sealed class PropertyRoute
        {
            public PropertyContext[] Contexts;
            /// <summary>タブ（null はタブ無し）。<see cref="PropertyTabTitle"/>・<see cref="PropertyTabIcon"/> に名前とアイコン。</summary>
            public string Tab;
            /// <summary>欄のキー（開閉の記憶。中身の関数が自分で見出しを描くなら、その関数が使うキー）。</summary>
            public string Key;
            /// <summary>小見出しなら親の大見出しのキー（親が閉じていれば描かない）。</summary>
            public string Parent;
            /// <summary>表の側で描く見出しの題（null なら中身の関数が自分で描く）。</summary>
            public Func<string> Title;
            public string Icon;
            /// <summary>見出しのアイコンが中身で変わるとき（層の種類など。あれば <see cref="Icon"/> より先）。</summary>
            public Func<string> IconOf;
            public Action<UiRows> Draw;
            /// <summary>出す条件（null はいつも）。</summary>
            public Func<bool> When;
            /// <summary>初めは閉じておく（細かい設定）。</summary>
            public bool ClosedAtFirst;
            /// <summary>見出しの右端の「既定に戻す」（null は出さない）。</summary>
            public Action Reset;
        }

        /// <summary>描くツールのタブ（Substance のブラシ｜アルファ｜ステンシル｜マテリアル）。マスクに描くあいだは「マテリアル」の代わりに「マスク」。</summary>
        internal const string TabBrush = "brush", TabAlpha = "alpha", TabStencil = "stencil", TabMaterial = "material", TabMask = "mask";

        static string PropertyTabTitle(string tab)
        {
            switch (tab)
            {
                case TabBrush: return L.Tr("Brush");
                case TabAlpha: return L.TrIn("properties tab", "Alpha");
                case TabStencil: return L.TrIn("properties tab", "Stencil");
                case TabMaterial: return L.TrIn("properties tab", "Material");
                case TabMask: return L.TrIn("properties tab", "Mask");
                default: return L.Tr(tab);
            }
        }
        static string PropertyTabIcon(string tab) => tab == TabBrush ? "paint_brush" : tab == TabAlpha ? "shapes" : tab == TabStencil ? "square" : tab == TabMaterial ? "layers" : tab == TabMask ? "vignette" : "tune";

        static readonly PropertyContext[] PaintContexts = { PropertyContext.Brush, PropertyContext.BrushEffect };
        static readonly PropertyContext[] LayerContexts = { PropertyContext.FillLayer, PropertyContext.Adjustment, PropertyContext.Group };

        List<PropertyRoute> propertyRoutes;
        /// <summary>振り分けの表（窓ごとに 1 度作る。行の関数はこの窓の状態を読む）。</summary>
        internal List<PropertyRoute> PropertyRoutes => propertyRoutes ?? (propertyRoutes = BuildPropertyRoutes());

        List<PropertyRoute> BuildPropertyRoutes()
        {
            var table = new List<PropertyRoute>();
            void R(PropertyContext[] contexts, string tab, string key, Action<UiRows> draw, string parent = null, Func<string> title = null, string icon = null,
                Func<bool> when = null, bool closedAtFirst = false, Action reset = null, Func<string> iconOf = null)
                => table.Add(new PropertyRoute { Contexts = contexts, Tab = tab, Key = key, Draw = draw, Parent = parent, Title = title, Icon = icon, IconOf = iconOf, When = when, ClosedAtFirst = closedAtFirst, Reset = reset });
            PropertyContext[] Only(params PropertyContext[] c) => c;

            // ── 描くツール（Substance の BRUSH / ALPHA / STENCIL / MATERIAL の並び。マスクに描くあいだはマテリアルの代わりにマスク） ──
            R(Only(PropertyContext.BrushEffect), TabBrush, "brush-effect", BrushEffectSection);                                  // ぼかし・指先・コピースタンプの設定
            R(PaintContexts, TabBrush, "brush", BrushTipSection, title: () => L.Tr("Brush"), icon: "paint_brush");                // サイズ・流量・不透明度・間隔・角度・筆圧のカーブ・プリセット
            R(PaintContexts, TabBrush, "brush-jitter", JitterSection, parent: "brush", closedAtFirst: true, reset: () => ResetBrushGroup("brush-jitter"));
            R(PaintContexts, TabBrush, "brush-texture", TextureSection, parent: "brush", closedAtFirst: true, reset: () => ResetBrushGroup("brush-texture"));
            R(PaintContexts, TabBrush, "brush-dual", DualBrushSection, parent: "brush", closedAtFirst: true, reset: () => ResetBrushGroup("brush-dual"));
            R(Only(PropertyContext.Brush), TabBrush, "brush-color", ColorDynamicsSection, parent: "brush", closedAtFirst: true, reset: () => ResetBrushGroup("brush-color"));
            R(PaintContexts, TabBrush, "brush-fade", FadeTiltSection, parent: "brush", closedAtFirst: true, reset: () => ResetBrushGroup("brush-fade"));
            R(PaintContexts, TabBrush, "brush-normal", DrawNormalBrushRows, title: () => L.Tr("Normal"), icon: "3d_rotation",
                when: () => channel == PaintChannel.Normal && !EditingMask);                                                 // 法線を傾きで選ぶブラシの値（Layers/TexturePaintWindow.Normal.cs）
            R(PaintContexts, TabAlpha, "brush-alpha", AlphaSection);                                                      // 先端の形と一覧（Tools/TexturePaintWindow.BrushAlpha.cs）
            R(PaintContexts, TabStencil, "brush-stencil", StencilSection);                                                  // ステンシル（Tools/TexturePaintWindow.Stencil.cs）
            R(PaintContexts, TabMaterial, "brush-material", MaterialSection, when: () => !EditingMask);                    // マテリアルで塗る（Tools/TexturePaintWindow.MaterialBrush.cs）
            R(PaintContexts, TabMask, "mask", rows => DrawMask(rows, SelectedLayerOrNull), title: MaskSectionTitle, icon: "vignette", when: () => EditingMask);

            // ── 塗りつぶし系のツール（そのツールの設定を先に、続けてマテリアル。Substance のポリゴン塗りつぶしの並び） ──
            R(Only(PropertyContext.Bucket, PropertyContext.PolygonFill), null, "surface-pick", SurfacePickSection);              // 3D でのクリックの範囲（Model/TexturePaintWindow.Surface3D.cs）
            R(Only(PropertyContext.Bucket, PropertyContext.PolygonFill), null, "brush-material", MaterialSection);
            R(Only(PropertyContext.Gradient), null, "brush-material", rows =>
            {
                MaterialSection(rows);
                if (SectionIsOpen("brush-material", true)) GradientMaterialSection(rows); // 二つのマテリアルのあいだ（Tools/TexturePaintWindow.CanvasTools.cs）
            });

            // ── 描かないツール（どの層を選んでいても、そのツールの設定） ──
            R(Only(PropertyContext.Selection), null, "surface-pick", SurfacePickSection);
            R(Only(PropertyContext.IdSelect), null, "id-map", IdMapSection);                                                  // Tools/TexturePaintWindow.IdSelect.cs
            R(Only(PropertyContext.Selection, PropertyContext.IdSelect), null, "selection-modify", SelectionModifySection);
            R(Only(PropertyContext.Move), null, "move", TransformSection);
            R(Only(PropertyContext.Path), null, "path", PathSection);
            R(Only(PropertyContext.Path), null, "brush-material", MaterialSection);                                          // パスもマテリアルで描く（パスの設定の後）

            // ── 層の種類（塗りつぶし・調整・グループ。中身を選んでいるとき） ──
            R(LayerContexts, null, "layer", rows => DrawLayerDetails(rows, SelectedLayerOrNull), title: () => SelectedLayerOrNull?.Name ?? "",
                iconOf: () => SelectedLayerOrNull is PaintLayer l ? LayerKindIcon(l) : null);
            R(Only(PropertyContext.FillLayer), null, "projection", rows => DrawFillProjection(rows, SelectedLayerOrNull), title: () => L.Tr("Projection"), icon: "texture",
                when: () => SelectedLayerOrNull is PaintLayer l && (l.FillImages.Count > 0 || !l.Projection.Equals(FillProjection.Default)));   // 画像のある（か投影を変えた）塗りつぶしだけ

            // ── 効果の段（レイヤーの重なりの子の行で選んだフィルター・Generator） ──
            R(Only(PropertyContext.Effect), null, "effect", rows => { var (e, target) = SelectedEffect(); DrawFilterParameters(rows, e, target); },
                title: () => SelectedEffect().effect is FilterEffect e ? FilterName(e.Settings) : "",
                iconOf: () => SelectedEffect().effect?.Settings.Type == FilterType.Generator ? "texture" : "auto_awesome");
            R(Only(PropertyContext.Anchor), null, "anchor", rows =>
            {
                var (anchor, placement) = SelectedAnchor();
                DrawAnchorRow(rows, SelectedLayerOrNull, placement); // 名前・読む段の数・外す（Layers/TexturePaintWindow.Anchors.cs）
                var readers = document.AnchorReaders(anchor.Id);
                NoteRow(rows, readers.Count == 0 ? L.Tr("No generator reads this anchor yet: add Generator ▸ Anchor on a layer above.")
                    : L.Tr("Read by:") + " " + string.Join(", ", readers.Select(r => r.layer.Name + (r.target == FilterTarget.Mask ? " (" + L.Tr("mask") + ")" : ""))), NoteKind.Info);
            }, title: () => SelectedAnchor().anchor?.Name ?? "", icon: "anchor");                                                       // Anchor の行で選んだもの
            return table;
        }

        /// <summary>文脈の表の行のうち、今出すもの（条件を通ったもの）を表の順に。</summary>
        internal List<PropertyRoute> RoutesFor(PropertyContext context)
            => PropertyRoutes.Where(r => Array.IndexOf(r.Contexts, context) >= 0 && (r.When == null || r.When())).ToList();

        /// <summary>key の欄が初めは閉じているか（表か、ツールの欄の初めの開閉）。</summary>
        bool SectionClosedAtFirst(string key) => PropertyRoutes.Any(r => r.Key == key && r.ClosedAtFirst) || ToolSectionsClosedAtFirst.Contains(key);

        /// <summary>
        /// 今の文脈（Substance Painter に合わせた決まり。上から順に当てる）:
        /// 1. レイヤーの重なりで効果の段（フィルター・Generator）か Anchor の行を選んでいる → その段・その Anchor。
        /// 2. 描かないツール（選択・ID の色で選択・移動・パス・スポイト）→ そのツール（どの層を選んでいても。Substance に無いツールなので Photoshop に合わせる）。
        /// 3. 塗りつぶし・調整・グループの層の中身を選んでいる（マスクに描いていない）→ その層（これらの層の中身には描けない）。
        /// 4. それ以外（ペイントの層か、どの層でもマスクに描く）→ 描くツール。
        /// </summary>
        internal PropertyContext PropertyContextNow()
        {
            if (SelectedEffect().effect != null) return PropertyContext.Effect;
            if (SelectedAnchor().anchor != null) return PropertyContext.Anchor;
            switch (tool)
            {
                case PaintTool.SelectRectangle: case PaintTool.SelectEllipse: case PaintTool.Lasso: case PaintTool.MagicWand: return PropertyContext.Selection;
                case PaintTool.IdSelect: return PropertyContext.IdSelect;
                case PaintTool.Move: return PropertyContext.Move;
                case PaintTool.Path: return PropertyContext.Path;
                case PaintTool.Eyedropper: return PropertyContext.Eyedropper;
            }
            var layer = SelectedLayerOrNull;
            if (layer != null && !EditingMask)
            {
                if (layer.IsGroup) return PropertyContext.Group;
                if (layer.Kind == LayerKind.Fill) return PropertyContext.FillLayer;
                if (layer.Kind == LayerKind.Adjustment) return PropertyContext.Adjustment;
            }
            switch (tool)
            {
                case PaintTool.Blur: case PaintTool.Smudge: case PaintTool.Clone: return PropertyContext.BrushEffect;
                case PaintTool.Fill: return PropertyContext.Bucket;
                case PaintTool.Gradient: return PropertyContext.Gradient;
                case PaintTool.PolygonFill: return PropertyContext.PolygonFill;
                default: return PropertyContext.Brush;
            }
        }

        /// <summary>プロパティの欄の題（ドックの見出しの「プロパティ ― …」）。</summary>
        internal string PropertyContextTitle()
        {
            var context = PropertyContextNow();
            string onMask = EditingMask ? " · " + L.Tr("Layer mask") : "";
            switch (context)
            {
                case PropertyContext.Effect:
                {
                    var (e, target) = SelectedEffect();
                    return (e.Settings.Type == FilterType.Generator ? L.Tr("Generator") : L.Tr("Filter")) + (target == FilterTarget.Mask ? " · " + L.Tr("Layer mask") : "");
                }
                case PropertyContext.Anchor: return L.Tr("Anchor") + (SelectedAnchor().placement == AnchorPlacement.Mask ? " · " + L.Tr("Layer mask") : "");
                case PropertyContext.FillLayer: return LayerKindName(SelectedLayerOrNull); // 塗りつぶしレイヤー・デカール
                case PropertyContext.Adjustment: return L.Tr("Adjustment Layer") + ": " + L.Tr(AdjustmentName(SelectedLayerOrNull.Adjustment.Type));
                case PropertyContext.Group: return L.Tr("Group");
                default: return L.Tr(CurrentToolSlot().Name) + (context == PropertyContext.Brush || context == PropertyContext.BrushEffect || context == PropertyContext.Bucket
                    || context == PropertyContext.Gradient || context == PropertyContext.PolygonFill ? onMask : "");
            }
        }

        PaintLayer SelectedLayerOrNull => document?.Layers.FirstOrDefault(l => l.Id == selectedLayer);

        /// <summary>選んでいる効果の段（選んだ層のスタックにあるものだけ。無ければ null）と、それが画素とマスクのどちらのスタックか。</summary>
        internal (FilterEffect effect, FilterTarget target) SelectedEffect()
        {
            if (selectedFilter == Guid.Empty) return (null, FilterTarget.Content);
            var layer = SelectedLayerOrNull;
            if (layer == null) return (null, FilterTarget.Content);
            var e = layer.Filters.FirstOrDefault(f => f.Id == selectedFilter);
            if (e != null) return (e, FilterTarget.Content);
            e = layer.Mask?.Filters.FirstOrDefault(f => f.Id == selectedFilter);
            return e != null ? (e, FilterTarget.Mask) : (null, FilterTarget.Content);
        }

        /// <summary>選んでいる Anchor の行（選んだ層かそのマスクの Anchor だけ。無ければ null）と、置き場。</summary>
        internal (AnchorPoint anchor, AnchorPlacement placement) SelectedAnchor()
        {
            var layer = selectedFilter == Guid.Empty ? null : SelectedLayerOrNull;
            if (layer?.Anchor != null && layer.Anchor.Id == selectedFilter) return (layer.Anchor, AnchorPlacement.Layer);
            if (layer?.Mask?.Anchor != null && layer.Mask.Anchor.Id == selectedFilter) return (layer.Mask.Anchor, AnchorPlacement.Mask);
            return (null, AnchorPlacement.Layer);
        }

        string MaskSectionTitle() => L.Tr("Layer mask") + (SelectedLayerOrNull is PaintLayer l ? ": " + l.Name : "");

        /// <summary>ブラシの小見出しのまとまりを既定の値に戻す（見出しの右端のボタン）。プリセットの値ではなく、新しいブラシの値。</summary>
        internal void ResetBrushGroup(string key)
        {
            var d = new BrushState();
            switch (key)
            {
                case "brush-jitter":
                    brush.sizeJitter = d.sizeJitter; brush.angleJitter = d.angleJitter; brush.roundnessJitter = d.roundnessJitter; brush.opacityJitter = d.opacityJitter;
                    brush.flowJitter = d.flowJitter; brush.scatter = d.scatter; brush.count = d.count; break;
                case "brush-texture": brush.textureId = d.textureId; brush.textureDepth = d.textureDepth; brush.textureScale = d.textureScale; break;
                case "brush-dual":
                    brush.dualEnabled = d.dualEnabled; brush.dualTipId = d.dualTipId; brush.dualRadius = d.dualRadius; brush.dualHardness = d.dualHardness; brush.dualSpacing = d.dualSpacing;
                    brush.dualAngle = d.dualAngle; brush.dualRoundness = d.dualRoundness; brush.dualScatter = d.dualScatter; brush.dualCount = d.dualCount; brush.dualMode = d.dualMode; break;
                case "brush-color":
                    brush.fgBgJitter = d.fgBgJitter; brush.hueJitter = d.hueJitter; brush.saturationJitter = d.saturationJitter; brush.brightnessJitter = d.brightnessJitter;
                    brush.purity = d.purity; brush.colorPerTip = d.colorPerTip; break;
                case "brush-fade":
                    brush.fadeSize = d.fadeSize; brush.fadeOpacity = d.fadeOpacity; brush.fadeFlow = d.fadeFlow;
                    brush.tiltSize = d.tiltSize; brush.tiltOpacity = d.tiltOpacity; brush.tiltFlow = d.tiltFlow; brush.tiltAngle = d.tiltAngle; break;
                default: throw new ArgumentException("No defaults for " + key, nameof(key));
            }
            message = L.Tr("Back to the default values.");
            Repaint();
        }
    }
}
