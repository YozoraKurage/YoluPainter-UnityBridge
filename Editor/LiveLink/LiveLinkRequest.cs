using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// 頼み（<c>inbox/&lt;id&gt;.json</c>）: 選んだ相手（シーンの GameObject）の、FBX の道・取り込みの設定・レンダラー・ボーンの値・BlendShape・
    /// マテリアルの値とテクスチャの道。メッシュと絵の画素は入れない（スタンドアロンがファイルを読む）。相手の下のレンダラー
    /// （MeshRenderer・SkinnedMeshRenderer。無効なものも、表示の入切を添えて）のうち、メッシュが .fbx から来たものだけを送り、ほかは理由つきで
    /// <see cref="Refused"/> に入れる。読むだけで、シーン・アセットには書かない。
    /// </summary>
    internal sealed class LiveLinkRequest
    {
        public const int Format = 1;

        internal sealed class ModelEntry
        {
            public int Id;
            public LiveLinkFbxMap Map;
            public string Fbx, Guid;
            public float GlobalScale = 1;
            public bool UseFileScale = true, BakeAxisConversion, ImportBlendShapes = true, PreserveHierarchy;
        }

        internal sealed class RendererEntry
        {
            public string Path, Node;
            public int Model;
            public bool Enabled, Skinned;
            public readonly List<KeyValuePair<string, float>> BlendShapes = new List<KeyValuePair<string, float>>();
            public readonly List<int> Materials = new List<int>();
            public Renderer Renderer;
        }

        internal sealed class BoneEntry
        {
            public int Model;
            public string Node;
            public LiveLinkFbxMap.Local Local;
        }

        internal sealed class MaterialEntry
        {
            public string Key, Name;
            public Material Material;
            public LiveLinkMaterialValues.Snapshot Values;
        }

        /// <summary>送れなかったレンダラー（相手の根からの道と理由の言葉）。</summary>
        internal readonly struct Refusal
        {
            public readonly string Path, Reason;
            public Refusal(string path, string reason) { Path = path; Reason = reason; }
        }

        public string Id;
        public string BridgeVersion = "", UnityVersion = "";
        public string ProjectRoot = "", ProjectName = "";
        public string TargetKey = "", TargetName = "", ExportDir = "";
        public Matrix4x4 RootWorld = Matrix4x4.identity;
        public readonly List<ModelEntry> Models = new List<ModelEntry>();
        public readonly List<RendererEntry> Renderers = new List<RendererEntry>();
        public readonly List<BoneEntry> Bones = new List<BoneEntry>();
        public readonly List<MaterialEntry> Materials = new List<MaterialEntry>();
        public readonly List<Refusal> Refused = new List<Refusal>();

        /// <summary>マテリアルの無いサブメッシュの組の鍵。</summary>
        public const string NoMaterialKey = "none";

        // スタンドアロンが断る大きさ（受け渡しの決まりの上限）。超える頼みは置かずに、ここで理由を出す
        public const long MaxBytes = 16L << 20;
        public const int MaxRenderers = 1024, MaxBones = 16384, MaxMaterials = 1024, MaxTexturesPerMaterial = 256;

        /// <summary>上限を超えるか（<paramref name="bytes"/> は UTF-8 の JSON の大きさ）。</summary>
        public static bool OverLimits(long bytes, int renderers, int refused, int models, int bones, int materials, int texturesInOneMaterial) =>
            bytes > MaxBytes || renderers > MaxRenderers || refused > MaxRenderers || models > MaxRenderers || bones > MaxBones ||
            materials > MaxMaterials || texturesInOneMaterial > MaxTexturesPerMaterial;

        /// <summary>この頼みが上限を超えるか。</summary>
        public bool OverLimits(string json) =>
            OverLimits(System.Text.Encoding.UTF8.GetByteCount(json), Renderers.Count, Refused.Count, Models.Count, Bones.Count, Materials.Count,
                Materials.Count == 0 ? 0 : Materials.Max(m => m.Values?.Textures.Count ?? 0));

        // ───────── 作る ─────────

        /// <summary>相手から頼みを作る（id は新しい UUID）。</summary>
        public static LiveLinkRequest Build(GameObject target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            var r = new LiveLinkRequest
            {
                Id = System.Guid.NewGuid().ToString("D"),
                BridgeVersion = BridgeVersionText(),
                UnityVersion = Application.unityVersion,
                ProjectRoot = LiveLinkSettings.ProjectRoot,
                TargetKey = TargetKeyOf(target),
                TargetName = target.name,
                ExportDir = LiveLinkSettings.ExportDirFor(target.name),
                RootWorld = target.transform.worldToLocalMatrix,
            };
            r.ProjectName = Path.GetFileName(r.ProjectRoot.TrimEnd('/'));
            var maps = new Dictionary<(string, Transform), LiveLinkFbxMap>();
            var models = new Dictionary<LiveLinkFbxMap, ModelEntry>();
            var materials = new Dictionary<Material, int>();
            int noMaterial = -1;

            // 1 回目: レンダラーごとに FBX のモデルを探す
            var found = new List<(Renderer Renderer, Mesh Mesh, string Path, string AssetPath, LiveLinkFbxMap Map, string Why)>();
            foreach (var renderer in target.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                var mesh = LiveLinkFbxMap.MeshOf(renderer);
                if (mesh == null) continue; // 何も描かないレンダラーは送らない（失う物も無い）
                string path = LiveLinkFbxMap.PathFrom(target.transform, renderer.transform);
                string assetPath = AssetDatabase.GetAssetPath(mesh);
                if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add((renderer, mesh, path, null, null, LiveLinkReason.MeshNotFromFbx));
                    continue;
                }
                var map = LiveLinkFbxMap.Find(renderer, mesh, assetPath, maps, out string why);
                found.Add((renderer, mesh, path, assetPath, map, why));
            }
            // 2 回目: 名前の道で根が見つからなかったレンダラー（親を付け替えたボーンの下のメッシュ等）は、同じ FBX のモデルがその Transform を
            // もう結んでいれば、そのモデルのもの
            for (int i = 0; i < found.Count; i++)
            {
                var f = found[i];
                if (f.Map != null || f.AssetPath == null) continue;
                foreach (var m in maps.Values)
                    if (m.AssetPath == f.AssetPath && m.AssetOf(f.Renderer.transform) != null) { found[i] = (f.Renderer, f.Mesh, f.Path, f.AssetPath, m, null); break; }
            }
            foreach (var (renderer, mesh, path, _, map, foundWhy) in found)
            {
                if (map == null) { r.Refused.Add(new Refusal(path, foundWhy ?? LiveLinkReason.BoneNotFound)); continue; }
                // メッシュのノード: FBX の中で同じメッシュを持ち、このレンダラーの Transform に当たるノード
                string node = null, why = null;
                var asset = map.AssetOf(renderer.transform);
                if (asset == null) why = map.WhyNotMapped(renderer.transform);
                else if (LiveLinkFbxMap.MeshOf(asset.GetComponent<Renderer>()) != mesh) why = LiveLinkReason.BoneNotFound;
                else if (!map.Addressable(asset)) why = LiveLinkReason.AmbiguousBone;
                else node = map.PathOf(asset);
                if (node == null) { r.Refused.Add(new Refusal(path, why ?? LiveLinkReason.BoneNotFound)); continue; }
                // スキンのボーンが全部 FBX のノードに決まること
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    foreach (var bone in skinned.bones)
                    {
                        string boneWhy = map.WhyNotMapped(bone);
                        if (boneWhy != null) { why = boneWhy; break; }
                    }
                    if (why != null) { r.Refused.Add(new Refusal(path, why)); continue; }
                }
                if (!models.TryGetValue(map, out var model))
                {
                    model = r.AddModel(map);
                    models.Add(map, model);
                }
                var entry = new RendererEntry
                {
                    Path = path, Node = node, Model = model.Id, Renderer = renderer,
                    Enabled = renderer.enabled && renderer.gameObject.activeInHierarchy,
                    Skinned = renderer is SkinnedMeshRenderer,
                };
                if (renderer is SkinnedMeshRenderer smr)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    for (int k = 0; k < mesh.blendShapeCount; k++)
                    {
                        string name = mesh.GetBlendShapeName(k);
                        float w = smr.GetBlendShapeWeight(k);
                        if (!names.Add(name) || float.IsNaN(w) || float.IsInfinity(w)) continue;
                        entry.BlendShapes.Add(new KeyValuePair<string, float>(name, w));
                    }
                }
                var mats = renderer.sharedMaterials;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var m = s < mats.Length ? mats[s] : null;
                    int index;
                    if (m == null)
                    {
                        if (noMaterial < 0) { noMaterial = r.Materials.Count; r.Materials.Add(new MaterialEntry { Key = NoMaterialKey, Name = "", Values = new LiveLinkMaterialValues.Snapshot() }); }
                        index = noMaterial;
                    }
                    else if (!materials.TryGetValue(m, out index))
                    {
                        index = r.Materials.Count;
                        materials.Add(m, index);
                        r.Materials.Add(new MaterialEntry { Key = MaterialKey(m), Name = m.name, Material = m, Values = LiveLinkMaterialValues.Read(m) });
                    }
                    entry.Materials.Add(index);
                }
                r.Renderers.Add(entry);
            }
            foreach (var model in r.Models) r.AddBones(model, target.transform);
            return r;
        }

        ModelEntry AddModel(LiveLinkFbxMap map)
        {
            var model = new ModelEntry
            {
                Id = Models.Count, Map = map,
                Fbx = LiveLinkFbxMap.PhysicalPath(map.AssetPath),
                Guid = AssetDatabase.AssetPathToGUID(map.AssetPath),
            };
            if (AssetImporter.GetAtPath(map.AssetPath) is ModelImporter mi)
            {
                model.GlobalScale = mi.globalScale;
                model.UseFileScale = mi.useFileScale;
                model.BakeAxisConversion = mi.bakeAxisConversion;
                model.ImportBlendShapes = mi.importBlendShapes;
                model.PreserveHierarchy = mi.preserveHierarchy;
            }
            Models.Add(model);
            return model;
        }

        /// <summary>FBX のノードのうち、シーンの Transform に当たり、名前の道で 1 つに引けるものの値（FBX の親のノードに当たる Transform に対する値）。
        /// FBX の根に当たる Transform が対象そのもの（か対象の先祖）なら、根（道 ""）は送らない: 対象に対する値は単位になり、根の子が 1 つだけで
        /// 畳まれた FBX では、その子の変換（軸の回転など）が Unity ではプレハブの根に移っているので、単位を送るとスタンドアロンでその変換が消える。
        /// 送らなければ、スタンドアロンは FBX のままの値を使う。対象の下に置いた FBX の根は、対象に対する値を送る。</summary>
        void AddBones(ModelEntry model, Transform target)
        {
            bool rootIsTarget = LiveLinkFbxMap.IsUnder(target, model.Map.SceneRoot);
            foreach (var node in model.Map.AssetNodes)
            {
                if (!model.Map.Addressable(node)) continue;
                if (node == model.Map.AssetRoot && rootIsTarget) continue;
                var local = model.Map.LocalOf(node, target);
                if (local == null) continue;
                Bones.Add(new BoneEntry { Model = model.Id, Node = model.Map.PathOf(node), Local = local.Value });
            }
        }

        /// <summary>対象の身元（同じ対象の送り直しを見分ける）: <see cref="GlobalObjectId"/>。それが無い物（プレビューのシーンの中など。どれも同じ空の id になる）は、
        /// このエディターのセッションの中だけの <c>instance:&lt;InstanceID&gt;</c>。</summary>
        public static string TargetKeyOf(GameObject target)
        {
            var id = GlobalObjectId.GetGlobalObjectIdSlow(target);
            return id.identifierType == 0 ? "instance:" + target.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture) : id.ToString();
        }

        /// <summary>マテリアルの身元: アセットなら <c>guid:&lt;GUID&gt;/fileid:&lt;ローカルの番号&gt;</c>、シーンの中のマテリアルは <c>object:&lt;GlobalObjectId&gt;</c>。
        /// GlobalObjectId が空の物（プレビューのシーン・保存していないシーンの中、DontSave のマテリアル。どれも同じ空の id になる）は、
        /// このエディターのセッションの中だけの <c>instance:&lt;InstanceID&gt;</c>（<see cref="LiveLinkImport.FindMaterial"/> が引く）。</summary>
        public static string MaterialKey(Material m)
        {
            if (m == null) return NoMaterialKey;
            if (EditorUtility.IsPersistent(m) && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(m, out string guid, out long fileId) && !string.IsNullOrEmpty(guid))
                return "guid:" + guid + "/fileid:" + fileId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var id = GlobalObjectId.GetGlobalObjectIdSlow(m);
            return id.identifierType == 0 ? "instance:" + m.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture) : "object:" + id;
        }

        /// <summary>このパッケージの版。</summary>
        public static string BridgeVersionText()
        {
            try { return PackageInfo.FindForAssembly(typeof(LiveLinkRequest).Assembly)?.version ?? ""; }
            catch (Exception) { return ""; }
        }

        // ───────── 書く ─────────

        public string ToJson()
        {
            var w = new JsonWriter();
            w.BeginObject();
            w.Name("format").Int(Format);
            w.Name("kind").String("open");
            w.Name("id").String(Id);
            w.Name("bridge").BeginObject().Name("version").String(BridgeVersion).Name("unity").String(UnityVersion).EndObject();
            w.Name("project").BeginObject().Name("root").String(ProjectRoot).Name("name").String(ProjectName).EndObject();
            w.Name("target").BeginObject().Name("key").String(TargetKey).Name("name").String(TargetName).Name("export_dir").String(ExportDir).EndObject();
            if (Finite(RootWorld))
            {
                w.Name("root").BeginObject().Name("world").BeginArray();
                for (int c = 0; c < 4; c++) for (int row = 0; row < 4; row++) w.Number(RootWorld[row, c]);
                w.EndArray().EndObject();
            }
            w.Name("models").BeginArray();
            foreach (var m in Models)
            {
                w.BeginObject().Name("id").Int(m.Id).Name("fbx").String(m.Fbx).Name("guid").String(m.Guid);
                w.Name("import").BeginObject()
                    .Name("global_scale").Number(m.GlobalScale)
                    .Name("use_file_scale").Bool(m.UseFileScale)
                    .Name("bake_axis_conversion").Bool(m.BakeAxisConversion)
                    .Name("import_blend_shapes").Bool(m.ImportBlendShapes)
                    .Name("preserve_hierarchy").Bool(m.PreserveHierarchy)
                    .EndObject();
                w.EndObject();
            }
            w.EndArray();
            w.Name("renderers").BeginArray();
            foreach (var e in Renderers)
            {
                w.BeginObject().Name("path").String(e.Path).Name("model").Int(e.Model).Name("node").String(e.Node)
                    .Name("enabled").Bool(e.Enabled).Name("skinned").Bool(e.Skinned);
                w.Name("blend_shapes").BeginObject();
                foreach (var kv in e.BlendShapes) w.Name(kv.Key).Number(kv.Value);
                w.EndObject();
                w.Name("materials").BeginArray();
                foreach (int i in e.Materials) w.Int(i);
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();
            w.Name("bones").BeginArray();
            foreach (var b in Bones)
            {
                var l = b.Local;
                w.BeginObject().Name("model").Int(b.Model).Name("node").String(b.Node).Name("local").BeginObject()
                    .Name("t").Numbers(l.T.x, l.T.y, l.T.z)
                    .Name("r").Numbers(l.R.x, l.R.y, l.R.z, l.R.w)
                    .Name("s").Numbers(l.S.x, l.S.y, l.S.z)
                    .EndObject().EndObject();
            }
            w.EndArray();
            w.Name("materials").BeginArray();
            foreach (var m in Materials) WriteMaterial(w, m);
            w.EndArray();
            w.Name("refused").BeginArray();
            foreach (var x in Refused) w.BeginObject().Name("path").String(x.Path).Name("reason").String(x.Reason).EndObject();
            w.EndArray();
            w.EndObject();
            return w.ToString();
        }

        static void WriteMaterial(JsonWriter w, MaterialEntry m)
        {
            var v = m.Values ?? new LiveLinkMaterialValues.Snapshot();
            w.BeginObject().Name("key").String(m.Key).Name("name").String(m.Name ?? "");
            w.Name("shader").BeginObject().Name("name").String(v.ShaderName).Name("guid").String(v.ShaderGuid)
                .Name("package").String(v.ShaderPackage).Name("version").String(v.ShaderVersion);
            w.Name("keywords").BeginArray();
            foreach (var k in v.Keywords) w.String(k);
            w.EndArray();
            if (m.Material != null) w.Name("render_queue").Int(v.RenderQueue);
            w.EndObject();
            w.Name("values").BeginObject();
            w.Name("floats").BeginObject(); foreach (var kv in v.Floats) w.Name(kv.Key).Number(kv.Value); w.EndObject();
            w.Name("colors").BeginObject(); foreach (var kv in v.Colors) w.Name(kv.Key).Numbers(kv.Value.r, kv.Value.g, kv.Value.b, kv.Value.a); w.EndObject();
            w.Name("vectors").BeginObject(); foreach (var kv in v.Vectors) w.Name(kv.Key).Numbers(kv.Value.x, kv.Value.y, kv.Value.z, kv.Value.w); w.EndObject();
            w.Name("ints").BeginObject(); foreach (var kv in v.Ints) w.Name(kv.Key).Int(kv.Value); w.EndObject();
            w.EndObject();
            w.Name("textures").BeginArray();
            foreach (var t in v.Textures)
            {
                w.BeginObject().Name("property").String(t.Property);
                // Unity の中にしかない絵は道を null に（スタンドアロンはファイルの無いスロットとして扱う）
                if (t.Path == null) w.Name("path").Null();
                else w.Name("path").String(t.Path).Name("guid").String(t.Guid);
                w.Name("srgb").Bool(t.Srgb).Name("normal_map").Bool(t.NormalMap)
                    .Name("scale").Numbers(t.Scale.x, t.Scale.y).Name("offset").Numbers(t.Offset.x, t.Offset.y).EndObject();
            }
            w.EndArray();
            w.EndObject();
        }

        static bool Finite(Matrix4x4 m)
        {
            for (int i = 0; i < 16; i++) if (float.IsNaN(m[i]) || float.IsInfinity(m[i])) return false;
            return true;
        }
    }
}
