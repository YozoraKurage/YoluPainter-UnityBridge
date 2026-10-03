using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// 3D ビューで見せるもの（Substance Painter の C・B・M に当たる）: マテリアル（描き方どおり。中立かマテリアル表示）/ 1 つのチャンネルだけ
    /// （Color・Roughness・Metallic・Height・Normal の出力・Emission・選んだ層のマスクを、照明・環境・影・トーンマッピングなしでそのまま）/
    /// 焼いたメッシュマップだけ。キーは C（チャンネルを順に。最後の次はマテリアル）、Shift+B（メッシュマップを順に）、Shift+C（マテリアルへ）。
    /// B は筆のまま（Shift+B だけを使う）、M は矩形選択のまま。文字の欄の入力中は使わず、ストロークの最中は断って知らせる。各ビュー上部中央の
    /// ドロップダウンから独立して選べる。キーはポインタのビューへ。描き込み・ピック・ブラシのカーソルは見せ方によらない。見せ方は窓の状態（.ylp には入らない）。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        internal enum ModelShowKind { Material, Channel, Mask, MeshMap }
        [SerializeField] ModelShowKind modelShow = ModelShowKind.Material;
        [SerializeField] PaintChannel modelShowChannel;
        [SerializeField] MeshMapKind modelShowMap;

        internal ModelShowKind ModelShow => modelShow;
        internal PaintChannel ModelShowChannel => modelShowChannel;
        internal MeshMapKind ModelShowMap => modelShowMap;

        /// <summary>見せ方を変えられないとき（ストロークやツールのドラッグの最中）の理由。</summary>
        string ModelShowRefusal() => stroke != null || toolDragging ? L.Tr("Finish the stroke first; the 3D view keeps showing {0}.", ModelShowName()) : null;

        /// <summary>マテリアル（描き方どおりの表示）に戻す。</summary>
        internal bool ShowMaterialIn3D()
        {
            if (ModelShowRefusal() is string refused) { message = refused; return false; }
            showKeysInCanvas = false;
            if (modelShow == ModelShowKind.Material) return true;
            modelShow = ModelShowKind.Material;
            ModelShowChanged(L.Tr("3D view: the material ({0} shading).", previewShading == Yozolab.YoluPainter.Editor.Preview.PreviewShading.Material ? L.Tr("material") : L.Tr("neutral")));
            return true;
        }

        /// <summary>1 つのチャンネルだけを照明なしで見せる（使っていないチャンネルは暗い灰色で、そう知らせる）。</summary>
        internal bool ShowChannelIn3D(PaintChannel c)
        {
            if (ModelShowRefusal() is string refused) { message = refused; return false; }
            showKeysInCanvas = false;
            modelShow = ModelShowKind.Channel; modelShowChannel = c;
            bool used = YlpContent.UsedChannels(document).Contains(c);
            ModelShowChanged(L.Tr("3D view: {0} only, unlit (the values as they are).", L.Tr(c.ToString())) + (used ? "" : " " + L.Tr("This texture set does not use {0}, so it shows dark grey.", L.Tr(c.ToString()))));
            return true;
        }

        /// <summary>選んだ層のマスクだけを見せる（白が見える所。マスクの無い層では断る）。</summary>
        internal bool ShowMaskIn3D()
        {
            if (ModelShowRefusal() is string refused) { message = refused; return false; }
            if (SelectedMaskLayer == null) { message = L.Tr("The selected layer has no mask to show."); return false; }
            showKeysInCanvas = false;
            modelShow = ModelShowKind.Mask;
            ModelShowChanged(L.Tr("3D view: the mask of {0} only (white shows the layer).", SelectedMaskLayer.Name));
            return true;
        }

        /// <summary>今のテクスチャセットの焼いたメッシュマップだけを見せる（焼いていなければ断る）。</summary>
        internal bool ShowMeshMapIn3D(MeshMapKind kind)
        {
            if (ModelShowRefusal() is string refused) { message = refused; return false; }
            if (!meshMaps.TryGet(kind, out var map)) { message = L.Tr("{0} is not baked for this texture set (3D ▸ Bake Mesh Maps…).", MeshMapLabel(kind)); return false; }
            showKeysInCanvas = false;
            modelShow = ModelShowKind.MeshMap; modelShowMap = kind;
            var check = map.Provenance.Check(CurrentMeshMapExpectation());
            ModelShowChanged(L.Tr("3D view: the baked {0} only.", MeshMapLabel(kind)) + (check.State == MeshMapState.Stale ? " " + L.Tr("It is stale for the loaded model.") : ""));
            return true;
        }

        /// <summary>C: チャンネルを順に（マテリアル → 使っているチャンネル → マスク → マテリアル）。</summary>
        internal void CycleChannelIn3D()
        {
            var order = YlpContent.UsedChannels(document).OrderBy(c => c).Cast<object>().ToList();
            if (SelectedMaskLayer != null) order.Add(ModelShowKind.Mask);
            int at = modelShow == ModelShowKind.Channel ? order.IndexOf(modelShowChannel) : modelShow == ModelShowKind.Mask ? order.IndexOf(ModelShowKind.Mask) : -1;
            var next = at + 1 < order.Count ? order[at + 1] : null;
            if (next == null) ShowMaterialIn3D();
            else if (next is PaintChannel c) ShowChannelIn3D(c);
            else ShowMaskIn3D();
        }

        /// <summary>Shift+B: 焼いたメッシュマップを順に（マテリアル → 種類の順 → マテリアル）。焼いていなければ知らせる。</summary>
        internal void CycleMeshMapIn3D()
        {
            var kinds = meshMaps.Maps.Select(m => m.Kind).ToList();
            if (kinds.Count == 0) { if (ModelShowRefusal() is string refused) message = refused; else message = L.Tr("No mesh maps are baked for this texture set (3D ▸ Bake Mesh Maps…)."); return; }
            int at = modelShow == ModelShowKind.MeshMap ? kinds.IndexOf(modelShowMap) : -1;
            if (at + 1 < kinds.Count) ShowMeshMapIn3D(kinds[at + 1]); else ShowMaterialIn3D();
        }

        PaintLayer SelectedMaskLayer => document?.Layers.FirstOrDefault(l => l.Id == selectedLayer && l.Mask != null);

        void ModelShowChanged(string note) { message = note; repaintPixels = true; lightingRevision = -1; Repaint(); }

        /// <summary>見出しとメニューに出す、今見せているものの名前。</summary>
        internal string ModelShowName() => ViewShowName(modelShow, modelShowChannel, modelShowMap);
        string ViewShowName(ModelShowKind kind, PaintChannel shownChannel, MeshMapKind shownMap)
        {
            switch (kind)
            {
                case ModelShowKind.Channel: return L.Tr(shownChannel.ToString());
                case ModelShowKind.Mask: return L.Tr("Layer mask");
                case ModelShowKind.MeshMap: return L.Tr("{0} (mesh map)", MeshMapGridLabel(shownMap));
                default: return L.Tr("Material");
            }
        }

        // ───────── キー ─────────

        /// <summary>C・Shift+C・Shift+B（修飾キーは Shift だけ）。文字の欄の入力中は使わない。ストロークの最中は断って知らせる（キーは使う）。</summary>
        bool HandleModelShowKeys(Event e)
        {
            if (e.type != EventType.KeyDown || GUIUtility.keyboardControl != 0 || e.control || e.command || e.alt) return false;
            bool c = e.keyCode == KeyCode.C, b = e.keyCode == KeyCode.B && e.shift;
            if (!c && !b) return false;
            bool canvas = canvasRect.Contains(e.mousePosition) || canvasShowButtonForTests.Contains(e.mousePosition) ? true :
                surfaceRect.Contains(e.mousePosition) || modelShowButtonForTests.Contains(e.mousePosition) ? false :
                viewMode == ViewMode.Canvas || viewMode == ViewMode.Split && showKeysInCanvas;
            if ((canvas ? CanvasShowRefusal() : ModelShowRefusal()) is string refused) message = refused;
            else if (canvas)
            {
                if (b) CycleMeshMapIn2D();
                else if (e.shift) ShowMaterialIn2D();
                else CycleChannelIn2D();
            }
            else if (b) CycleMeshMapIn3D();
            else if (e.shift) ShowMaterialIn3D();
            else CycleChannelIn3D();
            NoteTookKey(e); e.Use(); Repaint(); return true;
        }

        // ───────── 3D ビューへ渡す ─────────

        /// <summary>照明なしの見せ方のテクスチャを全部のテクスチャセットのスロットへ（マテリアルの見せ方なら外す）。照明の更新の後に呼ぶ
        /// （Normal は照明と同じ Normal の出力を見せる）。</summary>
        void ShowModelShowTextures()
        {
            if (preview == null) return;
            if (modelShow == ModelShowKind.Material || !preview.HasModel) { preview.SetUnlitTextures(null); return; }
            if (modelShow == ModelShowKind.MeshMap && !meshMaps.TryGet(modelShowMap, out _)) { modelShow = ModelShowKind.Material; preview.SetUnlitTextures(null); return; }
            if (modelShow == ModelShowKind.Mask && SelectedMaskLayer == null && stroke == null) { modelShow = ModelShowKind.Material; preview.SetUnlitTextures(null); return; }
            SyncCurrentSet();
            var textures = new Dictionary<int, Texture>();
            foreach (var set in textureSets)
            {
                if (!set.InModel) continue;
                bool current = set == currentSet; var d = current ? document : set.Document;
                Texture t = null;
                switch (modelShow)
                {
                    case ModelShowKind.Channel:
                        if (!YlpContent.UsedChannels(d).Contains(modelShowChannel)) break;
                        if (modelShowChannel == PaintChannel.Normal) t = current ? materialNormal : SetLighting(set);
                        else if (current && modelShowChannel == channel) t = compositor.Texture; // 描いているあいだも動く
                        else t = ChannelTexture(set, d, modelShowChannel);
                        break;
                    case ModelShowKind.Mask: if (current) t = MaskTexture3D(); break;
                    case ModelShowKind.MeshMap: t = MeshMapTexture3D(set); break;
                }
                foreach (int slot in set.Slots) if (!textures.ContainsKey(slot)) textures[slot] = t;
            }
            preview.SetUnlitTextures(textures);
        }

        /// <summary>照明なしの Normal を見せるのに、照明の Normal の出力を作らせるか（Lighting.cs）。</summary>
        bool ModelShowWantsNormal => modelShow == ModelShowKind.Channel && modelShowChannel == PaintChannel.Normal;

        // マスクとメッシュマップの 3D 用のテクスチャ（変わったときだけ作る。ストロークの最中は前のもの）
        Texture2D maskTexture3D; (Guid layer, long stamp) maskTexture3DKey;
        readonly Dictionary<Guid, (long revision, MeshMapKind kind, Texture2D texture)> meshMapTextures3D = new Dictionary<Guid, (long, MeshMapKind, Texture2D)>();
        /// <summary>3D のマスクの長辺の上限（マスクの出力は画素ごとに読むので、大きい文書は縮めて読む）。</summary>
        internal const int MaskPreviewMaxSize = 1024;

        Texture2D MaskTexture3D() => MaskDisplayTexture(ref maskTexture3D, ref maskTexture3DKey, true);
        Texture2D MaskDisplayTexture(ref Texture2D texture, ref (Guid layer, long stamp) key, bool freezeStroke)
        {
            var layer = SelectedMaskLayer; if (layer == null) return null;
            var mask = layer.Mask;
            long stamp = unchecked(mask.Surface.Revision * 31 + mask.FilterRevision * 7 + (mask.Inverted ? 1 : 0) + (mask.Enabled ? 2 : 0) + (long)(mask.Density * 1000) * 131);
            if (texture != null && key.layer == layer.Id && (key.stamp == stamp || freezeStroke && stroke != null)) return texture;
            if (!freezeStroke && !AdmitPreviewDisplayWork()) return key.layer == layer.Id ? texture : null;
            float scale = Mathf.Max(1, Mathf.Max(document.Width, document.Height) / (float)MaskPreviewMaxSize);
            int w = Mathf.Max(1, Mathf.RoundToInt(document.Width / scale)), h = Mathf.Max(1, Mathf.RoundToInt(document.Height / scale));
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int sx = Mathf.Min(document.Width - 1, Mathf.FloorToInt((x + .5f) * scale)), sy = Mathf.Min(document.Height - 1, Mathf.FloorToInt((y + .5f) * scale));
                    byte v = (byte)Mathf.Clamp(Mathf.RoundToInt((float)mask.FactorAt(sx, sy) * 255), 0, 255); int o = (y * w + x) * 4;
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = v; rgba[o + 3] = 255;
                }
            texture = Upload(texture, rgba, w, h, "YoluPainter view mask");
            key = (layer.Id, stamp);
            return texture;
        }

        Texture2D MeshMapTexture3D(TextureSet set)
        {
            if (!set.MeshMaps.TryGet(modelShowMap, out var map)) return null;
            if (meshMapTextures3D.TryGetValue(set.Id, out var cached) && cached.texture != null && cached.revision == set.MeshMaps.Revision && cached.kind == modelShowMap) return cached.texture;
            var texture = Upload(cached.texture, map.ToRgba8(false), map.Width, map.Height, "YoluPainter 3D view mesh map " + set.Name);
            meshMapTextures3D[set.Id] = (set.MeshMaps.Revision, modelShowMap, texture);
            return texture;
        }

        void DisposeModelShowTextures()
        {
            if (maskTexture3D != null) DestroyImmediate(maskTexture3D);
            maskTexture3D = null; maskTexture3DKey = default;
            foreach (var t in meshMapTextures3D.Values) if (t.texture != null) DestroyImmediate(t.texture);
            meshMapTextures3D.Clear();
        }

        // ───────── 見出しとメニュー ─────────

        /// <summary>各ビュー上部の中央に今の表示名を出す。左右の操作があるときは空いている範囲へ収める。</summary>
        Rect DrawViewShowDropdown(Rect bar, Rect view, float left, float right, bool canvas)
        {
            string label = canvas ? CanvasShowName() : ModelShowName();
            float w = Mathf.Min(Mathf.Clamp(PaintGui.TextWidth(label, PaintTheme.Label) + 28, 86, 220), Mathf.Max(0, right - left - 4));
            var r = new Rect(Mathf.Clamp(view.center.x - w * .5f, left, Mathf.Max(left, right - w)), bar.y + 2, w, 22);
            if (canvas) canvasShowButtonForTests = r; else modelShowButtonForTests = r;
            string tip = canvas ? L.Tr("What the 2D view shows: the current composite, one channel, the layer mask or a baked mesh map. Independent of 3D and the painted channel (C · Shift+B · Shift+C).") :
                L.Tr("What the 3D view shows: the material, or one channel or one baked mesh map unlit, as it is (C: channels · Shift+B: mesh maps · Shift+C: the material)");
            if (w > 0 && PaintGui.Button(r, PaintGui.Fit(label, Mathf.Max(0, w - 24), PaintTheme.Label, false) + " ▾", (canvas ? canvasShow : modelShow) != ModelShowKind.Material, true, tip))
            {
                showKeysInCanvas = canvas;
                ViewShowMenu(canvas).DropDown(r);
            }
            return r;
        }
        internal Rect modelShowButtonForTests, canvasShowButtonForTests, compactViewButtonForTests, compactShadingButtonForTests;

        /// <summary>3D メニューの「見せるもの」。</summary>
        void ModelShowMenuItems(PaintMenu m) => ModelShowItems(m, L.Tr("3D View Shows") + "/");

        void ModelShowItems(PaintMenu m, string prefix)
        {
            foreach (var item in ModelShowChoices())
                AddItem(m, prefix + item.path, item.choose, item.reason == null, item.on, item.keys);
        }

        internal PaintMenu ModelShowMenu() => ViewShowMenu(false);
        PaintMenu ViewShowMenu(bool canvas)
        {
            var m = new PaintMenu(); string group = null;
            foreach (var item in ViewShowChoices(canvas))
            {
                string text = item.path; int slash = text.IndexOf('/');
                if (slash >= 0)
                {
                    string next = text.Substring(0, slash);
                    if (next != group) { m.AddSeparator(""); m.AddHeading(next); group = next; }
                    text = text.Substring(slash + 1);
                }
                var content = new GUIContent(item.keys == null ? text : Shortcut(text, item.keys));
                if (item.reason == null) m.AddRadioItem(content, item.on, () => { TryAction(item.choose); Repaint(); });
                else m.AddDisabledItem(content, item.on);
            }
            return m;
        }

        /// <summary>共通ドロップダウンに渡せる、表示名・選択・無効の理由。GUI の部品には依存しない。</summary>
        internal List<(string path, Action choose, string reason, bool on, string keys)> ModelShowChoices() => ViewShowChoices(false);
        List<(string path, Action choose, string reason, bool on, string keys)> ViewShowChoices(bool canvas)
        {
            var items = new List<(string, Action, string, bool, string)>();
            string locked = canvas ? CanvasShowRefusal() : ModelShowRefusal() ?? (!preview.HasModel ? L.Tr("Load a model first.") : null);
            var shown = canvas ? canvasShow : modelShow;
            var shownChannel = canvas ? canvasShowChannel : modelShowChannel;
            var shownMap = canvas ? canvasShowMap : modelShowMap;
            void Add(string path, Action action, string reason, bool on, string keys = null)
            {
                string why = locked ?? reason;
                items.Add((why == null ? path : path + " · " + why, action, why, on, keys));
            }
            Add(L.Tr("Material"), () => { if (canvas) ShowMaterialIn2D(); else ShowMaterialIn3D(); }, null, shown == ModelShowKind.Material, "Shift+C");
            var used = YlpContent.UsedChannels(document);
            foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel)))
            {
                var ch = c;
                Add(L.Tr("Channel") + "/" + L.Tr(c.ToString()) + (used.Contains(c) ? "" : " " + L.Tr("(not used)")), () => { if (canvas) ShowChannelIn2D(ch); else ShowChannelIn3D(ch); }, null, shown == ModelShowKind.Channel && shownChannel == c, c == used.FirstOrDefault() ? "C" : null);
            }
            Add(L.Tr("Channel") + "/" + L.Tr("Layer mask"), () => { if (canvas) ShowMaskIn2D(); else ShowMaskIn3D(); }, SelectedMaskLayer == null ? L.Tr("The selected layer has no mask to show.") : null, shown == ModelShowKind.Mask);
            var maps = meshMaps.Maps.Select(x => x.Kind).ToList();
            foreach (MeshMapKind kind in Enum.GetValues(typeof(MeshMapKind)))
            {
                var k = kind;
                Add(L.Tr("Mesh Map") + "/" + MeshMapLabel(kind), () => { if (canvas) ShowMeshMapIn2D(k); else ShowMeshMapIn3D(k); }, maps.Contains(kind) ? null : L.Tr("not baked for this texture set"), shown == ModelShowKind.MeshMap && shownMap == kind, maps.Count > 0 && kind == maps[0] ? "Shift+B" : null);
            }
            return items;
        }

        /// <summary>訳した文字のままメニューに足す（<see cref="Item"/> は文字を訳すので、ここは訳したものを渡す）。</summary>
        void AddItem(PaintMenu m, string text, Action action, bool enabled, bool on, string keys = null)
        {
            var content = new GUIContent(keys == null ? text : Shortcut(text, keys));
            if (enabled) m.AddItem(content, on, () => { TryAction(action); Repaint(); }); else m.AddDisabledItem(content, on);
        }
    }
}
