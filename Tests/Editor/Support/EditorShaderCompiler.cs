using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>このエディタのシェーダーコンパイラが組み込みのインクルードを解決できるか。devcontainer の GUI モードでは
    /// HLSLSupport.cginc すら開けず、セッション中に初めてコンパイルされるシェーダーごとにエラーがログされる。
    /// パッケージの不具合ではないので、その状態のときだけテストの扱いを変える。</summary>
    internal static class EditorShaderCompiler
    {
        static bool? broken;

        public static bool IsBroken
        {
            get
            {
                if (broken.HasValue) return broken.Value;
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return (broken = false).Value;
                var probe = ShaderUtil.CreateShaderAsset(
                    "Shader \"Hidden/YoluPainter/Tests/IncludeProbe\" { SubShader { Pass { CGPROGRAM\n#pragma vertex vert_img\n#pragma fragment frag\n#include \"UnityCG.cginc\"\nfixed4 frag(v2f_img i) : SV_Target { return 1; }\nENDCG } } }", true);
                try { broken = ShaderUtil.ShaderHasError(probe); }
                finally { Object.DestroyImmediate(probe); }
                return broken.Value;
            }
        }

        /// <summary>壊れたエディタでは、描画のたびに出るシェーダーのエラーログでテストを落とさない。</summary>
        public static void TolerateErrorLogsIfBroken()
        {
            // 判定のためのコンパイル自体も、壊れていればエラーをログする。先に無視してから判定する。
            LogAssert.ignoreFailingMessages = true;
            if (!IsBroken) { LogAssert.ignoreFailingMessages = false; return; }
            // 非同期コンパイルだとエラーはテストが終わった後のフレームでログされ、無視の設定が外れた別の
            // テストを落とす。壊れたエディタでは同期コンパイルにして、エラーをそのテストの中で出させる。
            ShaderUtil.allowAsyncCompilation = false;
            WarmPackageShaders();
        }

        static bool warmed;
        /// <summary>壊れたエディタでは、シェーダーごとにセッションで最初のコンパイルの時だけエラーがログされる。テストの途中で
        /// 無視の設定が戻ることがあり（窓や資産の作り直しの後。実測）、再起動した直後の台では最初に表示の合成を使った試験が
        /// 落ちていた（2026-10-03、WindowResizeTests）。無視している今のうちに、パッケージのシェーダーを全部 1 回ずつ
        /// コンパイルさせてエラーを出し切る。</summary>
        static void WarmPackageShaders()
        {
            if (warmed) return;
            warmed = true;
            foreach (var guid in AssetDatabase.FindAssets("t:Shader", new[] { "Packages/net.yozolab.yolupainter" }))
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
                if (shader == null) continue;
                var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                try { for (int pass = 0; pass < material.passCount; pass++) material.SetPass(pass); }
                catch (System.Exception) { }
                finally { Object.DestroyImmediate(material); }
            }
        }
    }
}
