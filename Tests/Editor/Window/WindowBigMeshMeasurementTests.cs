using System.Collections;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Yozolab.YoluPainter.Editor;
namespace Yozolab.YoluPainter.Tests
{
    public sealed partial class WindowTests
    {
        [UnityTest]
        public IEnumerator LargeModelNewProjectMeasuresSelectionToDialogAndCreateToFirst3DFrame()
        {
            using (var fixture = new BigMeshFixture(true))
            {
                var dialog = NewProjectWindow.Open(new NewProjectSettings { Template = ProjectTemplate.Pbr, Resolution = 512 }, false, s => window.CreateProject(s));
                try
                {
                    Repaint(dialog);
                    var clock = Stopwatch.StartNew(); dialog.Settings.Model = fixture.Root; Repaint(dialog);
                    double selection = clock.Elapsed.TotalMilliseconds;
                    clock.Restart();
                    var at = new Vector2(dialog.position.width - 68, dialog.position.height - 26);
                    Mouse(dialog, EventType.MouseDown, at); if (dialog != null) Mouse(dialog, EventType.MouseUp, at);
                    Assert.That(window.TextureSets.Count, Is.EqualTo(32));
                    int before = window.Preview.RenderCount;
                    while (window.Preview.RenderCount <= before && clock.Elapsed.TotalSeconds < 30) { Repaint(window); yield return null; }
                    double first = clock.Elapsed.TotalMilliseconds;
                    Assert.That(window.Preview.RenderCount, Is.GreaterThan(before));
                    UnityEngine.Debug.Log($"GUI 合成モデル 70000 三角形/24 レンダラー/32 セット/512 PBR: 選択→ダイアログ={selection:F2} ms, 作る→最初の3D={first:F2} ms");
                }
                finally { if (dialog != null) dialog.Close(); }
            }
        }
    }
}
