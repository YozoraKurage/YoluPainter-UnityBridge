using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>GPU 経路に使ってよいシェーダーかの判定。Shader.isSupported はインクルード失敗などで
    /// パスが丸ごと除外されたシェーダーにも true を返し、そのまま使うとマゼンタを描くので、
    /// コンパイルエラーの有無も見る。</summary>
    internal static class ShaderHealth
    {
        public static bool IsUsable(Shader shader)
        { return shader != null && shader.isSupported && !ShaderUtil.ShaderHasError(shader); }
    }
}
