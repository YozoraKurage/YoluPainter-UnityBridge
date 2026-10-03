using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// テクスチャセットの設定のパネル（Substance Painter の Texture Set Settings。既定の配置ではレイヤーのタブ）: 今のテクスチャセットの
    /// Normal の出力（Height → Normal・強さ・端・ファイルの Y の向き）、メッシュマップ（ベイクの口と焼いたマップ）、モデルのポーズと BlendShape
    /// （スキンメッシュのとき。Substance に無いもので、全部のセットが同じモデルを見るので、モデルの設定としてここの最後に置く）。
    /// 欄の中身は前のプロパティの欄と同じ関数（DrawNormalPanel・DrawMeshMapPanel・DrawPosePanel）で、見出しの見た目もプロパティの欄と同じ。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        Vector2 textureSetSettingsScroll; float textureSetSettingsHeight = 200;

        void DrawTextureSetSettingsPanel(Rect r)
        {
            LoadSectionMemory();
            BeginSpots("textureSetSettings");
            PaintGui.Fill(r, PaintTheme.PanelBg);
            bool overflow = textureSetSettingsHeight > r.height + .5f;
            var view = new Rect(0, 0, r.width - (overflow ? 8 : 0), Mathf.Max(textureSetSettingsHeight, r.height));
            textureSetSettingsScroll.y = Mathf.Clamp(textureSetSettingsScroll.y, 0, Mathf.Max(0, textureSetSettingsHeight - r.height));
            PaintGui.BeginScroll(r, textureSetSettingsScroll);
            var rows = new UiRows(new Rect(0, 0, view.width, 1e6f), 0);
            TextureSetSettingsSections(rows);
            rows.Indent = 0;
            rows.Space(8);
            if (Event.current.type == EventType.Repaint && Mathf.Abs(rows.Used - textureSetSettingsHeight) > .5f) { textureSetSettingsHeight = rows.Used; Repaint(); }
            PaintGui.EndScroll();
            if (Event.current.type == EventType.ScrollWheel && r.Contains(Event.current.mousePosition))
            { textureSetSettingsScroll.y = Mathf.Clamp(textureSetSettingsScroll.y + Event.current.delta.y * 14, 0, Mathf.Max(0, textureSetSettingsHeight - r.height)); Event.current.Use(); Repaint(); }
            if (overflow) PaintGui.Rounded(new Rect(r.xMax - 6, r.y + r.height * textureSetSettingsScroll.y / textureSetSettingsHeight, 4, r.height * r.height / textureSetSettingsHeight), PaintTheme.ControlActive, 2);
        }

        /// <summary>テクスチャセットの設定の欄（上から Normal、メッシュマップ、ポーズと BlendShape）。メッシュマップとポーズは長いので初めは閉じる。</summary>
        void TextureSetSettingsSections(UiRows rows)
        {
            if (Section(rows, "normal", L.Tr("Normal"), "3d_rotation")) { DrawNormalPanel(rows); rows.Space(SectionGap); }
            if (Section(rows, "mesh-maps", MeshMapsTitle(), "grid_on", openByDefault: false)) { DrawMeshMapPanel(rows); rows.Space(SectionGap); }
            if (preview != null && preview.HasSkinnedMeshes)
            {
                if (Section(rows, "pose", L.Tr("Pose & BlendShapes") + " · " + L.Tr("whole model"), "accessibility", openByDefault: false)) { DrawPosePanel(rows); rows.Space(SectionGap); }
                ApplyPendingPose();
            }
        }

        /// <summary>見出しに添えるテクスチャセットの名前（セットが 2 つ以上のとき）。</summary>
        string TextureSetSettingsSubtitle() => textureSets.Count > 1 && currentSet != null ? currentSet.Name : null;

        /// <summary>テスト用: テクスチャセットの設定の欄を area に描く（ドックと同じ部品と幅）。使った高さ。</summary>
        internal float DrawTextureSetSettingsOnly(Rect area)
        {
            BeginSpots("textureSetSettings");
            PaintGui.Fill(area, PaintTheme.PanelBg);
            var rows = new UiRows(area, 0);
            TextureSetSettingsSections(rows);
            rows.Indent = 0;
            return rows.Used;
        }
    }
}
