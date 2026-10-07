using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// Live Link の頼みに入れるマテリアルの値: シェーダーの身元（名前・アセットの GUID・入っているパッケージの名前と版・キーワード・描画の順）、
    /// プロパティの値（数・整数・色・ベクトル。有限のものだけ）、2D のテクスチャのプロパティごとの絵のファイル（絶対の道・GUID・sRGB・ノーマルマップか・
    /// 拡大とずらし）。絵の画素は読まない（スタンドアロンがファイルを読む）。マテリアル・テクスチャ・インポート設定には書かない。
    /// Unity の中にしかない絵（ファイルの無い生成物・RenderTexture・ほかのアセットの中の絵）は、道を空にして、スロットの身元と sRGB だけを入れる。
    /// どのシェーダーでも集める（lilToon として扱うかはスタンドアロンが決める）。
    /// </summary>
    internal static class LiveLinkMaterialValues
    {
        /// <summary>テクスチャのプロパティ 1 つの絵のファイル（<see cref="Path"/> が null なら、Unity の中にしかない絵）。</summary>
        internal sealed class TextureFile
        {
            public string Property, Path, Guid = "";
            public bool Srgb, NormalMap;
            public Vector2 Scale, Offset;
        }

        /// <summary>マテリアルから読んだもの。</summary>
        internal sealed class Snapshot
        {
            public string ShaderName = "", ShaderGuid = "", ShaderPackage = "", ShaderVersion = "";
            public int RenderQueue;
            public readonly List<string> Keywords = new List<string>();
            public readonly List<KeyValuePair<string, float>> Floats = new List<KeyValuePair<string, float>>();
            public readonly List<KeyValuePair<string, int>> Ints = new List<KeyValuePair<string, int>>();
            public readonly List<KeyValuePair<string, Color>> Colors = new List<KeyValuePair<string, Color>>();
            public readonly List<KeyValuePair<string, Vector4>> Vectors = new List<KeyValuePair<string, Vector4>>();
            public readonly List<TextureFile> Textures = new List<TextureFile>();
        }

        public static Snapshot Read(Material m)
        {
            var s = new Snapshot();
            if (m == null) return s;
            var shader = m.shader;
            s.RenderQueue = m.renderQueue;
            if (shader == null) return s;
            s.ShaderName = shader.name ?? "";
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(shader, out string guid, out long _)) s.ShaderGuid = guid ?? "";
            var package = PackageOf(AssetDatabase.GetAssetPath(shader));
            s.ShaderPackage = package?.name ?? "";
            s.ShaderVersion = package?.version ?? "";
            foreach (var k in m.shaderKeywords.OrderBy(k => k, StringComparer.Ordinal))
                if (!string.IsNullOrEmpty(k) && k.IndexOf(' ') < 0 && !s.Keywords.Contains(k)) s.Keywords.Add(k);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int n = shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                string name = shader.GetPropertyName(i);
                if (!seen.Add(name)) continue;
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                    {
                        float v = m.GetFloat(name);
                        if (Finite(v)) s.Floats.Add(new KeyValuePair<string, float>(name, v));
                        break;
                    }
                    case ShaderPropertyType.Int:
                        s.Ints.Add(new KeyValuePair<string, int>(name, m.GetInteger(name)));
                        break;
                    case ShaderPropertyType.Color:
                    {
                        var c = m.GetColor(name);
                        if (Finite(c.r) && Finite(c.g) && Finite(c.b) && Finite(c.a)) s.Colors.Add(new KeyValuePair<string, Color>(name, c));
                        break;
                    }
                    case ShaderPropertyType.Vector:
                    {
                        var v = m.GetVector(name);
                        if (Finite(v.x) && Finite(v.y) && Finite(v.z) && Finite(v.w)) s.Vectors.Add(new KeyValuePair<string, Vector4>(name, v));
                        break;
                    }
                    case ShaderPropertyType.Texture:
                    {
                        if (shader.GetPropertyTextureDimension(i) != TextureDimension.Tex2D) break;
                        var t = m.GetTexture(name);
                        if (t == null) break;
                        var file = FileOf(t) ?? NotAFile(t);
                        file.Property = name;
                        var scale = m.GetTextureScale(name); var offset = m.GetTextureOffset(name);
                        file.Scale = Finite(scale.x) && Finite(scale.y) ? scale : Vector2.one;
                        file.Offset = Finite(offset.x) && Finite(offset.y) ? offset : Vector2.zero;
                        s.Textures.Add(file);
                        break;
                    }
                }
            }
            return s;
        }

        /// <summary>テクスチャの絵のファイル（画像のファイルから取り込んだアセットの本体でなければ null）。</summary>
        public static TextureFile FileOf(Texture t)
        {
            if (t == null || t is RenderTexture || !(t is Texture2D)) return null;
            string assetPath = AssetDatabase.GetAssetPath(t);
            if (string.IsNullOrEmpty(assetPath) || !AssetDatabase.IsMainAsset(t)) return null;
            var importer = AssetImporter.GetAtPath(assetPath);
            bool srgb, normal = false;
            switch (importer)
            {
                case TextureImporter ti:
                    srgb = ti.sRGBTexture;
                    normal = ti.textureType == TextureImporterType.NormalMap;
                    // ノーマルマップは sRGB の印に関わらずリニアで読む
                    if (normal) srgb = false;
                    break;
                case IHVImageFormatImporter _:
                    srgb = GraphicsFormatUtility.IsSRGBFormat(t.graphicsFormat);
                    break;
                default:
                    return null; // .asset などの中の絵（ファイルは画像でない）
            }
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(t, out string guid, out long _);
            return new TextureFile { Path = LiveLinkFbxMap.PhysicalPath(assetPath), Guid = guid ?? "", Srgb = srgb, NormalMap = normal };
        }

        /// <summary>ファイルの無い絵（道は null）: sRGB は絵の形式から、ノーマルマップかは分からないので偽。</summary>
        static TextureFile NotAFile(Texture t)
        {
            bool srgb = t is RenderTexture rt ? rt.sRGB : GraphicsFormatUtility.IsSRGBFormat(t.graphicsFormat);
            return new TextureFile { Path = null, Srgb = srgb };
        }

        /// <summary>アセットが入っているパッケージ（Assets の中・組み込み・見つからなければ null）。</summary>
        public static PackageInfo PackageOf(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Packages/", StringComparison.Ordinal)) return null;
            try { return PackageInfo.FindForAssetPath(assetPath); }
            catch (ArgumentException) { return null; }
        }

        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
