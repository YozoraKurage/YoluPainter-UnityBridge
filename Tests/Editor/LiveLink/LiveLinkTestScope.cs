using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.LiveLink;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// Live Link の試験の場: 受け渡しのフォルダと頼みの覚えを一時のフォルダへ差し替え、窓の状態を消し、言語を英語にする。試験の FBX・PNG・マテリアルは
    /// テストプロジェクトの一時のフォルダ（<c>Assets/ZZ_LiveLinkTests_…</c>）に書いて取り込み、終わったら消す。置く物はプレビューのシーンに置く
    /// （シーンビューに描かれない）。シェーダー・GPU には頼らない。壊れたエディター（GUI のモード）では、シェーダーのコンパイルのエラーだけを許す。
    /// </summary>
    internal sealed class LiveLinkTestScope : IDisposable
    {
        public readonly string Root;          // 一時のフォルダ（OS の）
        public readonly LiveLinkFolder Folder;
        public readonly string AssetFolder;   // Assets/ZZ_LiveLinkTests_…
        public readonly Scene Scene = EditorSceneManager.NewPreviewScene();
        readonly List<Object> owned = new List<Object>();
        readonly Action<LiveLinkImport.Prepared> presenter;
        readonly Action<LiveLinkReply> handle;
        public readonly List<LiveLinkImport.Prepared> Presented = new List<LiveLinkImport.Prepared>();

        readonly EditorShaderCompiler.ShaderErrorsOnly shaderErrors;

        public LiveLinkTestScope()
        {
            // FBX の取り込みでできる Standard のマテリアルが、壊れたエディター（GUI のモード）でセッションの最初にコンパイルのエラーを出す。それだけを許す
            shaderErrors = EditorShaderCompiler.TolerateShaderErrorsOnlyIfBroken();
            L.OverrideLanguage(PainterLanguage.English);
            Root = Path.Combine(Path.GetTempPath(), "yolupainter-livelink-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            LiveLinkFolder.Override = Path.Combine(Root, "LiveLink");
            Folder = LiveLinkFolder.Current();
            LiveLinkLedger.PathOverride = Path.Combine(Root, "requests.tsv");
            LiveLinkLedger.Reload();
            LiveLinkState.Clear();
            LiveLinkOpen.ForgetLaunch();
            presenter = LiveLinkImport.Presenter;
            LiveLinkImport.Presenter = p => Presented.Add(p);
            handle = LiveLinkReplies.Handle;
            LiveLinkReplies.Handle = LiveLinkReplies.Apply;
            AssetFolder = "Assets/ZZ_LiveLinkTests_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            AssetDatabase.CreateFolder("Assets", AssetFolder.Substring("Assets/".Length));
        }

        /// <summary>今のテストの段でも、壊れたエディターのシェーダーのエラーを許す（本体の中で取り込み・描画の前に）。</summary>
        public void TolerateShaderErrors() => shaderErrors.Reapply();

        public void Dispose()
        {
            try
            {
                foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
                owned.Clear();
                EditorSceneManager.ClosePreviewScene(Scene);
                AssetDatabase.DeleteAsset(AssetFolder);
            }
            finally
            {
                LiveLinkImport.Presenter = presenter;
                LiveLinkReplies.Handle = handle;
                LiveLinkFolder.Override = null;
                LiveLinkLedger.PathOverride = null;
                LiveLinkLedger.Reload();
                LiveLinkState.Clear();
                LiveLinkOpen.ForgetLaunch();
                L.OverrideLanguage(null);
                try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                shaderErrors.Dispose();
            }
        }

        /// <summary>終わったら消す。根の GameObject はプレビューのシーンへ移す（作った直後に呼ぶ）。</summary>
        public T Own<T>(T o) where T : Object
        {
            shaderErrors.Reapply();
            if (o is GameObject go && go.transform.parent == null && go.scene != Scene) SceneManager.MoveGameObjectToScene(go, Scene);
            owned.Add(o);
            return o;
        }

        /// <summary>プロジェクトの根からの道を OS の絶対の道（区切りは /）に。</summary>
        public static string Absolute(string assetPath) => LiveLinkSettings.Slash(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath)));

        /// <summary>FBX から来ていないメッシュ（組み込みの立方体）のレンダラー。マテリアルは付けない（GameObject.CreatePrimitive は組み込みの Standard を
        /// 付け、GUI のモードではそのシェーダーのコンパイルのエラーが後の試験に出る）。</summary>
        public GameObject NotFromFbx(string name)
        {
            var go = Own(new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer)));
            go.GetComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            return go;
        }

        /// <summary>FBX を書いて取り込む（取り込みの設定を変えるなら <paramref name="configure"/>）。返すのはアセットの道。</summary>
        public string WriteFbx(string name, FbxAscii.Scene scene, Action<ModelImporter> configure = null)
        {
            shaderErrors.Reapply();
            string path = AssetFolder + "/" + name + ".fbx";
            File.WriteAllText(Absolute(path), scene.ToAscii());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (configure != null)
            {
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                configure(importer);
                importer.SaveAndReimport();
            }
            return path;
        }

        /// <summary>取り込んだ FBX をシーンに置く（プレハブのインスタンス）。</summary>
        public GameObject Instantiate(string fbxPath)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (model == null) throw new InvalidOperationException("The FBX was not imported: " + fbxPath);
            return Own((GameObject)PrefabUtility.InstantiatePrefab(model, Scene));
        }

        /// <summary>単色の PNG を書いて取り込む（sRGB・種類を変えるなら <paramref name="configure"/>）。</summary>
        public Texture2D WritePng(string name, Color color, Action<TextureImporter> configure = null)
        {
            shaderErrors.Reapply();
            string path = AssetFolder + "/" + name + ".png";
            File.WriteAllBytes(Absolute(path), Png(color));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (configure != null)
            {
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                configure(importer);
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        public static byte[] Png(Color color)
        {
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color[16];
                for (int i = 0; i < 16; i++) pixels[i] = color;
                t.SetPixels(pixels); t.Apply();
                return t.EncodeToPNG();
            }
            finally { Object.DestroyImmediate(t); }
        }

        /// <summary>マテリアルのアセットを作る（シェーダーは組み込みの Standard。描かないので、コンパイルされなくてよい）。</summary>
        public Material CreateMaterial(string name)
        {
            shaderErrors.Reapply();
            var m = new Material(Shader.Find("Standard")) { name = name };
            AssetDatabase.CreateAsset(m, AssetFolder + "/" + name + ".mat");
            return m;
        }

        /// <summary>アセットにしないマテリアル（プレビューのシーンの物と同じ、GlobalObjectId が空の物）。終わったら消す。</summary>
        public Material CreateSceneMaterial(string name)
        {
            shaderErrors.Reapply();
            return Own(new Material(Shader.Find("Standard")) { name = name });
        }

        /// <summary>返事を outbox に置く（スタンドアロンの代わり。.tmp から置き換える）。</summary>
        public string PutReply(string request, int n, string json)
        {
            shaderErrors.Reapply();
            Folder.Ensure();
            string path = Path.Combine(Folder.Outbox, request + "-" + n + ".json");
            LiveLinkFolder.WriteReplacing(path, json);
            return path;
        }
    }
}
