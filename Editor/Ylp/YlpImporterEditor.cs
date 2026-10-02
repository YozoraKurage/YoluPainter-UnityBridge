using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>.ylp のインスペクター。取り込んだ結果（ドキュメントの大きさ・チャンネル）と YoluPainter で開くボタン。設定は無い
    /// （テクスチャを作らないので）。取り込んだ結果は <see cref="YlpImportInfo"/> から読む（.ylp を開き直さない）。</summary>
    [CustomEditor(typeof(YlpImporter)), CanEditMultipleObjects]
    internal sealed class YlpImporterEditor : ScriptedImporterEditor
    {
        protected override bool needsApplyRevert => false;

        public override void OnInspectorGUI()
        {
            if (targets.Length == 1) DrawSummary(((AssetImporter)target).assetPath);
            else EditorGUILayout.HelpBox("Select a single file to see its channels or open it in YoluPainter.", MessageType.None);
        }

        void DrawSummary(string assetPath)
        {
            var info = YlpImporter.LoadInfo(assetPath);
            if (info == null) EditorGUILayout.HelpBox("This file has not been imported yet.", MessageType.Info);
            else if (!string.IsNullOrEmpty(info.error)) EditorGUILayout.HelpBox("This file could not be imported: " + info.error, MessageType.Error);
            else
            {
                EditorGUILayout.LabelField("Document Size", info.width + " x " + info.height);
                EditorGUILayout.LabelField("Channels", info.channels.Length == 0 ? "(none painted)" : string.Join(", ", info.channels.Select(c => c.ToString())));
                EditorGUILayout.LabelField("File Format", info.format <= 1 ? "1 (before the format was recorded)" : info.format.ToString());
                if (!string.IsNullOrEmpty(info.savedBy)) EditorGUILayout.LabelField("Saved By", info.savedBy);
                if (!string.IsNullOrEmpty(info.createdBy)) EditorGUILayout.LabelField("Created By", info.createdBy);
            }
            EditorGUILayout.HelpBox("A .ylp is a YoluPainter work file and gives no textures: where the file is shared, YoluPainter may not be installed. "
                + "For materials, use Export Images (or lilToon… in YoluPainter), which writes PNG textures into Assets.", MessageType.Info);
            if (GUILayout.Button("Open in YoluPainter"))
            {
                TexturePaintWindow.OpenFileInWindow(YlpImporter.FullPath(assetPath));
                GUIUtility.ExitGUI(); // 別のウィンドウを開いた後のレイアウトを続けない
            }
        }
    }

    /// <summary>.ylp の主オブジェクトの Project ウィンドウのサムネイルとプレビュー。.ylp の thumbnail.png から描き、アセットには入れない。</summary>
    [CustomEditor(typeof(YlpImportInfo))]
    internal sealed class YlpImportInfoEditor : UnityEditor.Editor
    {
        Texture2D preview;
        void OnDisable() { if (preview != null) DestroyImmediate(preview); preview = null; }

        public override void OnInspectorGUI() { }
        public override bool HasPreviewGUI() => Preview() != null;
        public override void OnPreviewGUI(Rect r, GUIStyle background)
        {
            var texture = Preview(); if (texture == null) return;
            EditorGUI.DrawTextureTransparent(r, texture, ScaleMode.ScaleToFit);
        }
        Texture2D Preview()
        {
            if (preview == null) preview = YlpImporter.LoadThumbnail(AssetDatabase.GetAssetPath(target));
            return preview;
        }

        /// <summary>Project ウィンドウのアイコン: サムネイルを最近傍で width × height に（アルファはそのまま）。</summary>
        public override Texture2D RenderStaticPreview(string assetPath, Object[] subAssets, int width, int height)
        {
            var source = YlpImporter.LoadThumbnail(assetPath);
            if (source == null) return null;
            try
            {
                var pixels = source.GetPixels32(); int sw = source.width, sh = source.height;
                float scale = Mathf.Min(width / (float)sw, height / (float)sh);
                int ox = (int)((width - sw * scale) / 2), oy = (int)((height - sh * scale) / 2);
                var result = new Color32[width * height];
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    int sx = Mathf.FloorToInt((x - ox) / scale), sy = Mathf.FloorToInt((y - oy) / scale);
                    if (sx >= 0 && sy >= 0 && sx < sw && sy < sh) result[y * width + x] = pixels[sy * sw + sx];
                }
                var icon = new Texture2D(width, height, TextureFormat.RGBA32, false);
                icon.SetPixels32(result); icon.Apply(false, false);
                return icon;
            }
            finally { DestroyImmediate(source); }
        }
    }
}
