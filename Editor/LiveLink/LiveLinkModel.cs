using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// Live Link で送るシーンのモデル: 根のゲームオブジェクトの下の、有効な MeshRenderer と SkinnedMeshRenderer。メッシュは読むだけ
    /// （読めないメッシュもエディタの API で読む。インポート設定は変えない）、スキンメッシュは今のポーズを自前のメッシュへ焼いて読む。
    /// ゲームオブジェクトを複製せず、シーン・マテリアル・メッシュには書かない。位置は根のローカルの空間、三角形の巻きは鏡に映した
    /// レンダラーでも表を同じ向きにそろえる。
    /// マテリアルの組（同じマテリアルを使うサブメッシュ。マテリアルの無いサブメッシュは 1 つの組）ごとに、YoluPainter の流し込みの決まり
    /// （<see cref="PreviewMaterialBindings"/>）で、Live Link が MaterialPropertyBlock で見せられるチャンネルを決める（キーワードが要る
    /// チャンネルは、マテリアルでそのキーワードが入っているときだけ。PropertyBlock ではキーワードを変えられない）。
    /// </summary>
    internal sealed class LiveLinkModel : IDisposable
    {
        internal sealed class MaterialEntry
        {
            /// <summary>元のマテリアル（参照だけ）。マテリアルの無いサブメッシュの組は null。</summary>
            public Material Material;
            public PreviewMaterialBinding Binding;
            /// <summary>Live Link で見せるチャンネル（流し込みの決まりのうち、PropertyBlock で効くもの）。</summary>
            public readonly List<PreviewChannelBinding> Shown = new List<PreviewChannelBinding>();
            public readonly List<string> Notes = new List<string>();
            public string Name => Material != null ? Material.name : PreviewMaterialGroup.UnassignedName;
        }

        internal sealed class MeshEntry
        {
            public Renderer Renderer; public bool Skinned; public string Key, Name;
            /// <summary>サブメッシュ → モデルのマテリアルの番号。</summary>
            public int[] SubmeshMaterials;
            public int VertexCount;
            public ulong PoseHash;
        }

        public GameObject Root { get; private set; }
        public readonly List<MaterialEntry> Materials = new List<MaterialEntry>();
        public readonly List<MeshEntry> Meshes = new List<MeshEntry>();
        /// <summary>写せなかったもの・見せられないチャンネルなどの知らせ。</summary>
        public readonly List<string> Notes = new List<string>();
        /// <summary>送ったモデルの世代（ylb_model_send の返した値。送る前は 0）。</summary>
        public int Generation { get; private set; }
        public ulong StructureHash { get; private set; }
        /// <summary>送ったマテリアルの情報（シェーダー・キーワード・テクスチャのプロパティ）の値（ComputeMaterialHash。変われば、マテリアルの更新だけを送る）。</summary>
        public ulong MaterialHash { get; private set; }
        public int VertexTotal => Meshes.Sum(m => m.VertexCount);
        Mesh baked;

        LiveLinkModel() { }

        static bool IsActive(Renderer r) => r != null && r.enabled && r.gameObject.activeInHierarchy;

        static Mesh MeshOf(Renderer r) => r is SkinnedMeshRenderer s ? s.sharedMesh : r.TryGetComponent<MeshFilter>(out var f) ? f.sharedMesh : null;

        /// <summary>根の下のモデルを写す（まだ送らない）。</summary>
        public static LiveLinkModel Capture(GameObject root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            var model = new LiveLinkModel { Root = root };
            var byMaterial = new Dictionary<Material, int>(); int unassigned = -1;
            int MaterialIndex(Material m)
            {
                if (m == null) { if (unassigned < 0) { unassigned = model.Materials.Count; model.Materials.Add(new MaterialEntry()); } return unassigned; }
                if (byMaterial.TryGetValue(m, out int i)) return i;
                i = model.Materials.Count; byMaterial.Add(m, i); model.Materials.Add(new MaterialEntry { Material = m });
                return i;
            }
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!IsActive(r)) continue;
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) { model.Notes.Add(L.Tr("{0}: {1} is not sent (only mesh and skinned mesh renderers).", r.name, r.GetType().Name)); continue; }
                var mesh = MeshOf(r);
                if (mesh == null) continue;
                bool triangles = true;
                for (int s = 0; s < mesh.subMeshCount; s++) if (mesh.GetTopology(s) != MeshTopology.Triangles) triangles = false;
                if (!triangles) { model.Notes.Add(L.Tr("{0}: the mesh has non-triangle submeshes and is not sent.", r.name)); continue; }
                var mats = r.sharedMaterials;
                var subs = new int[mesh.subMeshCount];
                for (int s = 0; s < subs.Length; s++) subs[s] = MaterialIndex(s < mats.Length ? mats[s] : null);
                model.Meshes.Add(new MeshEntry { Renderer = r, Skinned = r is SkinnedMeshRenderer, Key = KeyOf(root.transform, r.transform), Name = r.name, SubmeshMaterials = subs, VertexCount = mesh.vertexCount });
            }
            foreach (var m in model.Materials) model.Resolve(m);
            model.StructureHash = model.ComputeStructureHash();
            model.MaterialHash = model.ComputeMaterialHash();
            return model;
        }

        /// <summary>根からの子の番号の道（名前が重なっても違う。根そのものは "."）。</summary>
        static string KeyOf(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) parts.Add(x.GetSiblingIndex().ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (parts.Count == 0) return ".";
            parts.Reverse();
            return string.Join("/", parts);
        }

        void Resolve(MaterialEntry e)
        {
            e.Shown.Clear(); e.Notes.Clear(); e.Binding = null;
            if (e.Material == null) { e.Notes.Add(L.Tr("These submeshes have no material, so painting cannot be shown on them.")); return; }
            e.Binding = PreviewMaterialBindings.Resolve(e.Material);
            if (!e.Binding.CanShow) { e.Notes.Add(e.Binding.Unusable); return; }
            e.Notes.AddRange(e.Binding.Remarks);
            foreach (var c in e.Binding.Channels)
            {
                var off = c.Keywords.Where(k => !e.Material.IsKeywordEnabled(k)).ToList();
                if (off.Count > 0) { e.Notes.Add(L.Tr("{0}: {1} is not shown (keyword {2} is off).", L.Tr(c.Channel.ToString()), c.Property, string.Join(", ", off))); continue; }
                e.Shown.Add(c);
            }
        }

        /// <summary>送ったモデルと今のシーンで、レンダラー・メッシュ・サブメッシュのマテリアルの組み合わせが違えば変わる値（モデルを送り直す合図）。
        /// マテリアルの中身（シェーダー・キーワード・テクスチャ）の変化は <see cref="ComputeMaterialHash"/>（マテリアルの更新だけで済む）。</summary>
        public ulong ComputeStructureHash()
        {
            var h = new Hasher();
            if (Root == null) return 0;
            foreach (var r in Root.GetComponentsInChildren<Renderer>(true))
            {
                if (!IsActive(r) || !(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                h.Add(r.GetInstanceID());
                var mesh = MeshOf(r);
                h.Add(mesh != null ? mesh.GetInstanceID() : 0); h.Add(mesh != null ? mesh.vertexCount : 0); h.Add(mesh != null ? mesh.subMeshCount : 0);
                foreach (var m in r.sharedMaterials) h.Add(m != null ? m.GetInstanceID() : 0);
            }
            return h.Value;
        }

        /// <summary>シェーダーごとの 2D テクスチャのプロパティの名前（ハッシュを 0.2 秒ごとに取るので、プロパティの多い lilToon で毎回数えない。マテリアルの更新を送るまで使う）。</summary>
        readonly Dictionary<int, List<string>> texturePropertyCache = new Dictionary<int, List<string>>();

        List<string> CachedTextureProperties(Shader shader)
        {
            int id = shader.GetInstanceID();
            if (!texturePropertyCache.TryGetValue(id, out var list)) texturePropertyCache[id] = list = PreviewMaterialBindings.TextureProperties(shader);
            return list;
        }

        /// <summary>モデルのマテリアルごとの、シェーダー・有効なキーワード・2D テクスチャのプロパティ（入っているテクスチャとその大きさ）の値。</summary>
        public ulong ComputeMaterialHash()
        {
            var h = new Hasher();
            foreach (var e in Materials)
            {
                var m = e.Material;
                h.Add(m != null ? m.GetInstanceID() : 0);
                if (m == null) continue;
                var shader = m.shader;
                h.Add(shader != null ? shader.GetInstanceID() : 0);
                // キーワードの並びは当てにしない（足し算で混ぜる）
                int sum = 0, count = 0;
                foreach (var k in m.shaderKeywords) { sum += k.GetHashCode(); count++; }
                h.Add(sum); h.Add(count);
                if (shader == null) continue;
                foreach (var property in CachedTextureProperties(shader))
                {
                    var t = m.GetTexture(property);
                    h.Add(property.GetHashCode()); h.Add(t != null ? t.GetInstanceID() : 0); h.Add(t != null ? t.width : 0); h.Add(t != null ? t.height : 0);
                }
            }
            return h.Value;
        }

        /// <summary>メッシュの形が変わり得るものの値（根から見た骨・レンダラーの行列と BlendShape の重み）。</summary>
        ulong PoseHashOf(MeshEntry e)
        {
            var h = new Hasher();
            if (e.Renderer == null || Root == null) return 0;
            var toRoot = Root.transform.worldToLocalMatrix;
            h.Add(toRoot * e.Renderer.transform.localToWorldMatrix);
            if (e.Renderer is SkinnedMeshRenderer s)
            {
                foreach (var b in s.bones) if (b != null) h.Add(toRoot * b.localToWorldMatrix);
                var mesh = s.sharedMesh;
                int n = mesh != null ? mesh.blendShapeCount : 0;
                for (int i = 0; i < n; i++) h.Add(s.GetBlendShapeWeight(i));
            }
            return h.Value;
        }

        /// <summary>今の形（根のローカルの空間の位置・法線・UV0・サブメッシュの三角形）を読む。</summary>
        void ReadGeometry(MeshEntry e, out float[] positions, out float[] normals, out float[] uv0, out int[][] submeshes)
        {
            var r = e.Renderer;
            var toRoot = Root.transform.worldToLocalMatrix;
            Mesh source; Matrix4x4 m; bool mirrored;
            if (r is SkinnedMeshRenderer skin)
            {
                if (baked == null) baked = new Mesh { name = "YoluPainter Live Link baked pose", hideFlags = HideFlags.HideAndDontSave };
                // 今のポーズと BlendShape を自前のメッシュへ焼く（レンダラーの Transform からの位置、スケールを含む）。元のレンダラーは変えない
                skin.BakeMesh(baked, true);
                var t = skin.transform; var scale = t.lossyScale;
                m = toRoot * Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                mirrored = (m.determinant < 0) != (scale.x * scale.y * scale.z < 0);
                source = baked;
            }
            else
            {
                m = toRoot * r.localToWorldMatrix;
                mirrored = m.determinant < 0;
                source = MeshOf(r);
            }
            var normalMatrix = m.inverse.transpose;
            using (var data = MeshUtility.AcquireReadOnlyMeshData(source))
            {
                var d = data[0]; int n = d.vertexCount;
                positions = new float[n * 3];
                using (var v = new NativeArray<Vector3>(n, Allocator.Temp))
                {
                    d.GetVertices(v);
                    for (int i = 0; i < n; i++) { var p = m.MultiplyPoint3x4(v[i]); positions[i * 3] = p.x; positions[i * 3 + 1] = p.y; positions[i * 3 + 2] = p.z; }
                }
                normals = null;
                if (d.HasVertexAttribute(VertexAttribute.Normal))
                {
                    normals = new float[n * 3];
                    using (var v = new NativeArray<Vector3>(n, Allocator.Temp))
                    {
                        d.GetNormals(v);
                        for (int i = 0; i < n; i++)
                        {
                            var q = normalMatrix.MultiplyVector(v[i]); float len = q.magnitude; if (len > 1e-20f) q /= len;
                            normals[i * 3] = q.x; normals[i * 3 + 1] = q.y; normals[i * 3 + 2] = q.z;
                        }
                    }
                }
                uv0 = null;
                if (d.HasVertexAttribute(VertexAttribute.TexCoord0))
                {
                    uv0 = new float[n * 2];
                    using (var v = new NativeArray<Vector2>(n, Allocator.Temp))
                    {
                        d.GetUVs(0, v);
                        for (int i = 0; i < n; i++) { uv0[i * 2] = v[i].x; uv0[i * 2 + 1] = v[i].y; }
                    }
                }
                submeshes = new int[d.subMeshCount][];
                for (int s = 0; s < d.subMeshCount; s++)
                {
                    var desc = d.GetSubMesh(s);
                    using (var idx = new NativeArray<int>(desc.indexCount, Allocator.Temp))
                    {
                        d.GetIndices(idx, s, true);
                        var a = idx.ToArray();
                        if (mirrored) for (int i = 0; i + 2 < a.Length; i += 3) { int x = a[i + 1]; a[i + 1] = a[i + 2]; a[i + 2] = x; }
                        submeshes[s] = a;
                    }
                }
            }
        }

        static string KeyName(string name)
        {
            var clean = new string((name ?? "").Where(c => c >= 0x20 && c != 0x7f).ToArray());
            return clean.Length > 256 ? clean.Substring(0, 256) : clean;
        }

        /// <summary>モデルを送る（積むだけ）。失敗すれば理由、送れれば null。</summary>
        public string Send(ulong handle)
        {
            if (Root == null) return L.Tr("The model is gone.");
            if (LiveLinkBridge.ModelBegin(handle, KeyName(Root.name)) < 0) return L.Tr("The Live Link library refused the model ({0}).", "begin");
            for (int i = 0; i < Materials.Count; i++)
                if (!WriteMaterial(handle, i, false)) return L.Tr("The Live Link library refused the model ({0}).", "material " + Materials[i].Name);
            for (int i = 0; i < Meshes.Count; i++)
            {
                var e = Meshes[i];
                if (e.Renderer == null) return L.Tr("A renderer of the model was removed.");
                ReadGeometry(e, out var positions, out var normals, out var uv0, out var subs);
                if (positions.Length / 3 != e.VertexCount) return L.Tr("{0}: the mesh changed while it was being sent.", e.Name);
                int mesh = LiveLinkBridge.ModelMesh(handle, e.Key, KeyName(e.Name), e.Skinned, positions, normals, uv0, e.VertexCount);
                if (mesh != i) return L.Tr("The Live Link library refused the model ({0}).", "mesh " + e.Name);
                for (int s = 0; s < subs.Length && s < e.SubmeshMaterials.Length; s++)
                    if (LiveLinkBridge.ModelSubmesh(handle, mesh, e.SubmeshMaterials[s], subs[s]) < 0) return L.Tr("The Live Link library refused the model ({0}).", "submesh " + e.Name + " " + s);
                e.PoseHash = PoseHashOf(e);
            }
            int generation = LiveLinkBridge.ModelSend(handle);
            if (generation <= 0) return L.Tr("The model could not be sent (not connected).");
            Generation = generation;
            return null;
        }

        /// <summary>マテリアルの組 1 つを、モデル（update が偽）かマテリアルの更新（真）の組み立てへ足す。</summary>
        bool WriteMaterial(ulong handle, int i, bool update)
        {
            var e = Materials[i];
            string guid = ""; long fileId = 0;
            if (e.Material != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(e.Material, out string g, out long f) && g != null && g.Length == 32 && g.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) { guid = g; fileId = f; }
            var shader = e.Material != null ? e.Material.shader : null;
            string shaderName = shader != null ? KeyName(shader.name) : "";
            int index = update ? LiveLinkBridge.MaterialsMaterial(handle, e.Material == null, KeyName(e.Name), guid, fileId, shaderName)
                               : LiveLinkBridge.ModelMaterial(handle, e.Material == null, KeyName(e.Name), guid, fileId, shaderName);
            if (index != i) return false;
            if (shader != null)
                foreach (var property in PreviewMaterialBindings.TextureProperties(shader))
                {
                    var t = e.Material.GetTexture(property);
                    int w = t != null ? t.width : 0, h = t != null ? t.height : 0;
                    if (update) LiveLinkBridge.MaterialsTexture(handle, index, property, w, h);
                    else LiveLinkBridge.ModelMaterialTexture(handle, index, property, w, h);
                }
            foreach (var c in e.Shown)
                if (update) LiveLinkBridge.MaterialsRoute(handle, index, (int)c.Channel, c.Property);
                else LiveLinkBridge.ModelMaterialRoute(handle, index, (int)c.Channel, c.Property);
            return true;
        }

        /// <summary>送れなかった更新を、同じ変化で毎回送り直さないように、今のマテリアルの値を送ったものとして覚える。</summary>
        public void AcceptMaterialHash() => MaterialHash = ComputeMaterialHash();

        /// <summary>マテリアルの情報（シェーダー・キーワード・テクスチャのプロパティ・見せるチャンネル）を決め直し、モデルは送り直さずに更新だけを送る。
        /// 失敗すれば理由、送れれば null。</summary>
        public string SendMaterials(ulong handle)
        {
            if (Root == null) return L.Tr("The model is gone.");
            if (Generation <= 0) return L.Tr("The model was not sent yet.");
            texturePropertyCache.Clear();
            foreach (var m in Materials) Resolve(m);
            if (LiveLinkBridge.MaterialsBegin(handle) < 0) return L.Tr("The Live Link library refused the material update ({0}).", "begin");
            for (int i = 0; i < Materials.Count; i++)
                if (!WriteMaterial(handle, i, true)) return L.Tr("The Live Link library refused the material update ({0}).", "material " + Materials[i].Name);
            if (LiveLinkBridge.MaterialsSend(handle) < 0) return L.Tr("The material update could not be sent (not connected).");
            MaterialHash = ComputeMaterialHash();
            return null;
        }

        /// <summary>形の変わったメッシュ（スキンメッシュのポーズ・BlendShape、動かしたレンダラー）を送る。送ったメッシュの数を返す（失敗は負）。</summary>
        public int SendPoseIfChanged(ulong handle)
        {
            if (Generation <= 0 || Root == null) return 0;
            var changed = new List<int>();
            for (int i = 0; i < Meshes.Count; i++)
            {
                var e = Meshes[i];
                if (e.Renderer == null) continue;
                ulong h = PoseHashOf(e);
                if (h != e.PoseHash) { changed.Add(i); e.PoseHash = h; }
            }
            if (changed.Count == 0) return 0;
            if (LiveLinkBridge.PoseBegin(handle) < 0) return -1;
            foreach (int i in changed)
            {
                ReadGeometry(Meshes[i], out var positions, out var normals, out _, out _);
                if (positions.Length / 3 != Meshes[i].VertexCount) return -1; // メッシュが差し替わった（構造の変化で送り直す）
                if (LiveLinkBridge.PoseMesh(handle, i, positions, normals, Meshes[i].VertexCount) < 0) return -1;
            }
            return LiveLinkBridge.PoseSend(handle) < 0 ? -1 : changed.Count;
        }

        public void Dispose()
        {
            if (baked != null) Object.DestroyImmediate(baked);
            baked = null;
        }

        /// <summary>64 ビットの FNV-1a（ポーズ・構造の変化を見るだけ。保存しない）。</summary>
        struct Hasher
        {
            ulong value; bool started;
            public ulong Value => started ? value : 14695981039346656037UL;
            public void Add(int x) { Mix((uint)x); }
            public void Add(float x) { Mix((uint)BitConverter.SingleToInt32Bits(x)); }
            public void Add(Matrix4x4 m) { for (int i = 0; i < 16; i++) Add(m[i]); }
            void Mix(uint x)
            {
                if (!started) { value = 14695981039346656037UL; started = true; }
                for (int i = 0; i < 4; i++) { value ^= (x >> (i * 8)) & 0xff; value *= 1099511628211UL; }
            }
        }
    }
}
