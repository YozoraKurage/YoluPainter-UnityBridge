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
        }
    }
}
