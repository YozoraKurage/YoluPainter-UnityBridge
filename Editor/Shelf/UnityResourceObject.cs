using System;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>GUID と localFileID で Unity のファイル内のオブジェクトを特定する。旧参照は主アセットだけ。</summary>
    internal static class UnityResourceObject
    {
        public static long LocalId(UnityEngine.Object value)
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string _, out long id))
                throw new ArgumentException("The object has no persistent Unity asset identifier.");
            return id;
        }
        public static string Key(UnityEngine.Object value) => AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(value)) + (AssetDatabase.IsMainAsset(value) ? "" : ":" + LocalId(value).ToString(CultureInfo.InvariantCulture));
        public static T Load<T>(string guid, long localId) where T : UnityEngine.Object
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return null;
            return localId == 0 ? AssetDatabase.LoadMainAssetAtPath(path) as T
                : AssetDatabase.LoadAllAssetsAtPath(path).OfType<T>().FirstOrDefault(o => LocalId(o) == localId);
        }
        public static T LoadKey<T>(string key) where T : UnityEngine.Object
        {
            var parts = (key ?? "").Split(':');
            if (parts.Length == 1) return Load<T>(parts[0], 0);
            return parts.Length == 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) ? Load<T>(parts[0], id) : null;
        }
    }
}
