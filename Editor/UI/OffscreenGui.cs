using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// IMGUI を RenderTexture に描いて PNG にする（見た目の確認用）。この devcontainer の GUI モードのエディタではシェーダーが
    /// 壊れて画面がマゼンタになるので、シェーダーが正しく動く batch-gl（-batchmode、xvfb の OpenGL）の常駐 Unity で使う。
    /// UI Toolkit の IMGUIContainer と同じ内部の入口（GUIUtility.BeginContainer と GUIClip の親の矩形）を反射で呼ぶので、Unity の
    /// 版が変わると動かなくなりうる（そのときは例外で知らせる）。描画のコードは通常のウィンドウと同じものを通る。
    /// </summary>
    internal static class OffscreenGui
    {
        internal static EventType[] Passes = { EventType.Layout, EventType.Repaint };
        internal static bool UseLayoutBegin = true;
        const BindingFlags Internal = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public;

        /// <summary>draw を Layout と Repaint の 2 回呼んで width × height に描き、PNG に書く。</summary>
        public static void RenderToPng(int width, int height, Action draw, string path, Color background)
        {
            var guiUtility = typeof(GUIUtility); var engine = guiUtility.Assembly;
            var beginContainer = guiUtility.GetMethod("BeginContainer", Internal, null, new[] { engine.GetType("UnityEngine.ObjectGUIState") }, null);
            var endContainer = guiUtility.GetMethod("Internal_EndContainer", Internal);
            var clip = engine.GetType("UnityEngine.GUIClip");
            var pushParent = clip.GetMethod("Internal_PushParentClip", Internal, null, new[] { typeof(Matrix4x4), typeof(Matrix4x4), typeof(Rect) }, null);
            var popParent = clip.GetMethod("Internal_PopParentClip", Internal);
            var layoutBegin = typeof(GUILayoutUtility).GetMethod("Begin", Internal, null, new[] { typeof(int) }, null);
            var layout = typeof(GUILayoutUtility).GetMethod("Layout", Internal, null, Type.EmptyTypes, null);
            if (beginContainer == null || endContainer == null || pushParent == null || popParent == null || layoutBegin == null || layout == null)
                throw new NotSupportedException("This Unity version does not expose the IMGUI internals OffscreenGui uses.");

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active; var previousEvent = Event.current;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, background);
                var state = Activator.CreateInstance(engine.GetType("UnityEngine.ObjectGUIState"), true);
                const int id = 0x59500002;
                foreach (var type in Passes)
                {
                    Event.current = new Event { type = type, mousePosition = new Vector2(-1000, -1000) };
                    beginContainer.Invoke(null, new[] { state });
                    pushParent.Invoke(null, new object[] { Matrix4x4.identity, Matrix4x4.identity, new Rect(0, 0, width, height) });
                    GL.PushMatrix(); GL.LoadPixelMatrix(0, width, height, 0);
                    try
                    {
                        RenderTexture.active = rt;
                        if (UseLayoutBegin) layoutBegin.Invoke(null, new object[] { id });
                        draw();
                        if (type == EventType.Layout) layout.Invoke(null, null);
                    }
                    catch (ExitGUIException) { }
                    finally { GL.PopMatrix(); popParent.Invoke(null, null); endContainer.Invoke(null, null); }
                }
                RenderTexture.active = rt; // 描く途中で 3D のプレビューなどが切り替えていても、この描画先を読む
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false); texture.Apply(false, false);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                Event.current = previousEvent; RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(texture); rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        /// <summary>ペイントのウィンドウを width × height で描く（ウィンドウは表示しない。デモのキューブと層を少し入れる）。</summary>
        public static void RenderWindow(TexturePaintWindow window, int width, int height, string path)
        {
            window.LayoutOverride = new Rect(0, 0, width, height);
            var onGui = typeof(TexturePaintWindow).GetMethod("OnGUI", BindingFlags.NonPublic | BindingFlags.Instance);
            RenderToPng(width, height, () => onGui.Invoke(window, null), path, PaintTheme.WindowBg);
        }
    }
}
