using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor.LiveLink;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 頼みの JSON を作る: FBX から来たメッシュ・来ていないメッシュ（<c>refused</c>）、FBX の中の道（対応が取れる・展開して名前で探す・名前が重なる・
    /// 根の子が 1 つだけで畳まれた FBX）、FBX の根の値を送るか（相手の下のときだけ）、親を付け替えたボーンの <c>local</c>、BlendShape、マテリアルの値と
    /// テクスチャの道（sRGB・ノーマルマップ・拡大とずらし・ファイルの無い絵は道 null）、シェーダーのパッケージ、取り込みの設定。試験の FBX は ASCII で書いてテストプロジェクトの一時のフォルダに取り込む。
    /// </summary>
    public sealed class LiveLinkRequestTests
    {
        LiveLinkTestScope scope;

        [SetUp] public void SetUp() => scope = new LiveLinkTestScope();
        [TearDown] public void TearDown() => scope.Dispose();

        static Dictionary<string, object> Json(LiveLinkRequest r) => (Dictionary<string, object>)JsonReader.Parse(r.ToJson());

        static List<Dictionary<string, object>> Items(Dictionary<string, object> o, string name) => JsonReader.Arr(o, name).Cast<Dictionary<string, object>>().ToList();

        static float[] Floats(object list) => ((List<object>)list).Select(x => (float)(double)x).ToArray();

        static void AreClose(float[] actual, float[] expected, string what)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length), what);
            for (int i = 0; i < actual.Length; i++) Assert.That(actual[i], Is.EqualTo(expected[i]).Within(1e-5f), what + "[" + i + "]");
        }

        static Dictionary<string, object> Bone(Dictionary<string, object> json, string node) =>
            Items(json, "bones").SingleOrDefault(b => JsonReader.Str(b, "node") == node);

        static void LocalIs(Dictionary<string, object> json, string node, Vector3 t, Quaternion r, Vector3 s)
        {
            var b = Bone(json, node);
            Assert.That(b, Is.Not.Null, "bone " + node);
            var local = JsonReader.Obj(b, "local");
            AreClose(Floats(local["t"]), new[] { t.x, t.y, t.z }, node + ".t");
            var q = Floats(local["r"]);
            // q と -q は同じ回転
            float sign = Mathf.Sign(q[0] * r.x + q[1] * r.y + q[2] * r.z + q[3] * r.w);
            AreClose(q.Select(x => x * sign).ToArray(), new[] { r.x, r.y, r.z, r.w }, node + ".r");
            AreClose(Floats(local["s"]), new[] { s.x, s.y, s.z }, node + ".s");
        }

        [Test]
        public void AnFbxInstanceIsSentWithItsFileImportSettingsNodesAndPose()
        {
            string fbx = scope.WriteFbx("Arm", FbxAscii.ArmScene());
            var target = scope.Instantiate(fbx);
            target.transform.position = new Vector3(3, 0, 0);
            var upper = target.transform.Find("Armature/Upper");
            upper.localRotation = Quaternion.Euler(0, 0, 30);
            var r = LiveLinkRequest.Build(target);
            var json = Json(r);

            Assert.That(JsonReader.Num(json, "format"), Is.EqualTo(1));
            Assert.That(JsonReader.Str(json, "kind"), Is.EqualTo("open"));
            Assert.That(Guid.TryParse(JsonReader.Str(json, "id"), out _), Is.True);
            Assert.That(JsonReader.Str(JsonReader.Obj(json, "bridge"), "unity"), Is.EqualTo(Application.unityVersion));
            Assert.That(JsonReader.Str(JsonReader.Obj(json, "project"), "root"), Is.EqualTo(LiveLinkSettings.ProjectRoot));
            var t = JsonReader.Obj(json, "target");
            Assert.That(JsonReader.Str(t, "key"), Is.EqualTo(LiveLinkRequest.TargetKeyOf(target)));
            Assert.That(JsonReader.Str(t, "name"), Is.EqualTo("Arm"));
            Assert.That(JsonReader.Str(t, "export_dir"), Is.EqualTo(LiveLinkSettings.ProjectRoot + "/Assets/YoluPainter/Arm"));
            AreClose(Floats(JsonReader.Obj(json, "root")["world"]), Enumerable.Range(0, 16).Select(i => target.transform.worldToLocalMatrix[i % 4, i / 4]).ToArray(), "root.world (column-major)");

            var models = Items(json, "models");
            Assert.That(models.Count, Is.EqualTo(1));
            Assert.That(JsonReader.Str(models[0], "fbx"), Is.EqualTo(LiveLinkTestScope.Absolute(fbx)));
            Assert.That(JsonReader.Str(models[0], "guid"), Is.EqualTo(AssetDatabase.AssetPathToGUID(fbx)));
            var import = JsonReader.Obj(models[0], "import");
            Assert.That(JsonReader.Num(import, "global_scale"), Is.EqualTo(1));
            Assert.That(JsonReader.Bool(import, "use_file_scale"), Is.True);
            Assert.That(JsonReader.Bool(import, "bake_axis_conversion"), Is.False);
            Assert.That(JsonReader.Bool(import, "import_blend_shapes"), Is.True);
            Assert.That(JsonReader.Bool(import, "preserve_hierarchy"), Is.False);

            var renderers = Items(json, "renderers").ToDictionary(x => JsonReader.Str(x, "path"));
            Assert.That(renderers.Keys, Is.EquivalentTo(new[] { "ArmMesh", "Armature/Upper/Lower/Hat" }));
            var arm = renderers["ArmMesh"];
            Assert.That(JsonReader.Str(arm, "node"), Is.EqualTo("ArmMesh"));
            Assert.That(JsonReader.Num(arm, "model"), Is.EqualTo(0));
            Assert.That(JsonReader.Bool(arm, "skinned"), Is.True);
            Assert.That(JsonReader.Bool(arm, "enabled"), Is.True);
            Assert.That(JsonReader.Arr(arm, "materials").Select(x => (double)x), Is.EqualTo(new[] { 0.0, 1.0 }), "Skin and Cloth, in submesh order");
            var hat = renderers["Armature/Upper/Lower/Hat"];
            Assert.That(JsonReader.Str(hat, "node"), Is.EqualTo("Armature/Upper/Lower/Hat"));
            Assert.That(JsonReader.Bool(hat, "skinned"), Is.False);
            Assert.That(JsonReader.Arr(hat, "materials").Select(x => (double)x), Is.EqualTo(new[] { 0.0 }), "the same material is one entry");

            // ボーン: FBX のノード全部。相手が FBX の根そのものなので、根（""）は送らない（スタンドアロンは FBX のままの値）。
            // Unity の取り込みは X を裏返す（Lower は -1）
            Assert.That(Items(json, "bones").Select(b => JsonReader.Str(b, "node")),
                Is.EquivalentTo(new[] { "Armature", "Armature/Upper", "Armature/Upper/Lower", "Armature/Upper/Lower/Hat", "ArmMesh" }));
            LocalIs(json, "Armature/Upper", new Vector3(0, 1, 0), Quaternion.Euler(0, 0, 30), Vector3.one);
            LocalIs(json, "Armature/Upper/Lower", new Vector3(-1, 0, 0), Quaternion.identity, Vector3.one);
            LocalIs(json, "Armature/Upper/Lower/Hat", new Vector3(-0.5f, 0.2f, 0), Quaternion.identity, Vector3.one);
            Assert.That(Items(json, "refused"), Is.Empty);

            var materials = Items(json, "materials");
            Assert.That(materials.Select(m => JsonReader.Str(m, "name")), Is.EqualTo(new[] { "Skin", "Cloth" }));
            var skin = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<Material>().Single(m => m.name == "Skin");
            Assert.That(JsonReader.Str(materials[0], "key"), Is.EqualTo(LiveLinkRequest.MaterialKey(skin)));
            Assert.That(JsonReader.Str(materials[0], "key"), Does.StartWith("guid:" + AssetDatabase.AssetPathToGUID(fbx) + "/fileid:"));
            Assert.That(LiveLinkImport.FindMaterial(JsonReader.Str(materials[0], "key")), Is.EqualTo(skin), "the key finds the material again");
        }

        [Test]
        public void AMeshThatIsNotFromAnFbxIsRefusedAndTheRestIsSent()
        {
            string fbx = scope.WriteFbx("Arm", FbxAscii.ArmScene());
            var root = scope.Own(new GameObject("Avatar"));
            var arm = scope.Instantiate(fbx);
            arm.transform.SetParent(root.transform, false);
            arm.transform.localPosition = new Vector3(0, 2, 0);
            var cube = scope.NotFromFbx("Accessory");
            cube.transform.SetParent(root.transform, false);
            var saved = UnityEngine.Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh);
            AssetDatabase.CreateAsset(saved, scope.AssetFolder + "/Saved.asset");
            var other = scope.Own(new GameObject("Saved", typeof(MeshFilter), typeof(MeshRenderer)));
            other.GetComponent<MeshFilter>().sharedMesh = saved;
            other.transform.SetParent(root.transform, false);
            other.GetComponent<MeshRenderer>().enabled = false;

            var r = LiveLinkRequest.Build(root);
            var json = Json(r);
            Assert.That(Items(json, "refused").Select(x => JsonReader.Str(x, "path") + ":" + JsonReader.Str(x, "reason")),
                Is.EquivalentTo(new[] { "Accessory:" + LiveLinkReason.MeshNotFromFbx, "Saved:" + LiveLinkReason.MeshNotFromFbx }));
            Assert.That(Items(json, "renderers").Select(x => JsonReader.Str(x, "path")), Is.EquivalentTo(new[] { "Arm/ArmMesh", "Arm/Armature/Upper/Lower/Hat" }),
                "the path is from the target's root; the node is inside the FBX");
            Assert.That(Items(json, "renderers").Select(x => JsonReader.Str(x, "node")), Is.EquivalentTo(new[] { "ArmMesh", "Armature/Upper/Lower/Hat" }));
            // FBX の根は、相手の根に対する値
            LocalIs(json, "", new Vector3(0, 2, 0), Quaternion.identity, Vector3.one);
        }

        [Test]
        public void TheSameFbxPlacedTwiceOrInsideItselfIsOneModelEach()
        {
            string fbx = scope.WriteFbx("Arm", FbxAscii.ArmScene());
            var root = scope.Own(new GameObject("Avatar"));
            var a = scope.Instantiate(fbx); a.transform.SetParent(root.transform, false);
            var b = scope.Instantiate(fbx); b.name = "Arm2"; b.transform.SetParent(root.transform, false); b.transform.localPosition = new Vector3(1, 0, 0);
            var nested = scope.Instantiate(fbx); nested.name = "Inner"; nested.transform.SetParent(a.transform.Find("Armature/Upper"), false);
            var json = Json(LiveLinkRequest.Build(root));
            Assert.That(Items(json, "refused"), Is.Empty);
            Assert.That(Items(json, "models").Count, Is.EqualTo(3));
            var byPath = Items(json, "renderers").ToDictionary(x => JsonReader.Str(x, "path"), x => (int)JsonReader.Num(x, "model").Value);
            Assert.That(byPath.Keys, Is.EquivalentTo(new[] { "Arm/ArmMesh", "Arm/Armature/Upper/Lower/Hat", "Arm2/ArmMesh", "Arm2/Armature/Upper/Lower/Hat",
                "Arm/Armature/Upper/Inner/ArmMesh", "Arm/Armature/Upper/Inner/Armature/Upper/Lower/Hat" }));
            Assert.That(new[] { byPath["Arm/ArmMesh"], byPath["Arm2/ArmMesh"], byPath["Arm/Armature/Upper/Inner/ArmMesh"] }.Distinct().Count(), Is.EqualTo(3));
            Assert.That(byPath["Arm/Armature/Upper/Inner/ArmMesh"], Is.EqualTo(byPath["Arm/Armature/Upper/Inner/Armature/Upper/Lower/Hat"]));
            var inner = Items(json, "bones").Where(x => (int)JsonReader.Num(x, "model").Value == byPath["Arm/Armature/Upper/Inner/ArmMesh"]).ToList();
            Assert.That(inner.Count, Is.EqualTo(6), "the inner copy has all its nodes");
            var innerRoot = inner.Single(x => JsonReader.Str(x, "node") == "");
            AreClose(Floats(JsonReader.Obj(innerRoot, "local")["t"]), new[] { 0f, 1f, 0f }, "the inner root relative to the target");
        }

        [Test]
        public void TheTargetIsKnownByItsGlobalObjectIdOrInThisSessionByItsInstance()
        {
            var inScene = new GameObject("Plain"); // 描く物の無い、開いているシーンの物
            try
            {
                Assert.That(LiveLinkRequest.TargetKeyOf(inScene), Is.EqualTo(GlobalObjectId.GetGlobalObjectIdSlow(inScene).ToString()));
                Assert.That(LiveLinkRequest.TargetKeyOf(inScene), Does.StartWith("GlobalObjectId_V1-2-"));
            }
            finally { UnityEngine.Object.DestroyImmediate(inScene); }
            // プレビューのシーンの物は GlobalObjectId が空なので、対象ごとに違う鍵にする
            var a = scope.Own(new GameObject("A"));
            var b = scope.Own(new GameObject("B"));
            Assert.That(GlobalObjectId.GetGlobalObjectIdSlow(a).identifierType, Is.EqualTo(0));
            Assert.That(LiveLinkRequest.TargetKeyOf(a), Is.EqualTo("instance:" + a.GetInstanceID()));
            Assert.That(LiveLinkRequest.TargetKeyOf(a), Is.Not.EqualTo(LiveLinkRequest.TargetKeyOf(b)));
        }

        [Test]
        public void MaterialsWithoutAnIdentityAreSentWithKeysOfTheirOwn()
        {
            string fbx = scope.WriteFbx("Arm", FbxAscii.ArmScene());
            var target = scope.Instantiate(fbx);
            // プレビューのシーン・保存していないシーンの中のマテリアルは GlobalObjectId が空で、どれも同じ空の id になる
            var a = scope.CreateSceneMaterial("A");
            var b = scope.CreateSceneMaterial("B");
            Assert.That(GlobalObjectId.GetGlobalObjectIdSlow(a).identifierType, Is.EqualTo(0));
            target.GetComponentInChildren<SkinnedMeshRenderer>().sharedMaterials = new[] { a, b };
            var all = Items(Json(LiveLinkRequest.Build(target)), "materials").Select(m => JsonReader.Str(m, "key")).ToArray();
            Assert.That(all.Distinct().Count(), Is.EqualTo(all.Length), "no two materials share a key");
            var keys = all.Where(k => k.StartsWith("instance:")).ToArray(); // 帽子のレンダラーは FBX のマテリアルのまま
            Assert.That(keys, Is.EqualTo(new[] { "instance:" + a.GetInstanceID(), "instance:" + b.GetInstanceID() }));
            Assert.That(LiveLinkImport.FindMaterial(keys[0]), Is.EqualTo(a));
            Assert.That(LiveLinkImport.FindMaterial(keys[1]), Is.EqualTo(b));
            Assert.That(LiveLinkImport.FindMaterial(keys[0], false), Is.Null, "an InstanceID of another editor session is not looked up");
            Assert.That(LiveLinkImport.FindMaterial("instance:x"), Is.Null);
        }

        [Test]
        public void ADisabledRendererIsSentAsHidden()
        {
            string fbx = scope.WriteFbx("Arm", FbxAscii.ArmScene());
            var target = scope.Instantiate(fbx);
            target.transform.Find("Armature/Upper/Lower/Hat").gameObject.SetActive(false);
            var renderers = Items(Json(LiveLinkRequest.Build(target)), "renderers").ToDictionary(x => JsonReader.Str(x, "path"));
            Assert.That(JsonReader.Bool(renderers["Armature/Upper/Lower/Hat"], "enabled"), Is.False);
            Assert.That(JsonReader.Bool(renderers["ArmMesh"], "enabled"), Is.True);
        }

        [Test]
        public void AnUnpackedInstanceIsFoundByNamesAndBySkinBones()
        {
            string fbx = scope.WriteFbx("Arm", FbxAscii.ArmScene());
            var target = scope.Instantiate(fbx);
            PrefabUtility.UnpackPrefabInstance(target, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            Assert.That(PrefabUtility.GetCorrespondingObjectFromOriginalSource(target.transform), Is.Null, "no correspondence after unpacking");
            target.name = "Renamed";
            var json = Json(LiveLinkRequest.Build(target));
            Assert.That(Items(json, "refused"), Is.Empty);
            Assert.That(Items(json, "renderers").Select(x => JsonReader.Str(x, "node")), Is.EquivalentTo(new[] { "ArmMesh", "Armature/Upper/Lower/Hat" }));
            Assert.That(Items(json, "bones").Select(b => JsonReader.Str(b, "node")),
                Is.EquivalentTo(new[] { "Armature", "Armature/Upper", "Armature/Upper/Lower", "Armature/Upper/Lower/Hat", "ArmMesh" }));
            LocalIs(json, "Armature/Upper/Lower", new Vector3(-1, 0, 0), Quaternion.identity, Vector3.one);
        }

        [Test]
        public void TheFbxRootIsSentOnlyWhenItIsBelowTheTarget()
        {
            string fbx = scope.WriteFbx("Arm", FbxAscii.ArmScene());
            // 相手が FBX の中の子: FBX の根は相手の先祖なので送らない。ほかのノードは FBX の親に対する値
            var instance = scope.Instantiate(fbx);
            instance.transform.position = new Vector3(0, 0, 4);
            var armature = instance.transform.Find("Armature").gameObject;
            var json = Json(LiveLinkRequest.Build(armature));
            Assert.That(Items(json, "renderers").Select(x => JsonReader.Str(x, "path") + "=" + JsonReader.Str(x, "node")), Is.EqualTo(new[] { "Upper/Lower/Hat=Armature/Upper/Lower/Hat" }));
            Assert.That(Bone(json, ""), Is.Null);
            LocalIs(json, "Armature/Upper/Lower", new Vector3(-1, 0, 0), Quaternion.identity, Vector3.one);

            // 根の子が 1 つだけで畳まれた FBX: その子の回転は Unity ではプレハブの根に移る。相手がその根なら送らない（単位を送ると回転が消える）、
            // 相手の下に置いたなら、その回転を含む相手に対する値
            var scene = new FbxAscii.Scene
            {
                Nodes = { new FbxAscii.Node("Armature", null, new double[] { 0, 0, 0 }, false) { Rotation = new double[] { -90, 0, 0 } }, new FbxAscii.Node("Body", 0, new double[] { 0, 0, 0 }, false) },
                Materials = { "Skin" },
            };
            var tube = FbxAscii.BoxTube("Body", 1, 2, 1, 0.1, 3);
            tube.Materials = new List<int> { 0 };
            tube.PolygonMaterials = tube.PolygonMaterials.Select(_ => 0).ToList();
            scene.Meshes.Add(tube);
            string collapsed = scope.WriteFbx("Lying", scene);
            var alone = scope.Instantiate(collapsed);
            var rootRotation = alone.transform.localRotation;
            Assert.That(Quaternion.Angle(rootRotation, Quaternion.identity), Is.GreaterThan(1), "Unity moves the single top node's rotation to the prefab root");
            json = Json(LiveLinkRequest.Build(alone));
            Assert.That(Items(json, "refused"), Is.Empty);
            Assert.That(Bone(json, ""), Is.Null, "an identity here would lose the rotation in the standalone");

            var holder = scope.Own(new GameObject("Avatar"));
            var placed = scope.Instantiate(collapsed);
            placed.transform.SetParent(holder.transform, false);
            placed.transform.localPosition = new Vector3(0, 1, 0);
            json = Json(LiveLinkRequest.Build(holder));
            LocalIs(json, "", new Vector3(0, 1, 0), rootRotation, Vector3.one);
        }

        [Test]
        public void AReparentedBoneIsSentRelativeToItsParentInTheFbx()
        {
            string fbx = scope.WriteFbx("Arm", FbxAscii.ArmScene());
            var target = scope.Instantiate(fbx);
            PrefabUtility.UnpackPrefabInstance(target, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            var lower = target.transform.Find("Armature/Upper/Lower");
            var holder = new GameObject("Holder").transform;
            holder.SetParent(target.transform, false);
            holder.localPosition = new Vector3(5, 5, 5);
            holder.localRotation = Quaternion.Euler(0, 90, 0);
            lower.SetParent(holder, true); // ワールドの位置はそのまま
            lower.Rotate(0, 0, 45, Space.Self);
            var json = Json(LiveLinkRequest.Build(target));
            Assert.That(Items(json, "refused"), Is.Empty, "the skin's bones are found through the bones list");
            LocalIs(json, "Armature/Upper/Lower", new Vector3(-1, 0, 0), Quaternion.Euler(0, 0, 45), Vector3.one);
            // Lower の下の Hat は、Lower に当たる Transform の子から名前で見つかる
            LocalIs(json, "Armature/Upper/Lower/Hat", new Vector3(-0.5f, 0.2f, 0), Quaternion.identity, Vector3.one);
            Assert.That(Items(json, "renderers").Single(x => JsonReader.Str(x, "node") == "Armature/Upper/Lower/Hat")["path"], Is.EqualTo("Holder/Lower/Hat"));
        }

        [Test]
        public void SiblingsWithTheSameNameAreAmbiguousOnlyWhereNamesDecide()
        {
            string fbx = scope.WriteFbx("Arm", FbxAscii.ArmScene());
            // プレハブのインスタンス: 足した同じ名前の物は、対応が取れているので関係ない
            var instance = scope.Instantiate(fbx);
            var extra = new GameObject("Hat").transform;
            extra.SetParent(instance.transform.Find("Armature/Upper/Lower"), false);
            var json = Json(LiveLinkRequest.Build(instance));
            Assert.That(Items(json, "refused"), Is.Empty);

            // 展開した階層: メッシュのノードと同じ名前の兄弟があると、どちらとも決めない
            var unpacked = scope.Instantiate(fbx);
            PrefabUtility.UnpackPrefabInstance(unpacked, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            var hat = unpacked.transform.Find("Armature/Upper/Lower/Hat");
            var copy = UnityEngine.Object.Instantiate(hat.gameObject, hat.parent);
            copy.name = "Hat";
            json = Json(LiveLinkRequest.Build(unpacked));
            Assert.That(Items(json, "refused").Select(x => JsonReader.Str(x, "reason")).Distinct(), Is.EqualTo(new[] { LiveLinkReason.AmbiguousBone }));
            Assert.That(Items(json, "refused").Count, Is.EqualTo(2), "both renderers named Hat");
            Assert.That(Items(json, "renderers").Select(x => JsonReader.Str(x, "node")), Is.EqualTo(new[] { "ArmMesh" }));
            Assert.That(Bone(json, "Armature/Upper/Lower/Hat"), Is.Null, "an ambiguous node has no value");

            // 展開して、ボーンでないノードの名前を変えた: 見つからない
            var renamed = scope.Instantiate(fbx);
            PrefabUtility.UnpackPrefabInstance(renamed, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            renamed.transform.Find("Armature/Upper/Lower/Hat").name = "Cap";
            json = Json(LiveLinkRequest.Build(renamed));
            Assert.That(Items(json, "refused").Select(x => JsonReader.Str(x, "path") + ":" + JsonReader.Str(x, "reason")),
                Is.EqualTo(new[] { "Armature/Upper/Lower/Cap:" + LiveLinkReason.BoneNotFound }));
        }

        [Test]
        public void AMissingBoneRefusesItsRenderer()
        {
            string fbx = scope.WriteFbx("Arm", FbxAscii.ArmScene());
            var target = scope.Instantiate(fbx);
            PrefabUtility.UnpackPrefabInstance(target, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            var skin = target.GetComponentInChildren<SkinnedMeshRenderer>();
            var bones = skin.bones;
            bones[1] = null;
            skin.bones = bones;
            var json = Json(LiveLinkRequest.Build(target));
            Assert.That(Items(json, "refused").Select(x => JsonReader.Str(x, "path") + ":" + JsonReader.Str(x, "reason")), Is.EqualTo(new[] { "ArmMesh:" + LiveLinkReason.BoneNotFound }));
        }

        [Test]
        public void AFbxWithOneTopNodeIsAddressedBelowTheCollapsedRoot()
        {
            var scene = new FbxAscii.Scene
            {
                Nodes = { new FbxAscii.Node("Armature", null, new double[] { 0, 0, 0 }, false), new FbxAscii.Node("Upper", 0, new double[] { 0, 1, 0 }, true), new FbxAscii.Node("Body", 0, new double[] { 0, 0, 0 }, false) },
                Materials = { "Skin" },
            };
            var tube = FbxAscii.BoxTube("Body", 2, 2, 1, 0.1, 3);
            tube.Materials = new List<int> { 0 };
            tube.PolygonMaterials = tube.PolygonMaterials.Select(_ => 0).ToList();
            scene.Meshes.Add(tube);
            string collapsed = scope.WriteFbx("Single", scene);
            var json = Json(LiveLinkRequest.Build(scope.Instantiate(collapsed)));
            Assert.That(Items(json, "renderers").Select(x => JsonReader.Str(x, "node")), Is.EqualTo(new[] { "Body" }), "Unity drops the single top node from the paths");
            Assert.That(JsonReader.Bool(JsonReader.Obj(Items(json, "models")[0], "import"), "preserve_hierarchy"), Is.False);

            string kept = scope.WriteFbx("Kept", scene, mi => mi.preserveHierarchy = true);
            json = Json(LiveLinkRequest.Build(scope.Instantiate(kept)));
            Assert.That(Items(json, "renderers").Select(x => JsonReader.Str(x, "node")), Is.EqualTo(new[] { "Armature/Body" }));
            Assert.That(JsonReader.Bool(JsonReader.Obj(Items(json, "models")[0], "import"), "preserve_hierarchy"), Is.True);
        }

        [Test]
        public void BlendShapeWeightsAreSentByName()
        {
            string fbx = scope.WriteFbx("Arm", FbxAscii.ArmScene());
            var target = scope.Instantiate(fbx);
            var skin = target.GetComponentInChildren<SkinnedMeshRenderer>();
            skin.SetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex("Bend"), 35);
            var arm = Items(Json(LiveLinkRequest.Build(target)), "renderers").Single(x => JsonReader.Str(x, "path") == "ArmMesh");
            var shapes = JsonReader.Obj(arm, "blend_shapes");
            Assert.That(shapes.Keys, Is.EquivalentTo(new[] { "Thick", "Bend" }), "the channel names, as Unity imports them");
            Assert.That(JsonReader.Num(shapes, "Bend"), Is.EqualTo(35).Within(1e-4));
            Assert.That(JsonReader.Num(shapes, "Thick"), Is.EqualTo(skin.GetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex("Thick"))).Within(1e-4));
        }

        [Test]
        public void TheImportSettingsAreSent()
        {
            string fbx = scope.WriteFbx("Arm", FbxAscii.ArmScene(), mi =>
            {
                mi.globalScale = 2;
                mi.useFileScale = false;
                mi.bakeAxisConversion = true;
                mi.importBlendShapes = false;
            });
            var json = Json(LiveLinkRequest.Build(scope.Instantiate(fbx)));
            var import = JsonReader.Obj(Items(json, "models")[0], "import");
            Assert.That(JsonReader.Num(import, "global_scale"), Is.EqualTo(2));
            Assert.That(JsonReader.Bool(import, "use_file_scale"), Is.False);
            Assert.That(JsonReader.Bool(import, "bake_axis_conversion"), Is.True, "sent as it is; the standalone refuses it");
            Assert.That(JsonReader.Bool(import, "import_blend_shapes"), Is.False);
            var arm = Items(json, "renderers").Single(x => JsonReader.Str(x, "node") == "ArmMesh");
            Assert.That(JsonReader.Obj(arm, "blend_shapes"), Is.Empty);
        }

        [Test]
        public void MaterialValuesAndTextureFilesAreSentWithoutPixels()
        {
            string fbx = scope.WriteFbx("Arm", FbxAscii.ArmScene());
            var target = scope.Instantiate(fbx);
            var material = scope.CreateMaterial("Body");
            material.SetColor("_Color", new Color(0.25f, 0.5f, 0.75f, 1));
            material.SetFloat("_Glossiness", 0.3f);
            var main = scope.WritePng("body", Color.white);
            var normal = scope.WritePng("body_normal", new Color(0.5f, 0.5f, 1), ti => ti.textureType = TextureImporterType.NormalMap);
            var mask = scope.WritePng("body_mask", Color.gray, ti => ti.sRGBTexture = false);
            material.SetTexture("_MainTex", main);
            material.SetTextureScale("_MainTex", new Vector2(2, 3));
            material.SetTextureOffset("_MainTex", new Vector2(0.5f, 0.25f));
            material.SetTexture("_BumpMap", normal);
            material.SetTexture("_MetallicGlossMap", mask);
            var rt = new RenderTexture(4, 4, 0);
            try
            {
                material.SetTexture("_EmissionMap", rt);
                material.EnableKeyword("_NORMALMAP");
                var skin = target.GetComponentInChildren<SkinnedMeshRenderer>();
                skin.sharedMaterials = new[] { material, null };

                var json = Json(LiveLinkRequest.Build(target));
                var arm = Items(json, "renderers").Single(x => JsonReader.Str(x, "node") == "ArmMesh");
                var materials = Items(json, "materials");
                var indexes = JsonReader.Arr(arm, "materials").Select(x => (int)(double)x).ToArray();
                var body = materials[indexes[0]];
                Assert.That(JsonReader.Str(materials[indexes[1]], "key"), Is.EqualTo(LiveLinkRequest.NoMaterialKey), "a submesh without a material");
                Assert.That(JsonReader.Str(body, "key"), Is.EqualTo(LiveLinkRequest.MaterialKey(material)));
                Assert.That(JsonReader.Str(body, "name"), Is.EqualTo("Body"));
                var shader = JsonReader.Obj(body, "shader");
                Assert.That(JsonReader.Str(shader, "name"), Is.EqualTo("Standard"));
                Assert.That(JsonReader.Str(shader, "package"), Is.Empty, "a built-in shader is in no package");
                Assert.That(JsonReader.Str(shader, "version"), Is.Empty);
                Assert.That(JsonReader.Arr(shader, "keywords"), Does.Contain("_NORMALMAP"));
                Assert.That(JsonReader.Num(shader, "render_queue"), Is.EqualTo(material.renderQueue));
                var values = JsonReader.Obj(body, "values");
                AreClose(Floats(JsonReader.Obj(values, "colors")["_Color"]), new[] { 0.25f, 0.5f, 0.75f, 1f }, "_Color");
                Assert.That(JsonReader.Num(JsonReader.Obj(values, "floats"), "_Glossiness"), Is.EqualTo(0.3).Within(1e-6));
                var textures = Items(body, "textures").ToDictionary(x => JsonReader.Str(x, "property"));
                Assert.That(textures.Keys, Is.EquivalentTo(new[] { "_MainTex", "_BumpMap", "_MetallicGlossMap", "_EmissionMap" }));
                var emission = textures["_EmissionMap"];
                Assert.That(emission.ContainsKey("path") && emission["path"] == null, Is.True, "the RenderTexture has no file: its path is null");
                Assert.That(emission.ContainsKey("guid"), Is.False);
                Assert.That(JsonReader.Bool(emission, "srgb"), Is.EqualTo(rt.sRGB));
                Assert.That(JsonReader.Bool(emission, "normal_map"), Is.False);
                var mainTex = textures["_MainTex"];
                Assert.That(JsonReader.Str(mainTex, "path"), Is.EqualTo(LiveLinkTestScope.Absolute(AssetDatabase.GetAssetPath(main))));
                Assert.That(JsonReader.Str(mainTex, "guid"), Is.EqualTo(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(main))));
                Assert.That(JsonReader.Bool(mainTex, "srgb"), Is.True);
                Assert.That(JsonReader.Bool(mainTex, "normal_map"), Is.False);
                AreClose(Floats(mainTex["scale"]), new[] { 2f, 3f }, "scale");
                AreClose(Floats(mainTex["offset"]), new[] { 0.5f, 0.25f }, "offset");
                Assert.That(JsonReader.Bool(textures["_BumpMap"], "normal_map"), Is.True);
                Assert.That(JsonReader.Bool(textures["_BumpMap"], "srgb"), Is.False, "a normal map is read as linear");
                Assert.That(JsonReader.Bool(textures["_MetallicGlossMap"], "srgb"), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(rt); }
        }

        [Test]
        public void APackageFileIsSentByItsRealPath()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(LiveLinkRequest).Assembly);
            Assert.That(LiveLinkFbxMap.PhysicalPath("Packages/" + info.name + "/package.json"), Is.EqualTo(LiveLinkSettings.Slash(System.IO.Path.GetFullPath(System.IO.Path.Combine(info.resolvedPath, "package.json")))));
            var package = LiveLinkMaterialValues.PackageOf("Packages/" + info.name + "/package.json");
            Assert.That(package?.name, Is.EqualTo(info.name));
            Assert.That(package?.version, Is.EqualTo(info.version));
            Assert.That(LiveLinkMaterialValues.PackageOf("Assets/x.shader"), Is.Null);
        }

        [Test]
        public void TheJsonWriterAndReaderAgree()
        {
            var w = new JsonWriter();
            w.BeginObject().Name("s").String("a\"b\\c\n\u0001é").Name("n").Number(0.1f).Name("z").Number(-0f).Name("i").Int(-3).Name("b").Bool(true).Name("x").Null()
                .Name("a").BeginArray().Int(1).BeginObject().EndObject().BeginArray().EndArray().EndArray().EndObject();
            var o = (Dictionary<string, object>)JsonReader.Parse(w.ToString());
            Assert.That(JsonReader.Str(o, "s"), Is.EqualTo("a\"b\\c\n\u0001é"));
            Assert.That((float)JsonReader.Num(o, "n").Value, Is.EqualTo(0.1f));
            Assert.That(w.ToString(), Does.Contain("\"z\":0,"));
            Assert.That(JsonReader.Num(o, "i"), Is.EqualTo(-3));
            Assert.That(JsonReader.Bool(o, "b"), Is.True);
            Assert.That(o["x"], Is.Null);
            Assert.That(JsonReader.Arr(o, "a").Count, Is.EqualTo(3));
            Assert.Throws<ArgumentException>(() => new JsonWriter().Number(float.NaN));
            Assert.Throws<FormatException>(() => JsonReader.Parse("{\"a\":1,}"));
            Assert.Throws<FormatException>(() => JsonReader.Parse("[1] 2"));
            Assert.Throws<FormatException>(() => JsonReader.Parse(new string('[', 100) + new string(']', 100)));
        }
    }
}
