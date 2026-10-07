using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// シーンに置いた FBX のモデル 1 つ（FBX のアセットと、その根に当たるシーンの Transform の組）と、FBX の中のノード ⇔ シーンの Transform の対応。
    /// スタンドアロンは FBX を自分で読み、ノードを「Unity が取り込んだモデルの根からの名前の道」で引くので、ここではその道と、シーンの今の形から
    /// 計算したローカル（FBX の親のノードに当たる Transform に対する値）を決める。
    /// 対応は 3 段で決める: (1) <see cref="PrefabUtility.GetCorrespondingObjectFromOriginalSource{T}"/>（シーンの Transform → FBX のアセットの中の Transform）、
    /// (2) プレハブを展開した・元のプレハブにしたスキンは、SkinnedMeshRenderer の bones の並び（FBX のアセットの同じメッシュのレンダラーの bones と同じ番号）、
    /// (3) 残りは、FBX の親のノードに当たる Transform の子から名前で探す（根から順に。同じ名前の兄弟がいれば決めない）。(2) があるので、展開して
    /// ボーンの親を付け替えた階層（衣装のボーンを体のボーンの下へ移した等）でも、ボーンは FBX のノードに当たる。読むだけで、シーン・アセットには書かない。
    /// </summary>
    internal sealed class LiveLinkFbxMap
    {
        /// <summary>FBX のアセットの道（Assets/… か Packages/…）。</summary>
        public readonly string AssetPath;
        /// <summary>FBX のモデルのアセットの根（取り込んだプレハブの根）。</summary>
        public readonly Transform AssetRoot;
        /// <summary>FBX の根に当たるシーンの Transform。</summary>
        public readonly Transform SceneRoot;

        readonly Dictionary<Transform, Transform> assetToScene = new Dictionary<Transform, Transform>();
        readonly Dictionary<Transform, Transform> sceneToAsset = new Dictionary<Transform, Transform>();
        readonly Dictionary<Transform, string> assetPaths = new Dictionary<Transform, string>();
        /// <summary>FBX の中で道が 1 つに決まらないノード（自分か先祖に同じ名前の兄弟がいる）。スタンドアロンが名前の道で引けない。</summary>
        readonly HashSet<Transform> twinsInFbx = new HashSet<Transform>();
        readonly List<Transform> assetNodes = new List<Transform>();

        public IReadOnlyList<Transform> AssetNodes => assetNodes;

        LiveLinkFbxMap(string assetPath, Transform assetRoot, Transform sceneRoot)
        {
            AssetPath = assetPath; AssetRoot = assetRoot; SceneRoot = sceneRoot;
            assetRoot.GetComponentsInChildren(true, assetNodes);
            foreach (var n in assetNodes)
            {
                assetPaths[n] = PathFrom(assetRoot, n);
                if (HasTwinOnTheWay(assetRoot, n)) twinsInFbx.Add(n);
            }
            MapByCorrespondence();
            MapByBones();
            MapByName();
        }

        /// <summary>FBX のノードの道（根は空）。</summary>
        public string PathOf(Transform assetNode) => assetPaths.TryGetValue(assetNode, out var p) ? p : null;

        /// <summary>FBX のノードに当たるシーンの Transform（無ければ null）。</summary>
        public Transform SceneOf(Transform assetNode) => assetNode != null && assetToScene.TryGetValue(assetNode, out var s) ? s : null;

        /// <summary>シーンの Transform に当たる FBX のノード（無ければ null）。</summary>
        public Transform AssetOf(Transform scene) => scene != null && sceneToAsset.TryGetValue(scene, out var a) ? a : null;

        /// <summary>FBX のノードを、スタンドアロンが名前の道で 1 つに引けるか。</summary>
        public bool Addressable(Transform assetNode) => assetPaths.ContainsKey(assetNode) && !twinsInFbx.Contains(assetNode);

        // ───────── 見つける ─────────

        /// <summary>レンダラーのメッシュが来た FBX のモデルを、シーンの中で探す。見つからなければ理由（<see cref="LiveLinkReason"/> の言葉）。</summary>
        public static LiveLinkFbxMap Find(Renderer renderer, Mesh mesh, string assetPath, Dictionary<(string, Transform), LiveLinkFbxMap> cache, out string reason)
        {
            reason = null;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (model == null) { reason = LiveLinkReason.MeshNotFromFbx; return null; }
            var assetRoot = model.transform;
            var sceneRoot = SceneRootByCorrespondence(renderer.transform, assetRoot) ?? SceneRootByName(renderer.transform, assetRoot, mesh, out reason);
            if (sceneRoot == null) { reason = reason ?? LiveLinkReason.BoneNotFound; return null; }
            var key = (assetPath, sceneRoot);
            if (!cache.TryGetValue(key, out var map)) cache[key] = map = new LiveLinkFbxMap(assetPath, assetRoot, sceneRoot);
            return map;
        }

        static Transform SceneRootByCorrespondence(Transform from, Transform assetRoot)
        {
            for (var t = from; t != null; t = t.parent)
                if (PrefabUtility.GetCorrespondingObjectFromOriginalSource(t) == assetRoot) return t;
            return null;
        }

        /// <summary>名前の道で: FBX の中で同じメッシュを持つノードの道を、レンダラーから上へたどって名前が合う先祖を根とする。</summary>
        static Transform SceneRootByName(Transform from, Transform assetRoot, Mesh mesh, out string reason)
        {
            reason = null;
            var found = new HashSet<Transform>();
            foreach (var r in assetRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (MeshOf(r) != mesh) continue;
                var root = AncestorByNames(from, assetRoot, r.transform);
                if (root != null) found.Add(root);
            }
            if (found.Count == 1) { foreach (var t in found) return t; }
            reason = found.Count > 1 ? LiveLinkReason.AmbiguousBone : LiveLinkReason.BoneNotFound;
            return null;
        }

        /// <summary><paramref name="scene"/> から、<paramref name="assetNode"/> の FBX の根からの深さだけ上へたどり、途中の名前が FBX と同じなら、
        /// その先祖（FBX の根に当たる）を返す。</summary>
        static Transform AncestorByNames(Transform scene, Transform assetRoot, Transform assetNode)
        {
            var s = scene; var a = assetNode;
            while (a != assetRoot)
            {
                if (s == null || a == null || s.name != a.name) return null;
                s = s.parent; a = a.parent;
            }
            return s;
        }

        internal static Mesh MeshOf(Renderer r) =>
            r is SkinnedMeshRenderer s ? s.sharedMesh : r != null && r.TryGetComponent<MeshFilter>(out var f) ? f.sharedMesh : null;

        // ───────── 対応 ─────────

        void Map(Transform asset, Transform scene)
        {
            assetToScene[asset] = scene;
            sceneToAsset[scene] = asset;
        }

        void MapByCorrespondence()
        {
            var claimedTwice = new HashSet<Transform>();
            foreach (var t in SceneRoot.GetComponentsInChildren<Transform>(true))
            {
                var src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(t);
                if (src == null || !assetPaths.ContainsKey(src)) continue;
                // 下に同じ FBX をもう 1 つ置いてあれば、その中の物はそちらの組
                if (SceneRootByCorrespondence(t, AssetRoot) != SceneRoot) continue;
                if (assetToScene.ContainsKey(src)) { claimedTwice.Add(src); continue; }
                Map(src, t);
            }
            // 同じノードに当たる Transform が 2 つあれば、どちらとも決めない
            foreach (var src in claimedTwice)
            {
                if (assetToScene.TryGetValue(src, out var scene)) sceneToAsset.Remove(scene);
                assetToScene.Remove(src);
            }
        }

        /// <summary>スキンのボーンの並びで: 根の下の、この FBX のメッシュの SkinnedMeshRenderer ごとに、FBX のアセットの中で同じメッシュを持つ
        /// SkinnedMeshRenderer（1 つに決まるときだけ）の bones と番号で結ぶ。どちらかがもう別の物に結ばれていれば結ばない。</summary>
        void MapByBones()
        {
            var assetSkins = AssetRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var skin in SceneRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = skin.sharedMesh;
                if (mesh == null || AssetDatabase.GetAssetPath(mesh) != AssetPath) continue;
                var nearest = SceneRootByCorrespondence(skin.transform, AssetRoot);
                if (nearest != null && nearest != SceneRoot) continue; // 下に置いた同じ FBX のもの
                SkinnedMeshRenderer source = null; int found = 0;
                foreach (var a in assetSkins) if (a.sharedMesh == mesh) { source = a; found++; }
                if (found != 1) continue;
                var sceneBones = skin.bones; var assetBones = source.bones;
                if (sceneBones.Length != assetBones.Length) continue;
                for (int i = 0; i < sceneBones.Length; i++)
                {
                    var scene = sceneBones[i]; var asset = assetBones[i];
                    if (scene == null || asset == null || !assetPaths.ContainsKey(asset)) continue;
                    if (assetToScene.ContainsKey(asset) || sceneToAsset.ContainsKey(scene)) continue;
                    Map(asset, scene);
                }
            }
        }

        /// <summary>名前で: FBX の根から順に、親に当たる Transform の子のうち同じ名前のもの（1 つだけのとき）。</summary>
        void MapByName()
        {
            if (!assetToScene.ContainsKey(AssetRoot) && !sceneToAsset.ContainsKey(SceneRoot)) Map(AssetRoot, SceneRoot);
            foreach (var n in assetNodes)
            {
                if (n == AssetRoot || assetToScene.ContainsKey(n)) continue;
                var parent = SceneOf(n.parent);
                if (parent == null) continue;
                var s = UniqueChild(parent, n.name);
                if (s == null || sceneToAsset.ContainsKey(s)) continue;
                Map(n, s);
            }
        }

        /// <summary>その名前の子が 1 つだけならそれ（無い・2 つ以上なら null）。</summary>
        static Transform UniqueChild(Transform parent, string name)
        {
            Transform found = null;
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i);
                if (c.name != name) continue;
                if (found != null) return null;
                found = c;
            }
            return found;
        }

        /// <summary>シーンの Transform を FBX のノードにできなかった理由（同じ名前の兄弟がいれば <see cref="LiveLinkReason.AmbiguousBone"/>）。</summary>
        public string WhyNotMapped(Transform scene)
        {
            if (scene == null) return LiveLinkReason.BoneNotFound;
            var asset = AssetOf(scene);
            if (asset != null) return Addressable(asset) ? null : LiveLinkReason.AmbiguousBone;
            // 名前で探せなかった: 親に当たる Transform の下に同じ名前の兄弟がいたなら、1 つに決まらなかった
            var parentAsset = AssetOf(scene.parent);
            if (parentAsset != null && UniqueChild(scene.parent, scene.name) == null) return LiveLinkReason.AmbiguousBone;
            return LiveLinkReason.BoneNotFound;
        }

        // ───────── 道 ─────────

        /// <summary>根から <paramref name="t"/> までの名前を / でつなぐ（根そのものは空。根の下でなければ null）。</summary>
        public static string PathFrom(Transform root, Transform t)
        {
            var names = new List<string>();
            for (var x = t; x != root; x = x.parent)
            {
                if (x == null) return null;
                names.Add(x.name);
            }
            names.Reverse();
            return string.Join("/", names);
        }

        public static bool IsUnder(Transform t, Transform root)
        {
            for (var x = t; x != null; x = x.parent) if (x == root) return true;
            return false;
        }

        /// <summary><paramref name="t"/> から <paramref name="root"/> の手前までの、どこかに同じ名前の兄弟がいるか。</summary>
        static bool HasTwinOnTheWay(Transform root, Transform t)
        {
            for (var x = t; x != null && x != root; x = x.parent)
            {
                var p = x.parent;
                if (p == null) return false;
                for (int i = 0; i < p.childCount; i++)
                {
                    var c = p.GetChild(i);
                    if (c != x && c.name == x.name) return true;
                }
            }
            return false;
        }

        // ───────── ローカル ─────────

        /// <summary>ローカル（T・R・S）。</summary>
        internal struct Local
        {
            public Vector3 T; public Quaternion R; public Vector3 S;
            public bool Finite => Ok(T.x) && Ok(T.y) && Ok(T.z) && Ok(R.x) && Ok(R.y) && Ok(R.z) && Ok(R.w) && Ok(S.x) && Ok(S.y) && Ok(S.z);
            static bool Ok(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        }

        /// <summary><paramref name="parent"/> に対する <paramref name="t"/> のローカル。親子なら Transform の値をそのまま（分解の誤差が無い）、
        /// そうでなければ <c>parent.worldToLocalMatrix * t.localToWorldMatrix</c> を T・R・S に分ける。<paramref name="parent"/> と <paramref name="t"/> が同じなら単位。</summary>
        public static Local Relative(Transform parent, Transform t)
        {
            if (parent == t) return new Local { T = Vector3.zero, R = Quaternion.identity, S = Vector3.one };
            if (t.parent == parent) return new Local { T = t.localPosition, R = t.localRotation, S = t.localScale };
            var m = parent.worldToLocalMatrix * t.localToWorldMatrix;
            return Decompose(m);
        }

        /// <summary>行列を T・R・S に分ける（せん断は捨てる。負の拡大は行列式の符号を X に寄せる）。</summary>
        public static Local Decompose(Matrix4x4 m)
        {
            Vector3 x = m.GetColumn(0), y = m.GetColumn(1), z = m.GetColumn(2);
            var s = new Vector3(x.magnitude, y.magnitude, z.magnitude);
            if (Vector3.Dot(Vector3.Cross(x, y), z) < 0) { s.x = -s.x; x = -x; }
            var r = s.y > 0 && s.z > 0 ? Quaternion.LookRotation(z / s.z, y / s.y) : Quaternion.identity;
            return new Local { T = m.GetColumn(3), R = Normalize(r), S = s };
        }

        static Quaternion Normalize(Quaternion q)
        {
            float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return n > 1e-12f ? new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n) : Quaternion.identity;
        }

        /// <summary>FBX のノード <paramref name="node"/> のローカル: FBX の親のノードに当たる Transform に対する値（FBX の根は <paramref name="target"/> に対する値）。
        /// 親に当たる Transform が無ければ null。</summary>
        public Local? LocalOf(Transform node, Transform target)
        {
            var scene = SceneOf(node);
            if (scene == null) return null;
            Transform parent = node == AssetRoot ? target : SceneOf(node.parent);
            if (parent == null) return null;
            var local = Relative(parent, scene);
            return local.Finite ? local : (Local?)null;
        }

        /// <summary>アセットの道を、OS のファイルの絶対の道（区切りは /）にする（パッケージの中も実際の場所に）。</summary>
        public static string PhysicalPath(string assetPath)
        {
            string physical = FileUtil.GetPhysicalPath(assetPath);
            if (string.IsNullOrEmpty(physical)) physical = assetPath;
            return LiveLinkSettings.Slash(Path.GetFullPath(physical));
        }
    }
}
