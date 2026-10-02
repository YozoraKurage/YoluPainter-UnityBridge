using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>Export Images: 使っている全チャンネルを PNG に書き出す。置き換えの確認、Assets の中での色空間の設定。</summary>
    public sealed partial class WindowTests
    {
        static byte[] Pixels(byte[] png)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try { Assert.That(texture.LoadImage(png), Is.True); return texture.GetPixels32().SelectMany(c => new[] { c.r, c.g, c.b, c.a }).ToArray(); }
            finally { Object.DestroyImmediate(texture); }
        }

        [Test] public void ExportImagesWritesEveryUsedChannelAndAsksBeforeReplacing()
        {
            var d = window.Document; var layer = d.Layers[d.Layers.Count - 1];
            PaintDot(window, 200, 200);
            window.Channel = PaintChannel.Roughness; PaintDot(window, 400, 400); window.Channel = PaintChannel.Color;
            var fake = UseFakeDialogs(window); fake.Folder = NewTempPath(); Directory.CreateDirectory(fake.Folder);
            window.ExportImages();
            var files = Directory.GetFiles(fake.Folder).Select(Path.GetFileName).OrderBy(f => f).ToArray();
            Assert.That(files, Is.EqualTo(new[] { "Texture_Color.png", "Texture_Roughness.png" }), window.StatusMessage);
            Assert.That(Pixels(File.ReadAllBytes(Path.Combine(fake.Folder, "Texture_Roughness.png"))), Is.EqualTo(d.Composite(PaintChannel.Roughness)));
            fake.ConfirmAnswer = false; fake.Asked.Clear();
            PaintDot(window, 600, 600);
            byte[] before = File.ReadAllBytes(Path.Combine(fake.Folder, "Texture_Color.png"));
            window.ExportImages();
            Assert.That(fake.Asked, Does.Contain("Confirm: Replace images?"));
            Assert.That(File.ReadAllBytes(Path.Combine(fake.Folder, "Texture_Color.png")), Is.EqualTo(before), "declining leaves the files alone");
        }

        [Test] public void ExportImagesIntoAssetsSetsTheColourSpaceOfNewTexturesOnly()
        {
            string folder = "Assets/YoluPainterExportTest" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            try
            {
                var d = window.Document;
                PaintDot(window, 200, 200);
                window.Channel = PaintChannel.Height; PaintDot(window, 300, 300); window.Channel = PaintChannel.Color;
                var fake = UseFakeDialogs(window); fake.Folder = Path.GetFullPath(folder);
                window.ExportImages();
                var color = (TextureImporter)AssetImporter.GetAtPath(folder + "/Texture_Color.png");
                var height = (TextureImporter)AssetImporter.GetAtPath(folder + "/Texture_Height.png");
                Assert.That(color, Is.Not.Null, window.StatusMessage); Assert.That(height, Is.Not.Null);
                Assert.That(color.sRGBTexture, Is.True); Assert.That(height.sRGBTexture, Is.False, "data channels are linear");
                // 既にあるテクスチャの設定は変えない（違っていれば知らせる）
                color.sRGBTexture = false; color.SaveAndReimport();
                window.ExportImages();
                Assert.That(((TextureImporter)AssetImporter.GetAtPath(folder + "/Texture_Color.png")).sRGBTexture, Is.False);
                Assert.That(window.StatusMessage, Does.Contain("import settings were left as they are"));
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
    }
}
