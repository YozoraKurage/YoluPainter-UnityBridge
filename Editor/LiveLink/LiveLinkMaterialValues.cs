using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// Live Link のマテリアルの値: 確かめた lilToon（インストールされたパッケージのシェーダー・確かめた版とバリアント・プロパティ。
    /// <see cref="PreviewMaterialBindings"/> が lilToon と決めたもの）のマテリアルの、プロパティの値（色・数・ベクトル・トグル・テクスチャの
    /// タイリングとオフセット）とキーワードを読み、スタンドアロンの 3D ビューが本物の値で描けるように送る。描いていないスロット
    /// （YoluPainter の流し込み先でない、スタンドアロンが描くテクスチャのプロパティ）の絵も、辺 <see cref="MaxEdge"/> まで縮めて、
    /// 1 回の送りで <see cref="Budget"/> バイトまで送る。
    /// 読むだけで、マテリアル・テクスチャ・インポート設定には書かない（絵は一時の RenderTexture に描いてから読む。元のテクスチャを
    /// 読めるようにしない）。送るのはスタンドアロンが機能の印（マテリアルの値）を持つときだけ（<see cref="LiveLinkBridge.FeatureMaterialValues"/>）。
    /// </summary>
    internal static class LiveLinkMaterialValues
    {
        /// <summary>送る絵の辺の上限（大きい絵は半分ずつ縮めて送る）。</summary>
        public const int MaxEdge = 2048;
        /// <summary>1 回の送り（モデルを送ったとき・値が変わったとき）で送る絵の画素のバイト数の上限。</summary>
        public const long Budget = 64L << 20;

        /// <summary>スタンドアロンが描くテクスチャのスロット（yolu-app の look/liltoon.rs の SLOTS と同じ）。</summary>
        public static readonly string[] Slots =
        {
            "_MainTex", "_MainColorAdjustMask", "_AlphaMask", "_BumpMap", "_ShadowStrengthMask", "_ShadowBorderMask", "_ShadowBlurMask",
            "_ShadowColorTex", "_Shadow2ndColorTex", "_Shadow3rdColorTex", "_EmissionMap", "_EmissionBlendMask", "_Emission2ndMap",
            "_Emission2ndBlendMask", "_MatCapTex", "_MatCapBlendMask", "_MatCap2ndTex", "_MatCap2ndBlendMask", "_RimColorTex", "_OutlineTex",
            "_OutlineWidthMask",
        };

        /// <summary>値を送るマテリアルか（確かめた lilToon で、Live Link の流し込みの決まりが lilToon と決めたもの）。似た名前のシェーダーは入らない。</summary>
        public static bool IsVerifiedLilToon(PreviewMaterialBinding binding) =>
            binding != null && binding.Kind == PreviewMaterialKind.LilToon && binding.LilToon != null && binding.LilToon.IsApplicable && binding.Source != null;

        /// <summary>プロパティ 1 つ（型は 0 Float・1 Int・2 Color・3 Vector）。</summary>
        internal readonly struct Property
        {
            public readonly string Name; public readonly int Type; public readonly Vector4 Value;
            public Property(string name, int type, Vector4 value) { Name = name; Type = type; Value = value; }
        }

        /// <summary>描いていないスロット 1 つ（テクスチャが入っていなければ <see cref="Texture"/> は null）。</summary>
        internal sealed class Slot
        {
            public string Name; public Texture Texture; public bool Srgb;
            /// <summary>中身の同一性（インスタンス・中身のハッシュ・大きさ・sRGB）。前に送ったものと同じなら送り直さない。</summary>
            public ulong Identity;
        }

        /// <summary>マテリアルから読んだもの（送る前の写し）。</summary>
        internal sealed class Snapshot
        {
            public string Shader, Source;
            public readonly List<Property> Properties = new List<Property>();
            public readonly List<string> Keywords = new List<string>();
            public readonly List<Slot> Slots = new List<Slot>();
            /// <summary>値の全部のハッシュ（変わったかを見る）。</summary>
            public ulong Hash;
        }

        /// <summary>テクスチャが sRGB として読まれるか。</summary>
        static bool IsSrgb(Texture t) => t is RenderTexture rt ? rt.sRGB : GraphicsFormatUtility.IsSRGBFormat(t.graphicsFormat);

        static ulong IdentityOf(Texture t)
        {
            if (t == null) return 0;
            var h = new Hasher();
            h.Add(t.GetInstanceID()); h.Add(t.width); h.Add(t.height); h.Add(IsSrgb(t) ? 1 : 0); h.Add(t.imageContentsHash.GetHashCode()); h.Add((int)t.updateCount);
            return h.Value;
        }

        /// <summary>すばやく変化を見る鍵（マテリアルの変更の数・シェーダー・描いていないスロットのテクスチャ）。0.2 秒ごとに取るので、プロパティの値は読まない。</summary>
        public static ulong QuickKey(Material m, IReadOnlyCollection<string> routed)
        {
            var h = new Hasher();
            h.Add(EditorUtility.GetDirtyCount(m));
            var shader = m.shader;
            h.Add(shader != null ? shader.GetInstanceID() : 0);
            if (shader == null) return h.Value;
            foreach (var name in Slots)
            {
                if (routed.Contains(name) || !m.HasTexture(name)) continue;
                var t = m.GetTexture(name);
                h.Add(name.GetHashCode()); h.Add((int)(IdentityOf(t) & 0xffffffff)); h.Add((int)(IdentityOf(t) >> 32));
            }
            return h.Value;
        }

        /// <summary>マテリアルの値を読む（書かない）。<paramref name="routed"/> は Live Link がスタンドアロンの描いた絵を見せるプロパティ（Color の
        /// 流し込み先。そのスロットの絵は送らない）。</summary>
        public static Snapshot Read(Material m, PreviewMaterialBinding binding, IReadOnlyCollection<string> routed)
        {
            var shader = m.shader;
            var s = new Snapshot { Shader = shader != null ? shader.name : "", Source = binding?.Summary ?? "" };
            var h = new Hasher();
            h.Add(s.Shader.GetHashCode());
            if (shader != null)
            {
                int n = shader.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    string name = shader.GetPropertyName(i);
                    Property p;
                    switch (shader.GetPropertyType(i))
                    {
                        case ShaderPropertyType.Color: { var c = m.GetColor(name); p = new Property(name, 2, new Vector4(c.r, c.g, c.b, c.a)); break; }
                        case ShaderPropertyType.Vector: p = new Property(name, 3, m.GetVector(name)); break;
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range: p = new Property(name, 0, new Vector4(m.GetFloat(name), 0, 0, 0)); break;
                        case ShaderPropertyType.Int: p = new Property(name, 1, new Vector4(m.GetInteger(name), 0, 0, 0)); break;
                        case ShaderPropertyType.Texture:
                            if ((shader.GetPropertyFlags(i) & ShaderPropertyFlags.NoScaleOffset) != 0 || shader.GetPropertyTextureDimension(i) != TextureDimension.Tex2D) continue;
                            var scale = m.GetTextureScale(name); var offset = m.GetTextureOffset(name);
                            p = new Property(name + "_ST", 3, new Vector4(scale.x, scale.y, offset.x, offset.y));
                            break;
                        default: continue;
                    }
                    if (!IsFinite(p.Value)) continue;
                    s.Properties.Add(p);
                    h.Add(p.Name.GetHashCode()); h.Add(p.Type); h.Add(p.Value.x); h.Add(p.Value.y); h.Add(p.Value.z); h.Add(p.Value.w);
                }
                foreach (var k in m.shaderKeywords.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (string.IsNullOrEmpty(k) || k.Contains(' ') || s.Keywords.Contains(k)) continue;
                    s.Keywords.Add(k); h.Add(k.GetHashCode());
                }
                foreach (var name in Slots)
                {
                    if (routed.Contains(name) || !m.HasTexture(name)) continue;
                    var t = m.GetTexture(name);
                    var slot = new Slot { Name = name, Texture = t, Srgb = t != null && IsSrgb(t), Identity = IdentityOf(t) };
                    s.Slots.Add(slot);
                    h.Add(name.GetHashCode()); h.Add((int)(slot.Identity & 0xffffffff)); h.Add((int)(slot.Identity >> 32));
                }
            }
            s.Hash = h.Value;
            return s;
        }

        static bool IsFinite(Vector4 v) => !(float.IsNaN(v.x) || float.IsInfinity(v.x) || float.IsNaN(v.y) || float.IsInfinity(v.y) ||
                                             float.IsNaN(v.z) || float.IsInfinity(v.z) || float.IsNaN(v.w) || float.IsInfinity(v.w));

        /// <summary>送る大きさ（辺が <see cref="MaxEdge"/> に収まるまで半分にする）。</summary>
        public static Vector2Int SendSize(Texture t) => SendSize(t.width, t.height);

        /// <summary>幅 × 高さの絵を送る大きさ（両方の辺を、長い辺が <see cref="MaxEdge"/> に収まるまで半分にする。奇数は切り捨て、1 より小さくしない）。</summary>
        public static Vector2Int SendSize(int width, int height)
        {
            int w = Math.Max(1, width), h = Math.Max(1, height);
            while (w > MaxEdge || h > MaxEdge) { w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); }
            return new Vector2Int(w, h);
        }

        /// <summary>テクスチャを一時の RenderTexture に描いて、RGBA8（行は下から）で読む。元のテクスチャには書かない（読めるようにも
        /// しない）。大きい絵は半分ずつ縮める。sRGB の絵は sRGB の RenderTexture に描くので、読んだ値はガンマのまま。読めなければ null。</summary>
        public static byte[] ReadPixels(Texture texture, int width, int height, bool srgb)
        {
            if (texture == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || texture.dimension != TextureDimension.Tex2D) return null;
            var format = srgb ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
            var active = RenderTexture.active;
            var temporaries = new List<RenderTexture>();
            Texture2D read = null;
            try
            {
                Texture source = texture;
                int w = texture.width, h = texture.height;
                // 大きく縮めるときは半分ずつ（1 回の双線形で縮めると、間の画素を飛ばす）
                while (w / 2 >= width && h / 2 >= height && (w > width || h > height))
                {
                    w = Math.Max(width, w / 2); h = Math.Max(height, h / 2);
                    var half = RenderTexture.GetTemporary(new RenderTextureDescriptor(w, h, format, 0) { msaaSamples = 1 });
                    temporaries.Add(half);
                    Graphics.Blit(source, half);
                    source = half;
                }
                var target = RenderTexture.GetTemporary(new RenderTextureDescriptor(width, height, format, 0) { msaaSamples = 1 });
                temporaries.Add(target);
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                read = new Texture2D(width, height, format, TextureCreationFlags.None) { hideFlags = HideFlags.HideAndDontSave };
                read.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                var bytes = read.GetRawTextureData<byte>().ToArray();
                return bytes.Length == width * height * 4 ? bytes : null;
            }
            catch (Exception) { return null; }
            finally
            {
                RenderTexture.active = active;
                foreach (var t in temporaries) RenderTexture.ReleaseTemporary(t);
                if (read != null) Object.DestroyImmediate(read);
            }
        }

        /// <summary>送った結果。</summary>
        internal struct Sent
        {
            /// <summary>1 = 送った、0 = スタンドアロンに印が無いので送らない、負は失敗。</summary>
            public int Result;
            public int Textures; public long Bytes;
        }

        /// <summary>写しを送る: 値（プロパティ・キーワード・スロットの様子）を送ってから、来ると言った絵を送る。<paramref name="sentSlots"/> は
        /// このマテリアルに前に送った絵の同一性（同じなら「前と同じ」で送り直さない。送ったら書き換える）、<paramref name="budget"/> は残りの
        /// 絵のバイト数（送った分を引く）。</summary>
        public static Sent Send(ulong handle, int material, Snapshot s, Dictionary<string, ulong> sentSlots, ref long budget)
        {
            var result = new Sent();
            if (LiveLinkBridge.ValuesBegin(handle, material, true, s.Shader, s.Source) < 0) { result.Result = -1; return result; }
            foreach (var p in s.Properties)
            {
                // 名前の決まりに合わないもの（長すぎる・制御文字）はブリッジが断る。そのプロパティだけを落とす
                switch (p.Type)
                {
                    case 0: LiveLinkBridge.ValuesFloat(handle, p.Name, p.Value.x); break;
                    case 1: LiveLinkBridge.ValuesInt(handle, p.Name, (int)p.Value.x); break;
                    case 2: LiveLinkBridge.ValuesColor(handle, p.Name, new Color(p.Value.x, p.Value.y, p.Value.z, p.Value.w)); break;
                    default: LiveLinkBridge.ValuesVector(handle, p.Name, p.Value); break;
                }
            }
            foreach (var k in s.Keywords) LiveLinkBridge.ValuesKeyword(handle, k);
            var follow = new List<(Slot Slot, byte[] Pixels, Vector2Int Size)>();
            var states = new Dictionary<string, LiveLinkSlotState>();
            foreach (var slot in s.Slots)
            {
                LiveLinkSlotState state;
                var t = slot.Texture;
                int w = t != null ? t.width : 0, h = t != null ? t.height : 0;
                if (t == null) state = LiveLinkSlotState.Empty;
                else if (sentSlots.TryGetValue(slot.Name, out ulong id) && id == slot.Identity) state = LiveLinkSlotState.Unchanged;
                else
                {
                    var size = SendSize(t);
                    long bytes = (long)size.x * size.y * 4;
                    if (bytes > budget) state = LiveLinkSlotState.OverBudget;
                    else
                    {
                        var pixels = ReadPixels(t, size.x, size.y, slot.Srgb);
                        if (pixels == null) state = LiveLinkSlotState.Unreadable;
                        else { state = LiveLinkSlotState.Follows; budget -= bytes; follow.Add((slot, pixels, size)); }
                    }
                }
                states[slot.Name] = state;
                LiveLinkBridge.ValuesSlot(handle, slot.Name, state, w, h);
            }
            result.Result = LiveLinkBridge.ValuesSend(handle);
            if (result.Result != 1) return result;
            foreach (var (slot, pixels, size) in follow)
            {
                if (LiveLinkBridge.TextureSend(handle, material, slot.Name, size.x, size.y, slot.Srgb, pixels) == 1)
                {
                    sentSlots[slot.Name] = slot.Identity;
                    result.Textures++; result.Bytes += pixels.Length;
                }
                else sentSlots.Remove(slot.Name);
            }
            foreach (var kv in states)
                if (kv.Value != LiveLinkSlotState.Follows && kv.Value != LiveLinkSlotState.Unchanged) sentSlots.Remove(kv.Key);
            return result;
        }

        /// <summary>「値なし」を送る（lilToon でなくなったマテリアル。スタンドアロンは前の値を捨てる）。</summary>
        public static int SendNone(ulong handle, int material)
        {
            if (LiveLinkBridge.ValuesBegin(handle, material, false, "", "") < 0) return -1;
            return LiveLinkBridge.ValuesSend(handle);
        }

        /// <summary>64 ビットの FNV-1a（変化を見るだけ。保存しない）。</summary>
        struct Hasher
        {
            ulong value; bool started;
            public ulong Value => started ? value : 14695981039346656037UL;
            public void Add(int x) { Mix((uint)x); }
            public void Add(float x) { Mix((uint)BitConverter.SingleToInt32Bits(x)); }
            void Mix(uint x)
            {
                if (!started) { value = 14695981039346656037UL; started = true; }
                for (int i = 0; i < 4; i++) { value ^= (x >> (i * 8)) & 0xff; value *= 1099511628211UL; }
            }
        }
    }
}
