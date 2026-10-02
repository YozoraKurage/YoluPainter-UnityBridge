using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>ツールのアイコン: 同梱の絵、描き手の画像での差し替え（選択中の絵が無ければ通常の絵）、プラグインの登録が最優先、
    /// 元に戻す、PNG でないもの・大きすぎるもの・ファイル名にできない ID を断る。描き手の画像は一時のプロジェクトに置く。</summary>
    public sealed class PainterToolIconsTests
    {
        string project, scratch;

        [SetUp] public void UseTemporaryProject()
        {
            project = Path.Combine(Path.GetTempPath(), "yolupainter-icons-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(project);
            scratch = Path.Combine(project, "scratch"); Directory.CreateDirectory(scratch);
            PainterSettings.ProjectRoot = project; PainterToolIcons.ForgetAll();
        }
        [TearDown] public void Clean()
        {
            PainterToolIcons.Unregister("brush"); PainterToolIcons.ForgetAll(); PainterSettings.ProjectRoot = null;
            if (Directory.Exists(project)) Directory.Delete(project, true);
        }

        string Png(string name, int width, int height, Color color)
        {
            var t = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try { var px = new Color32[width * height]; for (int i = 0; i < px.Length; i++) px[i] = color; t.SetPixels32(px); t.Apply(); string path = Path.Combine(scratch, name); File.WriteAllBytes(path, t.EncodeToPNG()); return path; }
            finally { UnityEngine.Object.DestroyImmediate(t); }
        }

        [Test] public void EveryBuiltInToolHasBundledIconsThatTakeTheUiColor()
        {
            foreach (var id in PainterToolIcons.BuiltInIds)
                foreach (var selected in new[] { false, true })
                {
                    var icon = PainterToolIcons.Get(id, selected);
                    Assert.That(icon.Texture, Is.Not.Null, id + (selected ? " (selected)" : ""));
                    Assert.That(icon.Tint, Is.True, "bundled icons are white and take the UI color");
                }
            Assert.That(PainterToolIcons.Get("brush", true).Texture, Is.Not.SameAs(PainterToolIcons.Get("brush", false).Texture), "the selected brush has its own (filled) icon");
        }

        [Test] public void AUserImageReplacesTheBundledIconUntilReset()
        {
            PainterToolIcons.SetUserIcon("brush", Png("red.png", 32, 32, Color.red));
            var icon = PainterToolIcons.Get("brush", false);
            Assert.That(icon.Tint, Is.False, "user images keep their own colors");
            Assert.That(icon.Texture.GetPixel(4, 4), Is.EqualTo(Color.red));
            Assert.That(PainterToolIcons.Get("brush", true).Texture, Is.SameAs(icon.Texture), "without a selected image the normal one is used");
            Assert.That(File.Exists(Path.Combine(PainterToolIcons.UserFolder, "brush.png")), Is.True);
            PainterToolIcons.SetUserIcon("brush", Png("blue.png", 32, 32, Color.blue), selected: true);
            Assert.That(PainterToolIcons.Get("brush", true).Texture.GetPixel(4, 4), Is.EqualTo(Color.blue));
            Assert.That(PainterToolIcons.HasUserIcon("brush"), Is.True);
            PainterToolIcons.ResetUserIcon("brush");
            Assert.That(PainterToolIcons.HasUserIcon("brush"), Is.False);
            Assert.That(PainterToolIcons.Get("brush", false).Tint, Is.True, "back to the bundled icon");
        }

        [Test] public void APluginRegistrationWinsOverTheUserImage()
        {
            PainterToolIcons.SetUserIcon("brush", Png("red.png", 16, 16, Color.red));
            var plugin = new Texture2D(8, 8);
            try
            {
                int changed = 0; Action count = () => changed++;
                PainterToolIcons.Changed += count;
                try { PainterToolIcons.Register("brush", plugin); } finally { PainterToolIcons.Changed -= count; }
                Assert.That(changed, Is.EqualTo(1));
                Assert.That(PainterToolIcons.Get("brush", false).Texture, Is.SameAs(plugin));
                Assert.That(PainterToolIcons.Get("brush", true).Texture, Is.SameAs(plugin), "no selected image: the same one");
                PainterToolIcons.Unregister("brush");
                Assert.That(PainterToolIcons.Get("brush", false).Texture.GetPixel(1, 1), Is.EqualTo(Color.red), "the user image again");
                Assert.That(PainterToolIcons.Get("my-plugin.tool", false).Texture, Is.Null, "an unknown tool has no icon");
            }
            finally { UnityEngine.Object.DestroyImmediate(plugin); }
        }

        [Test] public void BadImagesAndIdsAreRefusedWithoutChangingAnything()
        {
            string notPng = Path.Combine(scratch, "fake.png"); File.WriteAllText(notPng, "not an image");
            Assert.That(() => PainterToolIcons.SetUserIcon("brush", notPng), Throws.TypeOf<InvalidDataException>());
            Assert.That(() => PainterToolIcons.SetUserIcon("brush", Png("big.png", 300, 20, Color.white)), Throws.TypeOf<InvalidDataException>().With.Message.Contains("300"));
            Assert.That(() => PainterToolIcons.SetUserIcon("brush", Path.Combine(scratch, "missing.png")), Throws.TypeOf<FileNotFoundException>());
            foreach (var id in new[] { "", "a:b", "../x", "with space", new string('x', 65) })
                Assert.That(() => PainterToolIcons.SetUserIcon(id, Png("ok.png", 8, 8, Color.white)), Throws.ArgumentException, id);
            Assert.That(PainterToolIcons.HasUserIcon("brush"), Is.False);
            Assert.That(Directory.Exists(PainterToolIcons.UserFolder) ? Directory.GetFiles(PainterToolIcons.UserFolder) : new string[0], Is.Empty);
        }
    }
}
