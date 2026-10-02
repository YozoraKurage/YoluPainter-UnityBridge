using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>.ylp のインスペクター。取り込んだ結果（ドキュメントの大きさ・チャンネル）、YoluPainter で開くボタン、
    /// テクスチャの設定（Apply で取り込み直す）を出す。取り込んだ結果は <see cref="YlpImportInfo"/> から読む（.ylp を開き直さない）。</summary>
    [CustomEditor(typeof(YlpImporter)), CanEditMultipleObjects]
    internal sealed class YlpImporterEditor : ScriptedImporterEditor
    {
        SerializedProperty mipMaps, filter, wrap, aniso, compression;

        public override void OnEnable()
        {
            base.OnEnable();
            mipMaps = serializedObject.FindProperty(nameof(YlpImporter.generateMipMaps));
            filter = serializedObject.FindProperty(nameof(YlpImporter.filterMode));
            wrap = serializedObject.FindProperty(nameof(YlpImporter.wrapMode));
            aniso = serializedObject.FindProperty(nameof(YlpImporter.anisoLevel));
            compression = serializedObject.FindProperty(nameof(YlpImporter.compression));
        }

        public override void OnInspectorGUI()
        {
            if (targets.Length == 1) DrawSummary(((AssetImporter)target).assetPath);
            else EditorGUILayout.HelpBox("Select a single file to see its channels or open it in YoluPainter.", MessageType.None);

            EditorGUILayout.Space();
            serializedObject.Update();
            EditorGUILayout.LabelField("Textures", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(mipMaps, new GUIContent("Generate Mip Maps"));
            EditorGUILayout.PropertyField(filter, new GUIContent("Filter Mode"));
            EditorGUILayout.PropertyField(wrap, new GUIContent("Wrap Mode"));
            EditorGUILayout.PropertyField(aniso, new GUIContent("Aniso Level"));
            EditorGUILayout.PropertyField(compression, new GUIContent("Compression", "Compressed uses " + YlpImporter.CompressedFormat + " (desktop). Sizes that are not multiples of 4 stay uncompressed with a warning."));
            serializedObject.ApplyModifiedProperties();
            ApplyRevertGUI();
        }

        void DrawSummary(string assetPath)
        {
            var info = YlpImporter.LoadInfo(assetPath);
            if (info == null) EditorGUILayout.HelpBox("This file has not been imported yet.", MessageType.Info);
            else if (info.channels.Length == 0) EditorGUILayout.HelpBox("This file could not be imported: " + info.error, MessageType.Error);
            else
            {
                EditorGUILayout.LabelField("Document Size", info.width + " x " + info.height);
                EditorGUILayout.LabelField("Channels", string.Join(", ", info.channels.Select(c => c + (YlpContent.IsColor(c) ? " (sRGB)" : " (linear)"))));
                EditorGUILayout.LabelField("Main Texture", YlpImporter.MainChannel(info.channels).ToString());
                if (info.fromNativeDocument) EditorGUILayout.HelpBox("The file has no usable composite images, so the textures were composited from the native document.", MessageType.None);
            }
            if (GUILayout.Button("Open in YoluPainter"))
            {
                TexturePaintWindow.OpenFileInWindow(YlpImporter.FullPath(assetPath));
                GUIUtility.ExitGUI(); // 別のウィンドウを開いた後のレイアウトを続けない
            }
        }
    }
}
