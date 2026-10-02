using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// プロパティの欄の枠組み: 今のツール（ToolSections）、選んだレイヤー（LayerSections）、チャンネルとテクスチャセット
    /// （ChannelSections）のセクションを縦に積み、はみ出したらスクロールする。セクションは <see cref="Section"/> の見出しで折りたためる。
    /// まだ自前の部品に移していない中身は <see cref="LegacySection"/> で包む（Unity の標準の部品のまま、測った高さの区画に描く）。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        Vector2 propertiesScroll; float propertiesContentHeight = 200;
        readonly Dictionary<string, float> legacyHeights = new Dictionary<string, float>();
        readonly Dictionary<string, bool> sectionOpen = new Dictionary<string, bool>();

        void DrawPropertiesPanel(Rect r)
        {
            PaintGui.Fill(r, PaintTheme.PanelBg);
            bool overflow = propertiesContentHeight > r.height + .5f;
            var view = new Rect(0, 0, r.width - (overflow ? 8 : 0), Mathf.Max(propertiesContentHeight, r.height));
            propertiesScroll.y = Mathf.Clamp(propertiesScroll.y, 0, Mathf.Max(0, propertiesContentHeight - r.height));
            PaintGui.BeginScroll(r, propertiesScroll);
            var rows = new UiRows(new Rect(0, 0, view.width, 1e6f), 0);
            ToolSections(rows);
            LayerSections(rows);
            ChannelSections(rows);
            rows.Space(8);
            if (Event.current.type == EventType.Repaint && Mathf.Abs(rows.Used - propertiesContentHeight) > .5f) { propertiesContentHeight = rows.Used; Repaint(); }
            PaintGui.EndScroll();
            if (Event.current.type == EventType.ScrollWheel && r.Contains(Event.current.mousePosition))
            { propertiesScroll.y = Mathf.Clamp(propertiesScroll.y + Event.current.delta.y * 14, 0, Mathf.Max(0, propertiesContentHeight - r.height)); Event.current.Use(); Repaint(); }
            if (overflow) PaintGui.Rounded(new Rect(r.xMax - 6, r.y + r.height * propertiesScroll.y / propertiesContentHeight, 4, r.height * r.height / propertiesContentHeight), PaintTheme.ControlActive, 2);
        }

        /// <summary>折りたためるセクションの見出し（開いていれば true）。key ごとに開閉を覚える（既定は開）。</summary>
        bool Section(UiRows rows, string key, string title, string icon = null)
        {
            bool open = !sectionOpen.TryGetValue(key, out var stored) || stored;
            open = PaintGui.SectionHeader(rows.FullRow(PanelHeaderHeight, 4), title, open, icon);
            sectionOpen[key] = open;
            return open;
        }

        /// <summary>まだ Unity の標準の部品（GUILayout / EditorGUILayout）で描くセクション。前に描いたときに測った高さの区画に描く。
        /// オフスクリーンの描画（バッチモード）には標準のスタイルが無いので、そこでは描かない。</summary>
        void LegacySection(UiRows rows, string key, string title, Action draw)
        {
            if (!Section(rows, key, title)) return;
            if (!EditorStylesReady) { PaintGui.Text(rows.Row(20), "(" + L.Tr("not converted yet; drawn with Unity's controls") + ")", PaintTheme.LabelSmall); return; }
            float height = legacyHeights.TryGetValue(key, out var h) ? h : 120;
            var area = rows.Row(height, 8);
            GUILayout.BeginArea(area);
            try
            {
                draw();
                GUILayout.Space(1);
                if (Event.current.type == EventType.Repaint)
                {
                    float measured = Mathf.Max(20, GUILayoutUtility.GetLastRect().yMax + 2);
                    if (Mathf.Abs(measured - height) > .5f) { legacyHeights[key] = measured; Repaint(); }
                }
            }
            finally { GUILayout.EndArea(); }
        }

        static bool EditorStylesReady { get { try { return EditorStyles.boldLabel != null; } catch (NullReferenceException) { return false; } } }
    }
}
