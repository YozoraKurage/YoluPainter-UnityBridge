using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Editor
{
    [ScriptedImporter(1, "ylsmart")]
    internal sealed class SmartMaterialImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            var info = ScriptableObject.CreateInstance<SmartMaterialImportInfo>();
            info.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            try
            {
                string path = FileUtil.GetPhysicalPath(ctx.assetPath);
                if (new FileInfo(path).Length > YlpArchive.MaxTotalBytes) throw new InvalidDataException("The smart material file exceeds the read budget.");
                var bytes = File.ReadAllBytes(path);
                var material = SmartMaterialFile.Read(bytes); var summary = SmartMaterialFile.ReadInfo(bytes);
                info.format = summary.Format; info.width = material.Width; info.height = material.Height; info.layerCount = material.LayerCount;
                info.mask = material.Kind == SmartKind.Mask; info.displayName = material.Name; info.channels = string.Join(", ", material.Channels);
                info.savedBy = summary.SavedBy.ToString(); info.error = "";
                var pixels = SmartPreview.Render(material, 128);
                var thumbnail = new Texture2D(128, 128, TextureFormat.RGBA32, false, true) { name = "Preview", filterMode = FilterMode.Bilinear };
                thumbnail.LoadRawTextureData(pixels); thumbnail.Apply(false, false);
                info.thumbnail = thumbnail; ctx.AddObjectToAsset("Preview", thumbnail);
            }
            catch (Exception ex) { info.error = ex.Message; ctx.LogImportError(L.Tr("The smart material cannot be imported: {0}", ex.Message)); }
            ctx.AddObjectToAsset("SmartMaterial", info); ctx.SetMainObject(info);
        }
    }

    [CustomEditor(typeof(SmartMaterialImporter))]
    internal sealed class SmartMaterialImporterEditor : ScriptedImporterEditor
    {
        protected override bool needsApplyRevert => false;
        public override void OnInspectorGUI()
        {
            var info = AssetDatabase.LoadAssetAtPath<SmartMaterialImportInfo>(((SmartMaterialImporter)target).assetPath);
            if (info == null) return;
            if (!string.IsNullOrEmpty(info.error)) { EditorGUILayout.HelpBox(info.error, MessageType.Error); return; }
            EditorGUILayout.LabelField(L.Tr("Name"), info.displayName);
            EditorGUILayout.LabelField(L.Tr("Kind"), info.mask ? L.Tr("Smart mask") : L.Tr("Smart Material"));
            EditorGUILayout.LabelField(L.Tr("Size"), info.width + " × " + info.height);
            EditorGUILayout.LabelField(L.Tr("Layers"), info.layerCount.ToString());
            EditorGUILayout.LabelField(L.Tr("Channels"), info.channels);
            EditorGUILayout.LabelField(L.Tr("File Format"), info.format.ToString());
            EditorGUILayout.LabelField(L.Tr("Saved By"), info.savedBy);
            if (info.thumbnail != null) GUILayout.Label(info.thumbnail, GUILayout.Width(128), GUILayout.Height(128));
        }
    }

    [CustomEditor(typeof(SmartMaterialImportInfo))]
    internal sealed class SmartMaterialImportInfoEditor : UnityEditor.Editor
    {
        public override bool HasPreviewGUI() => ((SmartMaterialImportInfo)target).thumbnail != null;
        public override void OnPreviewGUI(Rect r, GUIStyle background) => EditorGUI.DrawTextureTransparent(r, ((SmartMaterialImportInfo)target).thumbnail, ScaleMode.ScaleToFit);
        public override Texture2D RenderStaticPreview(string assetPath, UnityEngine.Object[] subAssets, int width, int height)
        {
            var source = ((SmartMaterialImportInfo)target).thumbnail;
            if (source == null) return null;
            var result = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) pixels[y * width + x] = source.GetPixelBilinear((x + .5f) / width, (y + .5f) / height);
            result.SetPixels32(pixels); result.Apply(); return result;
        }
    }
}
