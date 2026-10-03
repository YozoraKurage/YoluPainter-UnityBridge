using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// マテリアルで塗る（Substance Painter のように、1 回のストロークで複数のチャンネル）: ブラシの「塗るチャンネル」の組と、チャンネルごとの値
    /// （Color は描画色、Emission は色、Roughness・Metallic・Height は値、Normal は傾き）。オンなら、ストロークは組の全部を同じダブ・筆圧・
    /// 手ぶれ補正・入り抜き・シンメトリーで塗る（Core の <see cref="PaintDocument.BeginMaterialStroke"/>。1 回の Undo、層で無効のチャンネルは
    /// 有効にする）。オフ（既定）なら今のチャンネル 1 つを描画色で塗る（以前と同じ値）。マスクの編集中はどちらでもマスクだけを塗る。
    /// 設定は brush.json とブラシのプリセットの JSON に入る（schema 3 の中で足した項目。古い読み手は読み飛ばし、今のチャンネル 1 つで塗る）。
    /// プリセットを選んでも描画色と同じく残す（筆先ではなく塗る値なので）。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        internal sealed partial class BrushState
        {
            /// <summary>マテリアルで塗る（組の全部のチャンネルを 1 回のストロークで）。</summary>
            public bool material;
            /// <summary>塗るチャンネルの組（1 &lt;&lt; (int)PaintChannel の和）。0 はまだ選んでいない（オンにしたとき今のチャンネルになる）。</summary>
            public int materialChannels;
            public Color materialEmission = Color.black;
            public float materialRoughness = .5f, materialMetallic, materialHeight = .5f;
            /// <summary>Normal の値: 傾き（−1〜1。Z は長さが 1 になるように決める）。0, 0 は平ら。</summary>
            public float materialNormalX, materialNormalY;
        }

        static readonly int AllChannelBits = AllBits();
        static int AllBits() { int b = 0; foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel))) b |= Bit(c); return b; }
        static int Bit(PaintChannel c) => 1 << (int)c;

        internal bool MaterialMode => brush.material;
        internal bool MaterialIncludes(PaintChannel c) => (brush.materialChannels & Bit(c)) != 0;
        /// <summary>マテリアルで塗るのを切り替える。初めてオンにしたとき（組が空）は、今のチャンネル 1 つの組で始める。</summary>
        internal void SetMaterialMode(bool on)
        {
            if (stroke != null) return;
            brush.material = on;
            if (on && (brush.materialChannels & AllChannelBits) == 0) brush.materialChannels = Bit(channel);
            Repaint();
        }
        /// <summary>組にチャンネルを足す・外す。最後の 1 つは外さない（何も塗らないストロークにしない）。</summary>
        internal void SetMaterialChannel(PaintChannel c, bool on)
        {
            if (stroke != null) return;
            int next = on ? brush.materialChannels | Bit(c) : brush.materialChannels & ~Bit(c);
            if ((next & AllChannelBits) == 0) { message = L.Tr("A material paints at least one channel."); return; }
            brush.materialChannels = next; Repaint();
        }

        /// <summary>次のストロークが塗るチャンネルとその値。マテリアルがオフなら今のチャンネルを描画色で（以前と同じ）。</summary>
        internal IReadOnlyList<ChannelPaint> StrokeChannels()
        {
            var color = GetBrush().Color;
            if (!brush.material) return new[] { new ChannelPaint(channel, color) };
            var list = new List<ChannelPaint>();
            foreach (var c in Channels) if (MaterialIncludes(c)) list.Add(new ChannelPaint(c, MaterialValue(c)));
            if (list.Count == 0) list.Add(new ChannelPaint(channel, color));
            return list;
        }

        /// <summary>マテリアルのチャンネルの値（straight RGBA8、1 チャンネルで描画色をその値にして塗ったときと同じバイト）。アルファは描画色のアルファ。</summary>
        internal Rgba32 MaterialValue(PaintChannel c)
        {
            float a = brush.color.a;
            switch (c)
            {
                case PaintChannel.Color: return ToBrushBytes(brush.color);
                case PaintChannel.Emission: return ToBrushBytes(new Color(brush.materialEmission.r, brush.materialEmission.g, brush.materialEmission.b, a));
                case PaintChannel.Roughness: return ToBrushBytes(Grey(brush.materialRoughness, a));
                case PaintChannel.Metallic: return ToBrushBytes(Grey(brush.materialMetallic, a));
                case PaintChannel.Height: return ToBrushBytes(Grey(brush.materialHeight, a));
                default: return ToBrushBytes(NormalColor(brush.materialNormalX, brush.materialNormalY, a));
            }
        }
        static Color Grey(float v, float a) => new Color(v, v, v, a);
        /// <summary>GetBrush と同じ変換（各成分 × 255 を四捨五入）。</summary>
        static Rgba32 ToBrushBytes(Color c) => new Rgba32((byte)Mathf.RoundToInt(c.r * 255), (byte)Mathf.RoundToInt(c.g * 255), (byte)Mathf.RoundToInt(c.b * 255), (byte)Mathf.RoundToInt(c.a * 255));
        /// <summary>傾き (x, y) の法線の色（<see cref="SetBrushNormal"/> と同じ: 長さが 1 を超える傾きは z = 0 の向きに縮める）。</summary>
        static Color NormalColor(float x, float y, float a)
        {
            float l2 = x * x + y * y; if (l2 > 1) { float l = Mathf.Sqrt(l2); x /= l; y /= l; l2 = 1; }
            float z = Mathf.Sqrt(1 - l2);
            return new Color(x * .5f + .5f, y * .5f + .5f, z * .5f + .5f, a);
        }

        internal void SetMaterialScalar(PaintChannel c, float value)
        {
            value = Mathf.Clamp01(value);
            if (c == PaintChannel.Roughness) brush.materialRoughness = value;
            else if (c == PaintChannel.Metallic) brush.materialMetallic = value;
            else if (c == PaintChannel.Height) brush.materialHeight = value;
            else throw new ArgumentException(c + " is not a scalar channel.", nameof(c));
        }
        float MaterialScalar(PaintChannel c) => c == PaintChannel.Roughness ? brush.materialRoughness : c == PaintChannel.Metallic ? brush.materialMetallic : brush.materialHeight;
        internal void SetMaterialNormal(float x, float y)
        {
            float l2 = x * x + y * y; if (l2 > 1) { float l = Mathf.Sqrt(l2); x /= l; y /= l; }
            brush.materialNormalX = x; brush.materialNormalY = y;
        }

        /// <summary>スポイトの色をマテリアルの今のチャンネルの値にする（Color は描画色）。</summary>
        void PickIntoMaterial(Rgba32 c)
        {
            switch (channel)
            {
                case PaintChannel.Color: brush.color = new Color(c.R / 255f, c.G / 255f, c.B / 255f, 1); break;
                case PaintChannel.Emission: brush.materialEmission = new Color(c.R / 255f, c.G / 255f, c.B / 255f, 1); break;
                case PaintChannel.Normal: SetMaterialNormal(c.R / 255f * 2 - 1, c.G / 255f * 2 - 1); break;
                default: SetMaterialScalar(channel, c.R / 255f); break;
            }
        }

        /// <summary>読み込んだブラシの設定のマテリアルの項目を確かめる（知らないチャンネルの印・範囲の外の値は読まずに断る）。</summary>
        static void ValidateMaterial(BrushState b)
        {
            if ((b.materialChannels & ~AllChannelBits) != 0) throw new InvalidDataException("Unsupported brush settings: unknown material channels " + b.materialChannels + ".");
            foreach (float v in new[] { b.materialRoughness, b.materialMetallic, b.materialHeight, b.materialEmission.r, b.materialEmission.g, b.materialEmission.b, b.materialEmission.a })
                if (float.IsNaN(v) || v < 0 || v > 1) throw new InvalidDataException("Unsupported brush settings: a material value is outside 0..1.");
            foreach (float v in new[] { b.materialNormalX, b.materialNormalY })
                if (float.IsNaN(v) || v < -1 || v > 1) throw new InvalidDataException("Unsupported brush settings: a material normal is outside -1..1.");
        }
        /// <summary>プリセットを選んだときに残すマテリアルの項目を写す。</summary>
        static void CopyMaterial(BrushState from, BrushState to)
        {
            to.material = from.material; to.materialChannels = from.materialChannels; to.materialEmission = from.materialEmission;
            to.materialRoughness = from.materialRoughness; to.materialMetallic = from.materialMetallic; to.materialHeight = from.materialHeight;
            to.materialNormalX = from.materialNormalX; to.materialNormalY = from.materialNormalY;
        }

        // ───────── プロパティの欄 ─────────

        /// <summary>ブラシの「マテリアル」の節: オン/オフ、塗るチャンネル（3 列のボタン）、組のチャンネルごとの値。</summary>
        void MaterialSection(UiRows rows)
        {
            if (!ToolSection(rows, "brush-material", L.Tr("Brush Material"), "layers")) return;
            bool on = PaintGui.FitToggle(Mark("material.toggle", rows.Row()), L.Tr("Paint several channels at once"), brush.material,
                L.Tr("One stroke paints every channel checked below with its own value, using the same dabs (2D and 3D). One undo takes all of them back. Off: the brush paints the selected channel with the foreground color."),
                stroke == null);
            if (on != brush.material) SetMaterialMode(on);
            if (!brush.material) { rows.Space(4); return; }
            for (int start = 0; start < Channels.Length; start += ChannelColumns)
            {
                var cells = UiRows.Split(rows.Row(ChannelChipHeight - 4), ChannelColumns, 4);
                for (int k = 0; k < ChannelColumns && start + k < Channels.Length; k++)
                {
                    var c = Channels[start + k]; var cell = Mark("material.chip." + c, cells[k]);
                    bool included = MaterialIncludes(c), hover = cell.Contains(Event.current.mousePosition) && GUI.enabled && stroke == null;
                    PaintGui.Rounded(cell, included ? PaintTheme.AccentDim : hover ? PaintTheme.ControlHover : PaintTheme.ControlBg, 4);
                    PaintGui.Icon(new Rect(cell.x + 3, cell.y, 18, cell.height), included ? "check" : ChannelIcon(c), included ? Color.white : PaintTheme.TextDim, 14);
                    PaintGui.Text(new Rect(cell.x + 21, cell.y, cell.width - 24, cell.height), PaintGui.Fit(L.Tr(c.ToString()), cell.width - 24, PaintTheme.LabelSmall), PaintTheme.LabelSmall, included ? Color.white : PaintTheme.Text);
                    if (c == channel) PaintGui.Rounded(new Rect(cell.x + 4, cell.yMax - 3, cell.width - 8, 2), included ? Color.white : PaintTheme.Accent, 1); // 今のチャンネル（表示しているもの）の印
                    PaintGui.Tooltip(cell, included ? L.Tr("{0}: painted by the stroke (click to leave it out)", L.Tr(c.ToString())) : L.Tr("{0}: not painted (click to paint it too)", L.Tr(c.ToString())));
                    if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && hover) { SetMaterialChannel(c, !included); Event.current.Use(); }
                }
            }
            foreach (var c in Channels) if (MaterialIncludes(c)) MaterialValueRow(rows, c);
            if (EditingMask) NoteRow(rows, L.Tr("While you edit a mask, strokes paint the mask only."), NoteKind.Info);
            rows.Space(4);
        }

        /// <summary>組のチャンネルの値の行（Color は描画色の見本、Emission は色の見本、データのチャンネルは 0〜1、Normal は傾き）。</summary>
        void MaterialValueRow(UiRows rows, PaintChannel c)
        {
            var row = Mark("material.value." + c, rows.Row());
            string name = L.Tr(c.ToString());
            switch (c)
            {
                case PaintChannel.Color:
                case PaintChannel.Emission:
                {
                    PaintGui.Text(new Rect(row.x, row.y, LabelColumn, row.height), PaintGui.Fit(name, LabelColumn - 6, PaintTheme.Label), PaintTheme.Label, GUI.enabled ? PaintTheme.Text : PaintTheme.TextDisabled);
                    var swatch = new Rect(row.x + LabelColumn, row.y + 1, row.width - LabelColumn, row.height - 2);
                    if (c == PaintChannel.Color)
                        PaintGui.ColorSwatch(swatch, brush.color, v => { brush.color = v; Repaint(); }, true, L.Tr("The foreground color (also in the Color panel)"), GUI.enabled && stroke == null);
                    else
                        PaintGui.ColorSwatch(swatch, new Color(brush.materialEmission.r, brush.materialEmission.g, brush.materialEmission.b, 1), v => { brush.materialEmission = new Color(v.r, v.g, v.b, 1); Repaint(); }, false, L.Tr("The emission color the stroke paints"), GUI.enabled && stroke == null);
                    break;
                }
                case PaintChannel.Normal:
                {
                    var cols = PaintGui.LabeledColumns(new Rect(row.x, row.y, row.width - 28, row.height), name, 56, 2);
                    double x = brush.materialNormalX, y = brush.materialNormalY;
                    double nx = PaintGui.KeepSlider(cols[0], L.TrIn("normal brush", "Tilt X"), x, -1, 1, "0.00", "", L.Tr("The Normal value as a direction: +1 leans right"));
                    double ny = PaintGui.KeepSlider(cols[1], L.TrIn("normal brush", "Tilt Y"), y, -1, 1, "0.00", "", L.Tr("+1 leans up (OpenGL / Unity)"));
                    if (nx != x || ny != y) SetMaterialNormal((float)nx, (float)ny);
                    if (PaintGui.IconButton(new Rect(row.xMax - 24, row.y, 24, row.height), "restart_alt", L.Tr("Flat brush value (128, 128, 255): paints a flat normal"), false, GUI.enabled && stroke == null, 16)) SetMaterialNormal(0, 0);
                    break;
                }
                default:
                {
                    float v = MaterialScalar(c);
                    float next = PaintGui.FitSlider(row, name, v, 0, 1, "0.00", "", L.Tr("The {0} value the stroke paints", name));
                    if (next != v) SetMaterialScalar(c, next);
                    break;
                }
            }
        }
    }
}
