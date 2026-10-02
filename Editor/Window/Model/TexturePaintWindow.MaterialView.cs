using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// 3D ビューのマテリアル表示: 中立（プレビュー用の中立のシェーダー）と、マテリアル（元のマテリアルの複製を元のシェーダーで。
    /// <see cref="IsolatedModelPreview.Shading"/>）を切り替える。マテリアル表示では、各テクスチャセットのスロットに、対応のあるチャンネルの
    /// 合成を渡す: 今のセットの今のチャンネルは合成器の表示（描いているあいだも動く）、ほかのチャンネルとほかのセットは CPU の合成
    /// （変わったタイルがあったときだけ、ストロークの外で作り直す。長辺 <see cref="SetPreviewMaxSize"/> まで）、Normal は照明と同じ Normal の出力。
    /// 描き込み・ピック・ブラシのカーソルは描き方によらない。元のマテリアル・テクスチャには触れない。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        [SerializeField] PreviewShading previewShading = PreviewShading.Neutral;
        /// <summary>マテリアルの欄で変えた値（プレビューの複製だけ。.ylp には入らない。ウィンドウのシリアライズでリロードはまたぐ）。</summary>
        [SerializeField] PreviewMaterialEdits materialEdits = new PreviewMaterialEdits();
        /// <summary>今のセットの Normal の出力（照明を切っていてもマテリアル表示の _BumpMap などに使う）。</summary>
        Texture materialNormal;
        readonly List<TileCoord> changedTiles = new List<TileCoord>();

        internal PreviewShading Shading
        {
            get => previewShading;
            set
            {
                if (previewShading == value) return;
                previewShading = value;
                if (preview != null) preview.Shading = value;
                if (value == PreviewShading.Neutral) DisposeMaterialChannelTextures();
                repaintPixels = true; lightingRevision = -1;
                message = value == PreviewShading.Material ? MaterialViewMessage() : L.Tr("3D view: neutral shading (the painted channel on a neutral surface).");
                Repaint();
            }
        }
        internal PreviewMaterialEdits MaterialEdits => materialEdits;

        /// <summary>マテリアル表示に切り替えたときの知らせ（今のセットのスロットの対応と、中立に戻した理由）。</summary>
        string MaterialViewMessage()
        {
            if (preview == null || !preview.HasModel) return L.Tr("3D view: material shading. Load a model to see its materials.");
            var b = preview.MaterialBinding(materialSlot);
            if (b == null) return L.Tr("3D view: material shading.");
            if (!b.CanShow) return L.Tr("3D view: material shading, but this slot is shown neutral: {0}", b.Unusable);
            return L.Tr("3D view: material shading ({0}). Only the preview's copy of the material is used; the material itself is not changed.", b.Summary);
        }

        /// <summary>マテリアル表示で、そのスロットの対応にチャンネルがあるか。</summary>
        bool MaterialMaps(int slot, PaintChannel c)
        {
            if (previewShading != PreviewShading.Material || preview == null || !preview.HasModel) return false;
            var b = preview.MaterialBinding(slot);
            return b != null && b.CanShow && b.For(c) != null;
        }

        /// <summary>マテリアル表示に、全部のテクスチャセットの塗った中身を渡す（照明の更新の後に呼ぶ。Normal の出力を使うので）。</summary>
        void ShowMaterialChannels()
        {
            if (preview == null) return;
            if (preview.Shading != previewShading) preview.Shading = previewShading;
            if (previewShading != PreviewShading.Material || !preview.HasModel) return;
            preview.MaterialEdits = materialEdits;
            SyncCurrentSet();
            var slots = new Dictionary<int, PreviewSlotChannels>();
            foreach (var set in textureSets)
            {
                if (set.MaterialSlot >= preview.MaterialSlotCount || slots.ContainsKey(set.MaterialSlot)) continue;
                var binding = preview.MaterialBinding(set.MaterialSlot);
                if (binding == null || !binding.CanShow) continue;
                bool current = set == currentSet; var d = current ? document : set.Document;
                var used = new HashSet<PaintChannel>(YlpContent.UsedChannels(d));
                var painted = new PreviewSlotChannels();
                foreach (var c in binding.Channels.Select(x => x.Channel).Distinct())
                {
                    if (!used.Contains(c)) continue;
                    if (c == PaintChannel.Normal) { painted.NormalOutput = current ? materialNormal : SetLighting(set); continue; }
                    painted.Composites[c] = current && c == channel ? compositor.Texture : ChannelTexture(set, d, c);
                }
                slots[set.MaterialSlot] = painted;
            }
            preview.SetMaterialChannels(slots);
        }

        /// <summary>セットのチャンネルの CPU の合成（長辺 <see cref="SetPreviewMaxSize"/> まで）。前に作ってから、そのチャンネルの合成に関わるタイルが
        /// 変わっていなければ作り直さない（文書の ChangeSerial と TryGetChangedTiles）。今のセットを描いているあいだは前のものを使う。</summary>
        internal Texture2D ChannelTexture(TextureSet set, PaintDocument d, PaintChannel c)
        {
            set.MaterialChannels.TryGetValue(c, out var cached);
            if (cached != null && cached.Texture != null && ReferenceEquals(cached.Document, d) && cached.Width == d.Width && cached.Height == d.Height)
            {
                if (cached.Revision == d.Revision || stroke != null && set == currentSet) return cached.Texture;
                changedTiles.Clear();
                if (d.TryGetChangedTiles(c, cached.Serial, changedTiles) && changedTiles.Count == 0) { cached.Revision = d.Revision; return cached.Texture; }
            }
            if (cached == null) set.MaterialChannels[c] = cached = new TextureSet.ChannelCache();
            cached.Texture = Upload(cached.Texture, d.Composite(c), d.Width, d.Height, "YoluPainter material view " + set.Name + " " + c);
            cached.Document = d; cached.Revision = d.Revision; cached.Serial = d.ChangeSerial; cached.Width = d.Width; cached.Height = d.Height;
            MaterialChannelBuilds++;
            return cached.Texture;
        }
        /// <summary>試験用: CPU の合成からチャンネルのテクスチャを作った回数。</summary>
        internal int MaterialChannelBuilds { get; private set; }

        void DisposeMaterialChannelTextures() { foreach (var set in textureSets) set.DisposeMaterialChannels(); materialNormal = null; }

        // ───────── 元のマテリアルの変化に追いつく ─────────

        int watchedMaterialState;
        /// <summary>Tick から: マテリアル表示のあいだ、元のマテリアルが外で変わった（インスペクターで値を変えた・Undo した）・シェーダーの
        /// コンパイルを待っているときに 3D ビューを描き直させる。</summary>
        void WatchSourceMaterials()
        {
            if (preview == null || previewShading != PreviewShading.Material || !preview.HasModel) return;
            int state = 17;
            for (int i = 0; i < preview.MaterialSlotCount; i++)
            {
                var m = preview.SourceMaterial(i);
                state = state * 31 + (m == null ? 0 : EditorUtility.GetDirtyCount(m) * 7 + (m.shader != null ? m.shader.GetInstanceID() : 0));
            }
            if (state != watchedMaterialState || preview.CompilingShaders) { watchedMaterialState = state; repaintPixels = true; Repaint(); }
        }

        // ───────── 見出しとメニュー ─────────

        /// <summary>3D ビューの見出しの右: 描き方の切り替え（中立・マテリアル）。マテリアル表示で中立に戻したスロットがあれば注意の印。</summary>
        float DrawShadingSwitch(Rect bar)
        {
            float x = surfaceRect.xMax - 6;
            x -= 26; if (PaintGui.IconButton(new Rect(x, bar.y + 2, 26, 22), "auto_awesome", L.Tr("Material shading: the source material's shader with the painted maps (only a preview copy; the material itself is not changed)"), previewShading == PreviewShading.Material, preview.HasModel && stroke == null, 16)) Shading = PreviewShading.Material;
            x -= 28; if (PaintGui.IconButton(new Rect(x, bar.y + 2, 26, 22), "contrast", L.Tr("Neutral shading: the painted channel on a neutral surface"), previewShading == PreviewShading.Neutral, stroke == null, 16)) Shading = PreviewShading.Neutral;
            x -= 30;
            var scene = new Rect(x, bar.y + 2, 26, 22);
            if (PaintGui.IconButton(scene, "light_mode", L.Tr("Scene: camera views, the light, the ambient and the background of the 3D view (the preview only)"), false, stroke == null, 16)) OpenScenePopup(scene);
            if (previewShading == PreviewShading.Material && preview.HasModel)
            {
                var notes = new List<string>();
                for (int i = 0; i < preview.MaterialSlotCount; i++) { var why = preview.MaterialReason(i); if (why != null) notes.Add(L.Tr("slot {0}", i) + ": " + why); }
                if (notes.Count > 0)
                {
                    x -= 22;
                    var warn = new Rect(x, bar.y, 20, bar.height);
                    PaintGui.Icon(warn, "warning", PaintTheme.Warning, 15);
                    PaintGui.Tooltip(warn, L.Tr("Shown neutral:") + "\n" + string.Join("\n", notes));
                }
            }
            return x;
        }

        void ShadingMenuItems(GenericMenu m)
        {
            Item(m, "Neutral Shading", () => Shading = PreviewShading.Neutral, true, previewShading == PreviewShading.Neutral);
            Item(m, "Material Shading", () => Shading = PreviewShading.Material, preview.HasModel, previewShading == PreviewShading.Material);
        }
    }
}
