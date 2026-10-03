using System;
using System.Linq;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Editor
{
    public sealed partial class TexturePaintWindow
    {
        internal PaintMenu EmptyLayerMenu()
        {
            var menu = new PaintMenu(); bool idle = stroke == null && !toolDragging;
            Item(menu, "New Layer", AddPaintLayer, idle, keys: "Ctrl+Shift+N");
            Item(menu, "New Fill Layer", AddFillLayerHere, idle);
            Item(menu, "New Group", () => selectedLayer = document.AddGroup(L.Tr("Group"), above: AboveSelected()).Id, idle);
            foreach (var (label, make) in AdjustmentMenu)
            {
                var build = make; var name = label;
                Item(menu, L.Tr("New Adjustment Layer") + "/" + L.Tr(name), () => selectedLayer = document.AddAdjustmentLayer(L.Tr(name), build(), above: AboveSelected()).Id, idle);
            }
            menu.AddSeparator("");
            Item(menu, "Paste", PasteClipboard, idle && PainterClipboard.Content != null, keys: "Ctrl+V");
            menu.AddSeparator("");
            string prefix = L.Tr("Apply Smart Material") + "/";
            foreach (var material in ImageResources.Smart.Where(m => m.Kind == SmartKind.Material))
            {
                string key = AssetKey(AssetSource.Project, material.Id.ToString("D"));
                Item(menu, prefix + L.Tr("This Project") + "/" + material.Name.Replace('/', '／'), () => PlaceSmartAsset(key), idle);
            }
            foreach (var entry in BuiltInSmartMaterials.All.Where(m => m.Kind == SmartKind.Material))
            {
                string key = AssetKey(AssetSource.BuiltIn, entry.Key);
                Item(menu, prefix + L.Tr("Built-in") + "/" + L.Tr(entry.Name), () => PlaceSmartAsset(key), idle);
            }
            if (selectedAsset != null && TrySmartKind(selectedAsset, out var kind) && kind == SmartKind.Material)
            {
                string key = selectedAsset;
                Item(menu, "Apply Selected Smart Material", () => PlaceSmartAsset(key), idle);
            }
            return menu;
        }
        void LayerBlankContext(Rect viewport, float content, float scroll, float width)
        {
            var e = Event.current;
            var empty = Rect.MinMaxRect(viewport.x, Mathf.Max(viewport.y, viewport.y + content - scroll), viewport.x + width, viewport.yMax);
            if (e.type == EventType.ContextClick && GUI.enabled && empty.height > 0 && empty.Contains(e.mousePosition))
            {
                CancelMenuInput(); EmptyLayerMenu().ShowAsContext(); e.Use();
            }
        }
    }
}
