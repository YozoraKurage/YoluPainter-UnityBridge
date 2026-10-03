using System;
using System.IO;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Brushes;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class BrushEffectLibraryTests
    {
        [TestCase(BrushEffect.Blur)] [TestCase(BrushEffect.Smudge)] [TestCase(BrushEffect.Clone)]
        public void LibrarySettingsKeepEffectsWithoutChangingSchema(BrushEffect effect)
        {
            string path = Path.Combine(Path.GetTempPath(), "yolupainter-brush-effect-" + Guid.NewGuid().ToString("N"));
            try
            {
                BrushLibrary.Personal.Folder = path;
                var settings = new BrushSettings { Effect = effect, BlurRadius = 21, SmudgeStrength = .37 };
                BrushLibrary.Personal.Add(new[] { new ImportedBrush("effect", "test", settings) }, "test");
                Assert.That(File.ReadAllText(Directory.GetFiles(path, "*.json")[0]), Does.Contain("\"schema\": 2"));
                BrushLibrary.Personal.Folder = path;
                var read = BrushLibrary.Personal.Presets[0].CreateSettings();
                Assert.That((read.Effect, read.BlurRadius, read.SmudgeStrength), Is.EqualTo((effect, 21, .37)));
            }
            finally { BrushLibrary.Personal.Folder = null; if (Directory.Exists(path)) Directory.Delete(path, true); }
        }
    }
}
