using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Api;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// プラグインの受け口（Yozolab.YoluPainter.Api）: 読み込み、メニューへの置き場、失敗したプラグインだけを止めて登録を残さないこと、
    /// 断るプラグイン（ID・型・API の版）、コマンドから文書を変えると 1 回の Undo になること、プラグインの例外がウィンドウを壊さないこと。
    /// プラグインはこのファイルの型を PainterPluginRegistry.Load に直接渡す（アセンブリの属性では知らせない）。
    /// </summary>
    public sealed class PainterPluginTests
    {
        public sealed class GoodPlugin : PainterPlugin
        {
            public static int Runs; public static Texture2D Icon;
            public override string Id => "test.good";
            public override string DisplayName => "Good";
            public override void Configure(IPainterPluginBuilder builder)
            {
                builder.AddCommand("Filter/Test/Noise Layer", s => { Runs++; s.AddImageLayer("Noise", PaintChannel.Color, Noise(s.Width, s.Height)); });
                builder.AddCommand("Tools/Say Hello", s => s.ShowMessage("hello"), s => s.Layers.Count > 0);
                builder.AddCommand("Tools/Never", s => { }, s => false);
                builder.AddCommand("Tools/Throws", s => throw new InvalidOperationException("plugin bug"));
                if (Icon != null) builder.SetToolIcon("brush", Icon);
            }
        }
        public sealed class BrokenPlugin : PainterPlugin
        {
            public static Texture2D Icon;
            public override string Id => "test.broken";
            public override void Configure(IPainterPluginBuilder builder)
            {
                builder.AddCommand("Broken/Command", s => { });
                if (Icon != null) builder.SetToolIcon("eraser", Icon);
                throw new InvalidOperationException("configure failed");
            }
        }
        public sealed class SameIdPlugin : PainterPlugin { public override string Id => "test.good"; public override void Configure(IPainterPluginBuilder b) { b.AddCommand("Other", s => { }); } }
        public sealed class BadIdPlugin : PainterPlugin { public override string Id => "has space"; public override void Configure(IPainterPluginBuilder b) { } }
        public sealed class FuturePlugin : PainterPlugin { public override string Id => "test.future"; public override Version RequiredApi => new Version(0, 99); public override void Configure(IPainterPluginBuilder b) { } }
        public sealed class NoConstructorPlugin : PainterPlugin { public NoConstructorPlugin(int x) { } public override string Id => "test.ctor"; public override void Configure(IPainterPluginBuilder b) { } }
        public abstract class AbstractPlugin : PainterPlugin { }
        public sealed class ClashPlugin : PainterPlugin
        {
            public override string Id => "test.clash";
            public override void Configure(IPainterPluginBuilder b) { b.AddCommand("Filter/Test/Noise Layer", s => { }); b.AddCommand("Filter/Test/Mine", s => { }); }
        }
        public sealed class BadPathPlugin : PainterPlugin { public override string Id => "test.path"; public override void Configure(IPainterPluginBuilder b) { b.AddCommand("Filter", s => { }); } }

        static byte[] Noise(int w, int h)
        {
            var bytes = new byte[w * h * 4]; var random = new System.Random(1);
            for (int i = 0; i < bytes.Length; i += 4) { bytes[i] = (byte)random.Next(256); bytes[i + 1] = (byte)random.Next(256); bytes[i + 2] = (byte)random.Next(256); bytes[i + 3] = 255; }
            return bytes;
        }

        string project; TexturePaintWindow window;

        [SetUp] public void Create()
        {
            project = Path.Combine(Path.GetTempPath(), "yolupainter-plugins-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(project);
            PainterSettings.ProjectRoot = project; GoodPlugin.Runs = 0; GoodPlugin.Icon = BrokenPlugin.Icon = null;
        }
        [TearDown] public void Clean()
        {
            PainterPluginRegistry.Load(PainterPluginRegistry.Discover()); // 本物の一覧に戻す
            if (window != null) { string recovery = window.RecoveryRoot; Object.DestroyImmediate(window); if (Directory.Exists(recovery)) Directory.Delete(recovery, true); }
            foreach (var icon in new[] { GoodPlugin.Icon, BrokenPlugin.Icon }) if (icon != null) Object.DestroyImmediate(icon);
            PainterSettings.ProjectRoot = null;
            if (Directory.Exists(project)) Directory.Delete(project, true);
        }

        static List<string> MenuTexts(PaintMenu menu)
        {
            var field = typeof(PaintMenu).GetField("m_MenuItems", BindingFlags.NonPublic | BindingFlags.Instance) ?? typeof(PaintMenu).GetField("menuItems", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) Assert.Inconclusive("PaintMenu.menuItems is not there in this Unity version.");
            var texts = new List<string>();
            foreach (var item in (IEnumerable)field.GetValue(menu))
            {
                var t = item.GetType();
                bool separator = (bool)t.GetField("separator").GetValue(item);
                var content = (GUIContent)t.GetField("content").GetValue(item);
                bool enabled = t.GetField("func")?.GetValue(item) != null || t.GetField("func2")?.GetValue(item) != null;
                texts.Add(separator ? "---" : content.text + (enabled ? "" : " (disabled)"));
            }
            return texts;
        }

        [Test] public void APluginsCommandsGoIntoTheNamedMenuOrThePluginsMenu()
        {
            PainterPluginRegistry.Load(new[] { typeof(GoodPlugin) });
            var good = PainterPluginRegistry.Entries.Single();
            Assert.That(good.Loaded, Is.True, good.Error); Assert.That(good.Name, Is.EqualTo("Good"));
            Assert.That(PainterPluginRegistry.CommandsIn("Filter").Select(c => c.Path), Is.EqualTo(new[] { "Test/Noise Layer" }));
            Assert.That(PainterPluginRegistry.CommandsIn(PainterPluginRegistry.PluginsMenu).Select(c => c.Path), Is.EqualTo(new[] { "Tools/Say Hello", "Tools/Never", "Tools/Throws" }));
            Assert.That(PainterPluginRegistry.HasPluginsMenu, Is.True);
            window = ScriptableObject.CreateInstance<TexturePaintWindow>();
            var filter = new PaintMenu(); window.AddPluginCommands(filter, "Filter");
            Assert.That(MenuTexts(filter), Is.EqualTo(new[] { "---", "Test/Noise Layer" }), "after a separator at the end of the built-in Filter menu");
            var plugins = new PaintMenu(); window.AddPluginCommands(plugins, PainterPluginRegistry.PluginsMenu);
            Assert.That(MenuTexts(plugins), Is.EqualTo(new[] { "Tools/Say Hello", "Tools/Never (disabled)", "Tools/Throws" }));
        }

        [Test] public void ACommandEditsTheDocumentAsOneUndoStepAndPluginErrorsStayInThePlugin()
        {
            PainterPluginRegistry.Load(new[] { typeof(GoodPlugin) });
            window = ScriptableObject.CreateInstance<TexturePaintWindow>();
            var doc = window.Document; int layers = doc.Layers.Count; var selected = window.SelectedLayer;
            window.RunPluginCommand(PainterPluginRegistry.CommandsIn("Filter").Single());
            Assert.That(GoodPlugin.Runs, Is.EqualTo(1));
            Assert.That(doc.Layers.Count, Is.EqualTo(layers + 1));
            var added = doc.Layers.Single(l => l.Name == "Noise");
            Assert.That(window.SelectedLayer, Is.EqualTo(added.Id), "the new layer is selected");
            Assert.That(window.Session.ReadLayer(added.Id, PaintChannel.Color), Is.EqualTo(Noise(doc.Width, doc.Height)), "the image is the layer's pixels exactly");
            var order = doc.Layers.ToList(); Assert.That(order.IndexOf(added), Is.EqualTo(order.IndexOf(doc.GetLayer(selected)) + 1), "above the layer that was selected");
            Assert.That(doc.Undo(), Is.True);
            Assert.That(doc.Layers.Count, Is.EqualTo(layers), "one undo removes the layer and its pixels together");
            window.RunPluginCommand(PainterPluginRegistry.CommandsIn(PainterPluginRegistry.PluginsMenu).Single(c => c.Path == "Tools/Throws"));
            Assert.That(window.StatusMessage, Does.Contain("plugin bug"));
            window.RunPluginCommand(PainterPluginRegistry.CommandsIn(PainterPluginRegistry.PluginsMenu).Single(c => c.Path == "Tools/Say Hello"));
            Assert.That(window.StatusMessage, Is.EqualTo("hello"));
        }

        [Test] public void TheSessionReadsAndReplacesWithinTheSelection()
        {
            window = ScriptableObject.CreateInstance<TexturePaintWindow>();
            var s = window.Session; var doc = window.Document;
            Assert.That(s.Width, Is.EqualTo(doc.Width)); Assert.That(s.SelectedLayer, Is.EqualTo(window.SelectedLayer));
            Assert.That(s.Layers.Select(l => l.Id), Is.EqualTo(doc.Layers.Select(l => l.Id)));
            var id = s.SelectedLayer; doc.SetSelection(SelectionMask.Rectangle(doc, 0, 0, 16, 16));
            var white = Enumerable.Repeat((byte)255, doc.Width * doc.Height * 4).ToArray();
            Assert.That(s.ReplaceLayerPixels(id, PaintChannel.Roughness, white), Is.True, "the channel is enabled as part of the same step");
            Assert.That(doc.GetLayer(id).IsChannelEnabled(PaintChannel.Roughness), Is.True);
            var read = s.ReadLayer(id, PaintChannel.Roughness);
            Assert.That(read[(5 * doc.Width + 5) * 4 + 3], Is.EqualTo(255)); Assert.That(read[(100 * doc.Width + 100) * 4 + 3], Is.EqualTo(0), "outside the selection");
            Assert.That(s.ReadComposite(PaintChannel.Roughness), Is.EqualTo(doc.Composite(PaintChannel.Roughness)));
            Assert.That(doc.Undo(), Is.True);
            Assert.That(doc.GetLayer(id).IsChannelEnabled(PaintChannel.Roughness), Is.False, "one undo: pixels and the enabled channel");
            Assert.That(() => s.AddImageLayer("Bad", PaintChannel.Color, new byte[3]), Throws.ArgumentException);
            Assert.That(doc.Layers.Count, Is.EqualTo(1), "a refused image adds no layer");
        }

        [Test] public void BrokenAndRefusedPluginsAreStoppedWithoutLeavingAnything()
        {
            GoodPlugin.Icon = new Texture2D(4, 4); BrokenPlugin.Icon = new Texture2D(4, 4);
            PainterPluginRegistry.Load(new[] { typeof(GoodPlugin), typeof(BrokenPlugin), typeof(SameIdPlugin), typeof(BadIdPlugin), typeof(FuturePlugin),
                typeof(NoConstructorPlugin), typeof(AbstractPlugin), typeof(string), null, typeof(ClashPlugin), typeof(BadPathPlugin) });
            var entries = PainterPluginRegistry.Entries;
            string Error(int i) => entries[i].Error ?? "";
            Assert.That(entries[0].Loaded, Is.True);
            Assert.That(Error(1), Does.Contain("configure failed"));
            Assert.That(PainterPluginRegistry.Commands.Any(c => c.Path == "Broken/Command"), Is.False, "a plugin that failed in Configure keeps none of its commands");
            Assert.That(PainterToolIcons.Get("eraser", false).Texture, Is.Not.SameAs(BrokenPlugin.Icon), "nor its tool icons");
            Assert.That(PainterToolIcons.Get("brush", false).Texture, Is.SameAs(GoodPlugin.Icon));
            Assert.That(Error(2), Does.Contain("already uses the id"));
            Assert.That(Error(3), Does.Contain("not 1–64"));
            Assert.That(Error(4), Does.Contain("0.99"));
            Assert.That(Error(5), Does.Contain("parameterless"));
            Assert.That(Error(6), Does.Contain("abstract"));
            Assert.That(Error(7), Does.Contain("PainterPlugin"));
            Assert.That(Error(8), Does.Contain("null"));
            Assert.That(entries[9].Loaded, Is.True, "a clashing command is left out, the plugin still loads");
            Assert.That(entries[9].Notes.Single(), Does.Contain("Filter/Test/Noise Layer"));
            Assert.That(PainterPluginRegistry.CommandsIn("Filter").Select(c => c.Owner.Id), Is.EqualTo(new[] { "test.good", "test.clash" }));
            Assert.That(Error(10), Does.Contain("names a menu but no command"));
            PainterPluginRegistry.Load(Array.Empty<Type>());
            Assert.That(PainterToolIcons.Get("brush", false).Texture, Is.Not.SameAs(GoodPlugin.Icon), "reloading removes the old registrations");
            Assert.That(PainterPluginRegistry.HasPluginsMenu, Is.False);
        }
    }
}
