using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// 書き出しの返事（<c>exported</c>）の取り込み: 書いた PNG を取り込み、TextureImporter の sRGB とノーマルマップを返事のとおりにする
    /// （<c>Assets</c> の外の道は取り込まず、理由を出す）。それから、変える組（マテリアル・プロパティ・前のテクスチャ → 新しいテクスチャ）の一覧を
    /// 作り、当てるかを利用者に確かめる（<see cref="LiveLinkApplyWindow"/>）。当てるときは 1 つの取り消しの段（<see cref="Undo.RecordObject"/>）で、
    /// 当てた後にアセットを保存する。マテリアルに当てるのは確かめた後だけ。
    /// </summary>
    internal static class LiveLinkImport
    {
        /// <summary>変える組 1 つ。<see cref="Problem"/> があれば当てられない（理由の言葉）。</summary>
        internal sealed class Row
        {
            public string MaterialKey, Property, NewPath;
            public Material Material;
            public Texture Old;
            public Texture2D Texture;
            public string Problem;
            public bool Selected = true;
            public bool CanApply => Problem == null && Material != null && Texture != null;
        }

        internal sealed class Prepared
        {
            public string Request = "", TargetName = "";
            public readonly List<Row> Rows = new List<Row>();
            /// <summary>当てると何かが変わる組。</summary>
            public IEnumerable<Row> Changes => Rows.Where(r => r.CanApply && r.Old != r.Texture);
        }

        /// <summary>確かめる窓を出す口（試験は差し替える）。変わる組が無ければ出さない。</summary>
        internal static Action<Prepared> Presenter = prepared => LiveLinkApplyWindow.Show(prepared);

        public static void Present(Prepared prepared)
        {
            if (prepared != null && prepared.Changes.Any()) Presenter(prepared);
        }

        /// <summary>PNG を取り込み、組の一覧を作る（マテリアルには書かない）。</summary>
        public static Prepared Prepare(LiveLinkReply reply)
        {
            var entry = LiveLinkLedger.Find(reply.Request);
            var result = new Prepared { Request = reply.Request, TargetName = entry?.TargetName ?? "" };
            // instance: の鍵は、送ったときと同じエディターの間だけ引ける（InstanceID はエディターを開き直すと別の物を指す）
            bool sameSession = entry != null && entry.Value.SentUtc >= EditorStartUtc;
            foreach (var f in reply.Files)
            {
                var row = new Row { MaterialKey = f.Material, Property = f.Property, NewPath = f.Path };
                result.Rows.Add(row);
                string assetPath = AssetPathOf(f.Path);
                if (assetPath == null) { row.Problem = LiveLinkReason.OutsideAssets; continue; }
                row.Texture = ImportTexture(assetPath, f.Srgb, f.NormalMap);
                if (row.Texture == null) { row.Problem = LiveLinkReason.NotImported; continue; }
                row.Material = FindMaterial(f.Material, sameSession);
                if (row.Material == null) { row.Problem = LiveLinkReason.MaterialNotFound; continue; }
                if (!Writable(row.Material)) { row.Problem = LiveLinkReason.MaterialReadOnly; continue; }
                if (string.IsNullOrEmpty(f.Property) || !row.Material.HasTexture(f.Property)) { row.Problem = LiveLinkReason.NoSuchProperty; continue; }
                row.Old = row.Material.GetTexture(f.Property);
            }
            return result;
        }

        /// <summary>絶対の道を、このプロジェクトの <c>Assets/…</c> の道にする（Assets の外なら null）。</summary>
        public static string AssetPathOf(string absolute) => AssetPathOf(absolute, Application.dataPath, Application.platform == RuntimePlatform.WindowsEditor);

        public static string AssetPathOf(string absolute, string dataPath, bool ignoreCase)
        {
            if (string.IsNullOrEmpty(absolute)) return null;
            string full, assets;
            try { full = LiveLinkSettings.Slash(Path.GetFullPath(absolute)); assets = LiveLinkSettings.Slash(Path.GetFullPath(dataPath)).TrimEnd('/'); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return null; }
            var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.StartsWith(assets + "/", cmp)) return null;
            string rest = full.Substring(assets.Length + 1);
            if (rest.Length == 0 || rest.Split('/').Any(part => part == ".." || part.Length == 0)) return null;
            return "Assets/" + rest;
        }

        /// <summary>取り込み、sRGB とノーマルマップを返事のとおりにする（違うときだけ取り込み直す）。読めなければ null。</summary>
        static Texture2D ImportTexture(string assetPath, bool srgb, bool normalMap)
        {
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", assetPath))) return null;
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            if (AssetImporter.GetAtPath(assetPath) is TextureImporter ti)
            {
                bool changed = false;
                var type = normalMap ? TextureImporterType.NormalMap : (ti.textureType == TextureImporterType.NormalMap ? TextureImporterType.Default : ti.textureType);
                if (ti.textureType != type) { ti.textureType = type; changed = true; }
                if (!normalMap && ti.sRGBTexture != srgb) { ti.sRGBTexture = srgb; changed = true; }
                if (changed) ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        /// <summary>このエディターが始まった時刻（UTC）。</summary>
        static DateTime EditorStartUtc => DateTime.UtcNow - TimeSpan.FromSeconds(EditorApplication.timeSinceStartup);

        /// <summary>頼みのマテリアルの鍵からマテリアル（無ければ null）。<c>instance:</c> の鍵は、頼みを送ったのと同じエディターの間
        /// （<paramref name="sameSession"/>）だけ引く。</summary>
        public static Material FindMaterial(string key, bool sameSession = true)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (key.StartsWith("guid:", StringComparison.Ordinal))
            {
                int slash = key.IndexOf("/fileid:", StringComparison.Ordinal);
                if (slash < 0) return null;
                string guid = key.Substring(5, slash - 5);
                if (!long.TryParse(key.Substring(slash + 8), NumberStyles.Integer, CultureInfo.InvariantCulture, out long fileId)) return null;
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) return null;
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                    if (o is Material m && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(m, out string g, out long id) && g == guid && id == fileId) return m;
                return null;
            }
            if (key.StartsWith("object:", StringComparison.Ordinal) && GlobalObjectId.TryParse(key.Substring(7), out var gid))
                return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid) as Material;
            if (sameSession && key.StartsWith("instance:", StringComparison.Ordinal) && int.TryParse(key.Substring(9), NumberStyles.Integer, CultureInfo.InvariantCulture, out int instanceId))
                return EditorUtility.InstanceIDToObject(instanceId) as Material;
            return null;
        }

        /// <summary>書き換えて残せるマテリアルか（シーンの中のマテリアル、または Assets の下の .mat。FBX の中・パッケージの中は残せない）。</summary>
        public static bool Writable(Material m)
        {
            if (m == null) return false;
            if (!EditorUtility.IsPersistent(m)) return true;
            string path = AssetDatabase.GetAssetPath(m);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal)) return false;
            if (AssetImporter.GetAtPath(path) is ModelImporter) return false;
            return AssetDatabase.IsOpenForEdit(m, StatusQueryOptions.UseCachedIfPossible);
        }

        /// <summary>選んだ組を当てる（1 つの取り消しの段。当てた数を返す）。</summary>
        public static int Apply(IEnumerable<Row> rows)
        {
            var list = rows.Where(r => r.CanApply && r.Old != r.Texture).ToList();
            if (list.Count == 0) return 0;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(L.Tr("Apply YoluPainter textures"));
            foreach (var m in list.Select(r => r.Material).Distinct()) Undo.RecordObject(m, L.Tr("Apply YoluPainter textures"));
            foreach (var r in list)
            {
                r.Material.SetTexture(r.Property, r.Texture);
                EditorUtility.SetDirty(r.Material);
            }
            Undo.CollapseUndoOperations(group);
            AssetDatabase.SaveAssets();
            return list.Count;
        }
    }
}
