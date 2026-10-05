using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// ドメインのリロード（スクリプトの再コンパイル）と Play への出入りのあとの、自動のつなぎ直し。つないでモデルを見せていたとき、切る前に
    /// つなぎ先の名前とモデルの根の場所を SessionState に覚え（<see cref="Remember"/>）、リロードのあとに <see cref="TryResume"/> が
    /// つなぎ直してモデルを送り直す。起動はしない（スタンドアロンが閉じられていれば、少し待って静かにやめる）。覚えは 1 回で消す
    /// （失敗してもまた試さない）。利用者が切ったとき・エディタを終えるときも消す。SessionState はエディタを終えると消える。
    /// モデルの根の場所は、保存したシーンなら GlobalObjectId（Play のシーンでも同じ物を指す）、保存していないシーンやそれで見つからないときは
    /// シーンの中の子の番号の道。
    /// </summary>
    internal static class LiveLinkResume
    {
        public const string Key = "Yozolab.YoluPainter.LiveLink.Resume";

        [Serializable]
        sealed class Saved
        {
            public string linkName;
            public string globalId;
            public string scenePath;
            public string sceneName;
            public string hierarchy;
        }

        public static bool Pending => !string.IsNullOrEmpty(SessionState.GetString(Key, ""));

        /// <summary>つないでモデルを見せているなら覚える。そうでなければ覚えを消す。</summary>
        public static void Remember(LiveLinkSession session)
        {
            var root = session != null && session.Status == LiveLinkStatus.Connected && session.Model != null ? session.Model.Root : null;
            if (root == null || EditorUtility.IsPersistent(root) || !root.scene.IsValid()) { Forget(); return; }
            var saved = new Saved { linkName = session.LinkName, scenePath = root.scene.path ?? "", sceneName = root.scene.name ?? "", hierarchy = HierarchyOf(root.transform) };
            if (!string.IsNullOrEmpty(saved.scenePath)) saved.globalId = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();
            SessionState.SetString(Key, JsonUtility.ToJson(saved));
        }

        public static void Forget() => SessionState.EraseString(Key);

        /// <summary>覚えがあればつなぎ直す（起動しない）。始めた流れを返す（覚えが無い・モデルが見つからない・ブリッジが使えないときは null）。</summary>
        public static LiveLinkOpen TryResume(LiveLinkOpenOptions options = null, bool autoTick = true)
        {
            string json = SessionState.GetString(Key, "");
            if (string.IsNullOrEmpty(json)) return null;
            Forget(); // 1 回だけ。失敗しても次の切り方で書き直されるまで試さない
            if (LiveLinkSession.Active != null) return null;
            Saved saved;
            try { saved = JsonUtility.FromJson<Saved>(json); } catch (ArgumentException) { return null; }
            var root = saved == null ? null : Find(saved);
            if (root == null || !LiveLinkModel.HasSendableRenderer(root) || !LiveLinkBridge.Available) return null;
            options = options ?? new LiveLinkOpenOptions();
            options.LinkName = string.IsNullOrEmpty(saved.linkName) ? LiveLinkSession.DefaultLinkName : saved.linkName;
            options.MayStart = false;
            options.Progress = null;
            return LiveLinkOpen.Begin(root, options, autoTick);
        }

        // ───────── モデルの根の場所 ─────────

        /// <summary>シーンの根から、子の番号をつないだ道（"2/0/5"）。</summary>
        static string HierarchyOf(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Add(x.GetSiblingIndex().ToString(CultureInfo.InvariantCulture));
            parts.Reverse();
            return string.Join("/", parts);
        }

        static GameObject Find(Saved saved)
        {
            bool savedScene = !string.IsNullOrEmpty(saved.scenePath);
            // 保存したシーンは GlobalObjectId が確か（Play のシーンも同じ物を指す）。保存していないシーンは、番号が決まらないので使わない
            if (savedScene && !string.IsNullOrEmpty(saved.globalId) && GlobalObjectId.TryParse(saved.globalId, out var id))
            {
                var found = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id);
                var go = found is Component c ? c.gameObject : found as GameObject;
                if (go != null && go.scene.IsValid() && !EditorUtility.IsPersistent(go)) return go;
            }
            return FindByPath(saved);
        }

        static GameObject FindByPath(Saved saved)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                if (!(!string.IsNullOrEmpty(saved.scenePath) ? scene.path == saved.scenePath : string.IsNullOrEmpty(scene.path) && scene.name == saved.sceneName)) continue;
                var parts = saved.hierarchy?.Split('/');
                if (parts == null || parts.Length == 0) continue;
                var roots = scene.GetRootGameObjects();
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rootIndex) || rootIndex < 0) continue;
                // GetRootGameObjects は子の番号の順
                if (rootIndex >= roots.Length) continue;
                var t = roots[rootIndex].transform;
                for (int p = 1; p < parts.Length && t != null; p++)
                    t = int.TryParse(parts[p], NumberStyles.Integer, CultureInfo.InvariantCulture, out int child) && child >= 0 && child < t.childCount ? t.GetChild(child) : null;
                if (t != null) return t.gameObject;
            }
            return null;
        }
    }
}
