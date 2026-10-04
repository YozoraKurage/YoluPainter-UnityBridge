using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Api;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// プラグイン（Yozolab.YoluPainter.Api）とウィンドウのつなぎ: プラグインのコマンドをメニューに足し、呼ぶときにこのウィンドウの
    /// プロジェクトを <see cref="IPainterSession"/> として渡す。プラグインからの変更は Core の API だけを通る（1 回ずつ Undo できる）。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        PluginSession pluginSession;
        internal IPainterSession Session => pluginSession ?? (pluginSession = new PluginSession(this));

        /// <summary>メニューに入れるプラグインのコマンド（区切りの後ろ）。</summary>
        internal void AddPluginCommands(PaintMenu menu, string menuName)
        {
            var commands = PainterPluginRegistry.CommandsIn(menuName).ToList();
            if (commands.Count == 0) return;
            if (menuName != PainterPluginRegistry.PluginsMenu) menu.AddSeparator("");
            foreach (var command in commands)
            {
                var c = command;
                var content = new GUIContent(c.Path);
                if (stroke == null && PainterPluginRegistry.IsEnabled(c, Session)) menu.AddItem(content, false, () => RunPluginCommand(c));
                else menu.AddDisabledItem(content);
            }
        }

        /// <summary>プラグインのコマンドを走らせる（ストロークの最中は走らせない）。失敗はステータスバーに出す。</summary>
        internal void RunPluginCommand(PainterPluginRegistry.Command command)
        {
            if (stroke != null) { message = L.Tr("A stroke is in progress."); return; }
            CancelToolDrag();
            string error = PainterPluginRegistry.Run(command, Session);
            if (error != null) message = L.Tr("The plugin command {0} failed: {1}", command.Path, error);
            repaintPixels = true; Repaint();
        }

        void ShowPluginList()
        {
            var entries = PainterPluginRegistry.Entries;
            string text = entries.Count == 0 ? L.Tr("No plugins are installed. A plugin is an assembly that references Yozolab.YoluPainter.Api and declares [assembly: ExportsPainterPlugin(typeof(…))].")
                : string.Join("\n\n", entries.Select(e => e.Name + " (" + e.Id + ")" + (e.Error != null ? "\n  " + L.Tr("Not loaded: {0}", e.Error) : "")
                    + string.Concat(e.Notes.Select(n => "\n  " + n))));
            Dialogs.Inform(L.Tr("Plugins"), L.Tr("Plugin API {0}", PainterApi.Version.ToString()) + "\n\n" + text);
        }

        /// <summary>プラグインに渡すプロジェクト（このウィンドウの今のテクスチャセットの文書。ほかのセットは見せない）。</summary>
        sealed class PluginSession : IPainterSession
        {
            readonly TexturePaintWindow w;
            public PluginSession(TexturePaintWindow window) { w = window; }

            PaintDocument Document => w != null && w.document != null ? w.document : throw new InvalidOperationException("The YoluPainter window is closed.");
            public int Width => Document.Width;
            public int Height => Document.Height;
            public string ProjectPath => w.projectPath;
            public GameObject Model => w.model;
            public PaintChannel Channel => w.channel;
            public Guid SelectedLayer => Document.Layers.Any(l => l.Id == w.selectedLayer) ? w.selectedLayer : Guid.Empty;
            public IReadOnlyList<PainterLayerInfo> Layers => Document.Layers
                .Select(l => new PainterLayerInfo(l.Id, l.ParentId, l.Name, l.Kind, l.Visible, l.IsGroup, l.Opacity,
                    ((PaintChannel[])Enum.GetValues(typeof(PaintChannel))).Where(l.IsChannelEnabled).ToArray()))
                .ToList();

            public byte[] ReadComposite(PaintChannel channel) { RequireChannel(channel); return YlpContent.Image(Document, channel); }

            public byte[] ReadLayer(Guid layerId, PaintChannel channel)
            {
                var document = Document; RequireChannel(channel);
                var layer = document.GetLayer(layerId);
                if (layer.Kind != LayerKind.Raster) throw new InvalidOperationException("Only paint layers have pixels (" + layer.Name + " is a " + layer.Kind + " layer).");
                var image = new byte[document.Width * document.Height * 4];
                if (!layer.TryGetChannel(channel, out var surface)) return image;
                int tile = document.TileSize; var bytes = new byte[tile * tile * 4];
                foreach (var coord in surface.EnumerateTileCoordinates())
                {
                    if (!surface.CopyTile(coord, bytes)) continue;
                    int w = Math.Min(tile, document.Width - coord.X * tile), h = Math.Min(tile, document.Height - coord.Y * tile);
                    for (int y = 0; y < h; y++) Buffer.BlockCopy(bytes, y * tile * 4, image, ((coord.Y * tile + y) * document.Width + coord.X * tile) * 4, w * 4);
                }
                return image;
            }

            public Guid AddImageLayer(string name, PaintChannel channel, byte[] rgba)
            {
                RequireEditable(); var document = Document; RequireChannel(channel);
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A layer needs a name.", nameof(name));
                Guid id = Guid.Empty;
                document.Batch(() =>
                {
                    var layer = document.AddLayer(name, above: w.AboveSelected());
                    document.SetChannelEnabled(layer.Id, channel, true);
                    document.ReplacePixels(layer.Id, channel, rgba, withinSelection: false);
                    id = layer.Id;
                });
                w.selectedLayer = id; Changed(); return id;
            }

            public bool ReplaceLayerPixels(Guid layerId, PaintChannel channel, byte[] rgba)
            {
                RequireEditable(); var document = Document;
                var layer = document.GetLayer(layerId);
                bool changed = false;
                document.Batch(() =>
                {
                    if (!layer.IsChannelEnabled(channel)) document.SetChannelEnabled(layerId, channel, true);
                    changed = document.ReplacePixels(layerId, channel, rgba);
                });
                Changed(); return changed;
            }

            public void ShowMessage(string message) { if (w != null) { w.message = message ?? ""; w.Repaint(); } }

            static void RequireChannel(PaintChannel channel) { if (!Enum.IsDefined(typeof(PaintChannel), channel)) throw new ArgumentOutOfRangeException(nameof(channel)); }
            void RequireEditable() { if (w == null || w.stroke != null) throw new InvalidOperationException("A stroke is in progress."); }
            void Changed() { w.repaintPixels = true; w.Repaint(); }
        }
    }
}
