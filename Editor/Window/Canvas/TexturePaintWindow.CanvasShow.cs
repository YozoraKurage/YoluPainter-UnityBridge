using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>2D の表示は描き込み先と3Dの表示から独立した窓の状態。マテリアルは従来の合成、ほかは選んだ表示だけを写す。</summary>
    public sealed partial class TexturePaintWindow
    {
        [SerializeField] ModelShowKind canvasShow;
        [SerializeField] PaintChannel canvasShowChannel;
        [SerializeField] MeshMapKind canvasShowMap;
        bool showKeysInCanvas;
        Texture canvasDisplayTexture;
        Texture2D canvasMeshMapTexture, canvasMaskTexture;
        (Guid layer, long stamp) canvasMaskKey;
        (Guid set, long revision, MeshMapKind kind) canvasMeshMapKey;
        object canvasMeshMapSource;

        internal ModelShowKind CanvasShow => canvasShow;
        internal PaintChannel CanvasShowChannel => canvasShowChannel;
        internal MeshMapKind CanvasShowMap => canvasShowMap;
        internal Texture CanvasDisplayTexture => canvasDisplayTexture;
        internal string CanvasShowName() => ViewShowName(canvasShow, canvasShowChannel, canvasShowMap);
        internal List<(string path, Action choose, string reason, bool on, string keys)> CanvasShowChoices() => ViewShowChoices(true);
        internal PaintMenu CanvasShowMenu() => ViewShowMenu(true);
        string CanvasShowRefusal() => stroke != null || toolDragging ? L.Tr("Finish the stroke first; the 2D view keeps showing {0}.", CanvasShowName()) : null;

        internal bool ShowMaterialIn2D()
        {
            if (CanvasShowRefusal() is string refused) { message = refused; return false; }
            showKeysInCanvas = true; canvasShow = ModelShowKind.Material;
            CanvasShowChanged(L.Tr("2D view: the current composite.")); return true;
        }
        internal bool ShowChannelIn2D(PaintChannel c)
        {
            if (CanvasShowRefusal() is string refused) { message = refused; return false; }
            showKeysInCanvas = true; canvasShow = ModelShowKind.Channel; canvasShowChannel = c;
            CanvasShowChanged(L.Tr("2D view: {0} only, unlit (the values as they are).", L.Tr(c.ToString()))); return true;
        }
        internal bool ShowMaskIn2D()
        {
            if (CanvasShowRefusal() is string refused) { message = refused; return false; }
            if (SelectedMaskLayer == null) { message = L.Tr("The selected layer has no mask to show."); return false; }
            showKeysInCanvas = true; canvasShow = ModelShowKind.Mask;
            CanvasShowChanged(L.Tr("2D view: the selected layer mask (white shows the layer).")); return true;
        }
        internal bool ShowMeshMapIn2D(MeshMapKind kind)
        {
            if (CanvasShowRefusal() is string refused) { message = refused; return false; }
            if (!meshMaps.TryGet(kind, out _)) { message = L.Tr("{0} is not baked for this texture set (3D ▸ Bake Mesh Maps…).", MeshMapLabel(kind)); return false; }
            showKeysInCanvas = true; canvasShow = ModelShowKind.MeshMap; canvasShowMap = kind;
            CanvasShowChanged(L.Tr("2D view: the baked {0} only.", MeshMapLabel(kind))); return true;
        }
        void CanvasShowChanged(string note) { message = note; repaintPixels = true; Repaint(); }

        void CycleChannelIn2D()
        {
            var order = YlpContent.UsedChannels(document).OrderBy(c => c).Cast<object>().ToList();
            if (SelectedMaskLayer != null) order.Add(ModelShowKind.Mask);
            int at = canvasShow == ModelShowKind.Channel ? order.IndexOf(canvasShowChannel) : canvasShow == ModelShowKind.Mask ? order.IndexOf(ModelShowKind.Mask) : -1;
            var next = at + 1 < order.Count ? order[at + 1] : null;
            if (next is PaintChannel c) ShowChannelIn2D(c);
            else if (next != null) ShowMaskIn2D();
            else ShowMaterialIn2D();
        }
        void CycleMeshMapIn2D()
        {
            var kinds = meshMaps.Maps.Select(m => m.Kind).ToList();
            if (kinds.Count == 0) { message = L.Tr("No mesh maps are baked for this texture set (3D ▸ Bake Mesh Maps…)."); return; }
            int at = canvasShow == ModelShowKind.MeshMap ? kinds.IndexOf(canvasShowMap) : -1;
            if (at + 1 < kinds.Count) ShowMeshMapIn2D(kinds[at + 1]); else ShowMaterialIn2D();
        }
        bool CanvasShowWantsNormal => viewMode != ViewMode.Model && canvasShow == ModelShowKind.Channel && canvasShowChannel == PaintChannel.Normal && showNormalOutput;

        /// <summary>合成の後で表示用の参照を更新する。描画イベントでは合成しない。2Dのメッシュマップの写しは3Dから分ける。</summary>
        void UpdateCanvasShowTexture()
        {
            if (canvasShow == ModelShowKind.Mask && SelectedMaskLayer == null || canvasShow == ModelShowKind.MeshMap && !meshMaps.TryGet(canvasShowMap, out _))
                canvasShow = ModelShowKind.Material;
            canvasDisplayTexture = DisplayTexture;
            if (viewMode == ViewMode.Model) return;
            switch (canvasShow)
            {
                case ModelShowKind.Channel:
                    if (canvasShowChannel == channel) canvasDisplayTexture = DisplayTexture;
                    else if (canvasShowChannel == PaintChannel.Normal && showNormalOutput) canvasDisplayTexture = materialNormal;
                    else
                    {
                        currentSet.MaterialChannels.TryGetValue(canvasShowChannel, out var cached);
                        bool ready = cached?.Texture != null && ReferenceEquals(cached.Document, document) && cached.Width == document.Width && cached.Height == document.Height && (cached.Revision == document.Revision || stroke != null);
                        canvasDisplayTexture = ready ? cached.Texture : AdmitPreviewDisplayWork() ? ChannelTexture(currentSet, document, canvasShowChannel) : null;
                    }
                    break;
                case ModelShowKind.Mask: canvasDisplayTexture = MaskDisplayTexture(ref canvasMaskTexture, ref canvasMaskKey, false); break;
                case ModelShowKind.MeshMap:
                    meshMaps.TryGet(canvasShowMap, out var map);
                    var key = (currentSet.Id, meshMaps.Revision, canvasShowMap);
                    if (canvasMeshMapTexture == null || !canvasMeshMapKey.Equals(key) || !ReferenceEquals(canvasMeshMapSource, map))
                    {
                        if (!AdmitPreviewDisplayWork()) { canvasDisplayTexture = null; break; }
                        canvasMeshMapTexture = Upload(canvasMeshMapTexture, map.ToRgba8(false), map.Width, map.Height, "YoluPainter 2D view mesh map");
                        canvasMeshMapKey = key; canvasMeshMapSource = map;
                    }
                    canvasDisplayTexture = canvasMeshMapTexture;
                    break;
            }
        }
        void DisposeCanvasShowTextures()
        {
            if (canvasMeshMapTexture != null) DestroyImmediate(canvasMeshMapTexture);
            if (canvasMaskTexture != null) DestroyImmediate(canvasMaskTexture);
            canvasMaskTexture = canvasMeshMapTexture = null; canvasDisplayTexture = null; canvasMeshMapSource = null; canvasMeshMapKey = default; canvasMaskKey = default;
        }
    }
}
