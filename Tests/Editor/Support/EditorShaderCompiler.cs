using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>このエディタのシェーダーコンパイラが組み込みのインクルードを解決できるか。devcontainer の GUI モードでは
    /// HLSLSupport.cginc すら開けず、セッション中に初めてコンパイルされるシェーダーごとにエラーがログされる。
    /// パッケージの不具合ではないので、その状態のときだけテストの扱いを変える。</summary>
    internal static class EditorShaderCompiler
    {
        static bool? broken;

        /// <summary>同期のコンパイルで判定する。非同期のままだと、作った直後はまだエラーが無く、壊れていても false になる。</summary>
        static bool IsBroken
        {
            get
            {
                if (broken.HasValue) return broken.Value;
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return (broken = false).Value;
                bool async = ShaderUtil.allowAsyncCompilation;
                ShaderUtil.allowAsyncCompilation = false;
                try { return (broken = Probe()).Value; }
                finally { ShaderUtil.allowAsyncCompilation = async; }
            }
        }

        static bool Probe()
        {
            var probe = ShaderUtil.CreateShaderAsset(
                "Shader \"Hidden/YoluPainter/Tests/IncludeProbe\" { SubShader { Pass { CGPROGRAM\n#pragma vertex vert_img\n#pragma fragment frag\n#include \"UnityCG.cginc\"\nfixed4 frag(v2f_img i) : SV_Target { return 1; }\nENDCG } } }", true);
            try { return ShaderUtil.ShaderHasError(probe); }
            finally { Object.DestroyImmediate(probe); }
        }

        /// <summary>シェーダーのコンパイルのエラー・警告のログか（壊れたエディターでインクルードが開けないときに出る物）。</summary>
        static readonly Regex ShaderLog = new Regex(@"^Shader (?:error|warning) in '|Couldn't open include file '[^']+\.(?:cginc|hlsl)'", RegexOptions.CultureInvariant);

        public static bool IsShaderLog(string message) => !string.IsNullOrEmpty(message) && ShaderLog.IsMatch(message);

        /// <summary>
        /// 壊れたエディターでだけ、シェーダーのコンパイルのエラーを許し、ほかのエラー・例外・Assert のログは集めて <see cref="IDisposable.Dispose"/> で
        /// テストを落とす（全部は許さない）。FBX の取り込みでできる Standard のマテリアルのように、
        /// シェーダーで描かない試験でも、セッションで最初のコンパイルのエラーが出ることがある。コンパイルは同期にして、エラーをこの間に出させる。
        /// 壊れていないエディターでは何もしない（いつもの LogAssert のまま）。
        /// </summary>
        public static ShaderErrorsOnly TolerateShaderErrorsOnlyIfBroken() => new ShaderErrorsOnly();

        /// <summary>シェーダーのエラーだけを許している間。ログの確かめはテストの段（SetUp・本体・TearDown）ごとなので、本体の中でコンパイルが起きる前に
        /// <see cref="Reapply"/> を呼ぶ（SetUp で許しても本体には効かない。実測: SetUp でだけ許すと、本体の FBX の取り込みのエラーで落ちた）。</summary>
        internal sealed class ShaderErrorsOnly : IDisposable
        {
            readonly bool active;
            readonly bool async;
            readonly List<string> others = new List<string>();

            public ShaderErrorsOnly()
            {
                // 判定のためのコンパイル自体も、壊れていればエラーをログする。先に無視してから判定する
                LogAssert.ignoreFailingMessages = true;
                active = IsBroken;
                if (!active) { LogAssert.ignoreFailingMessages = false; return; }
                async = ShaderUtil.allowAsyncCompilation;
                ShaderUtil.allowAsyncCompilation = false;
                Application.logMessageReceived += Collect;
            }

            /// <summary>今の段でも、許す（壊れていないエディターでは何もしない）。</summary>
            public void Reapply()
            {
                if (active) LogAssert.ignoreFailingMessages = true;
            }

            void Collect(string message, string stack, LogType type)
            {
                if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
                if (IsShaderLog(message)) return;
                others.Add(type + ": " + message);
            }

            public void Dispose()
            {
                if (!active) return;
                Application.logMessageReceived -= Collect;
                ShaderUtil.allowAsyncCompilation = async;
                // ignoreFailingMessages は戻さない: テストの LogScope はテストの終わりに集めたログを確かめるので、ここで戻すと許したはずの
                // シェーダーのエラーで落ちる（LogScope はテストごとなので、次のテストには残らない）
                Assert.That(others, Is.Empty, "errors other than shader compile errors were logged");
            }
        }
    }
}
