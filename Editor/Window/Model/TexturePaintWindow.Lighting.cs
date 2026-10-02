using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>3D プレビューの照明に Normal の出力（手描きの Normal と Height → Normal）を使う。どのチャンネルを描いていても効くよう、
    /// Normal の合成と出力を別に持つ。作り直すのはストロークの外で文書が変わったときだけ（CPU の代わりの経路は 4K で数秒かかる）。
    /// ほかのテクスチャセットは CPU の Normal の出力を、それぞれのスロットの照明に使う（文書の版が変わったときだけ作り直す）。</summary>
    public sealed partial class TexturePaintWindow
    {
        bool previewNormals = true;
        TileGpuCompositor lightingCompositor; NormalOutputView lightingOutput; long lightingRevision = -1;
        internal bool PreviewNormals { get => previewNormals; set { previewNormals = value; lightingRevision = -1; repaintPixels = true; } }
        internal Texture PreviewNormalTexture { get; private set; }

        void DrawLightingToggle()
        {
            bool next = GUILayout.Toggle(previewNormals, new GUIContent("Lit normals", "Light the 3D preview with the Normal output (painted Normal and Height → Normal). Updated when a stroke ends."), EditorStyles.toolbarButton, GUILayout.Width(80));
            if (next != previewNormals) PreviewNormals = next;
        }

        void UpdatePreviewLighting()
        {
            Texture current = null;
            // マテリアル表示が Normal を使うなら、照明を切っていても出力を作る（_BumpMap などに入れる）
            bool wanted = previewNormals || MaterialMaps(materialSlot, PaintChannel.Normal);
            if (!wanted || !YlpContent.UsedChannels(document).Contains(PaintChannel.Normal)) DisposeLighting();
            else if (ShowsNormalOutput && normalOutput?.Texture != null) current = normalOutput.Texture; // 表示用の出力をそのまま使う
            else
            {
                if (stroke == null && document.Revision != lightingRevision)
                {
                    if (lightingCompositor == null) lightingCompositor = new TileGpuCompositor { ResidentBudgetBytes = 0 };
                    lightingCompositor.Update(document, PaintChannel.Normal);
                    if (lightingOutput == null) lightingOutput = new NormalOutputView();
                    lightingOutput.Update(document, lightingCompositor.Texture);
                    lightingRevision = document.Revision;
                }
                current = lightingOutput?.Texture;
            }
            materialNormal = current;
            PreviewNormalTexture = previewNormals ? current : null;
            if (preview == null) return;
            var normals = new System.Collections.Generic.Dictionary<int, Texture>();
            if (previewNormals && current != null) normals[materialSlot] = current;
            if (previewNormals && preview.HasModel)
                foreach (var set in textureSets)
                    if (set != currentSet && set.MaterialSlot < preview.MaterialSlotCount && !normals.ContainsKey(set.MaterialSlot))
                    { var n = SetLighting(set); if (n != null) normals[set.MaterialSlot] = n; }
            preview.SetNormalTextures(normals);
        }
        void DisposeLighting() { lightingOutput?.Dispose(); lightingOutput = null; lightingCompositor?.Dispose(); lightingCompositor = null; lightingRevision = -1; }
    }
}
