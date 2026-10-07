using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// IMGUI を RenderTexture に描いて PNG にする（見た目の確認用。ウィンドウの絵を撮る）。この devcontainer の GUI モードのエディタではシェーダーが
    /// 壊れて画面がマゼンタになるので、シェーダーが正しく動く batch-gl（-batchmode、xvfb の OpenGL）の常駐 Unity で使う。
    /// UI Toolkit の IMGUIContainer と同じ内部の入口（GUIUtility.BeginContainer と GUIClip の親の矩形）を反射で呼ぶので、Unity の
    /// 版が変わると動かなくなりうる（そのときは例外で知らせる）。描画のコードは通常のウィンドウと同じものを通る。描く途中で例外が
    /// 出たら、その描画が積んだクリップを外してから例外を投げ直す（戻さないと、以後の描画が常駐 Unity の再起動まで空になる）。
    /// </summary>
    internal static class OffscreenGui
    {
        const BindingFlags Internal = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public;

        /// <summary>draw を Layout と Repaint の 2 回呼んで width × height に描き、PNG に書く。</summary>
        public static void RenderToPng(int width, int height, Action draw, string path, Color background, Vector2? mouse = null)
        {
            var guiUtility = typeof(GUIUtility); var engine = guiUtility.Assembly;
            var beginContainer = guiUtility.GetMethod("BeginContainer", Internal, null, new[] { engine.GetType("UnityEngine.ObjectGUIState") }, null);
            var endContainer = guiUtility.GetMethod("Internal_EndContainer", Internal);
            var clip = engine.GetType("UnityEngine.GUIClip");
            var pushParent = clip.GetMethod("Internal_PushParentClip", Internal, null, new[] { typeof(Matrix4x4), typeof(Matrix4x4), typeof(Rect) }, null);
            var popParent = clip.GetMethod("Internal_PopParentClip", Internal);
            var layoutBegin = typeof(GUILayoutUtility).GetMethod("Begin", Internal, null, new[] { typeof(int) }, null);
            var layout = typeof(GUILayoutUtility).GetMethod("Layout", Internal, null, Type.EmptyTypes, null);
            var clipCount = clip.GetMethod("Internal_GetCount", Internal); var clipPop = clip.GetMethod("Internal_Pop", Internal);
            if (beginContainer == null || endContainer == null || pushParent == null || popParent == null || layoutBegin == null || layout == null || clipCount == null || clipPop == null)
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
                foreach (var type in new[] { EventType.Layout, EventType.Repaint })
                {
                    Event.current = new Event { type = type, mousePosition = mouse ?? new Vector2(-1000, -1000) };
                    beginContainer.Invoke(null, new[] { state });
                    // 通常の EditorWindow が用意するエディタのスキンとスタイルを、オフスクリーンの入口でも用意する。
                    var skinMode = guiUtility.GetField("s_SkinMode", Internal);
                    skinMode?.SetValue(null, 1);
                    GUI.skin = UnityEditor.EditorGUIUtility.GetBuiltinSkin(UnityEditor.EditorSkin.Inspector);
                    typeof(UnityEditor.EditorStyles).GetMethod("UpdateSkinCache", Internal, null, Type.EmptyTypes, null)?.Invoke(null, null);
                    pushParent.Invoke(null, new object[] { Matrix4x4.identity, Matrix4x4.identity, new Rect(0, 0, width, height) });
                    Event.current = new Event { type = type, mousePosition = mouse ?? new Vector2(-1000, -1000) }; // BeginContainer がイベントを差し替えるので後で入れる
                    GL.PushMatrix(); GL.LoadPixelMatrix(0, width, height, 0);
                    int clips = (int)clipCount.Invoke(null, null); Exception failed = null;
                    try
                    {
                        RenderTexture.active = rt;
                        layoutBegin.Invoke(null, new object[] { id });
                        draw();
                        if (type == EventType.Layout) layout.Invoke(null, null);
                    }
                    catch (Exception ex) when (!(ex is ExitGUIException) && !(ex.InnerException is ExitGUIException)) { failed = ex; }
                    catch (Exception) { } // ExitGUI はふつうの終わり方
                    finally
                    {
                        // 描く途中で落ちたら、その描画が積んだクリップを外してから閉じる（残すと以後の描画が全部空になる）
                        while ((int)clipCount.Invoke(null, null) > clips) clipPop.Invoke(null, null);
                        GL.PopMatrix(); popParent.Invoke(null, null); endContainer.Invoke(null, null);
                    }
                    if (failed != null) { throw new InvalidOperationException("The GUI threw while drawing offscreen: " + (failed.InnerException ?? failed).Message, failed.InnerException ?? failed); }
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
    }
}
