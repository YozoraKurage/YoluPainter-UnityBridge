using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>マテリアル表示に渡す、1 つのスロットの塗った中身。</summary>
    public sealed class PreviewSlotChannels
    {
        /// <summary>チャンネルの合成（straight RGBA8、左下原点。合成器の表示か CPU の合成を載せたもの）。使っていないチャンネルは入れない。
        /// Normal は合成ではなく <see cref="NormalOutput"/> を使う。</summary>
        public readonly Dictionary<PaintChannel, Texture> Composites = new Dictionary<PaintChannel, Texture>();
        /// <summary>Normal の出力（接空間・OpenGL の Y+・不透明）。Normal を使っていなければ null。</summary>
        public Texture NormalOutput;
    }

    /// <summary>
    /// マテリアル表示（<see cref="IsolatedModelPreview"/> の持ち物）: スロットごとに、元のマテリアルの複製（シェーダー・キーワード・値は元のまま。
    /// マテリアルの欄で変えた値を重ねる）を作り、対応（<see cref="PreviewMaterialBinding"/>）のあるチャンネルだけを、そのプロパティの読み方に
    /// 詰め直したテクスチャ（PreviewChannelPack.shader、スロットとプロパティごとの RenderTexture）で入れる。元のマテリアル・テクスチャには触れない。
    /// 元のマテリアルが変わった（dirty の回数・シェーダー）ら対応を決め直し、複製を合わせ直す。
    /// </summary>
    internal sealed class PreviewMaterialView : IDisposable
    {
        public const string PackShaderName = "Hidden/YoluPainter/PreviewChannelPack";
        sealed class Slot
        {
            public PreviewMaterialBinding Binding;
            public Material Display;
            public int SyncedDirty = -1, SyncedEdits = -1; public Shader SyncedShader;
            public readonly Dictionary<string, RenderTexture> Packed = new Dictionary<string, RenderTexture>();
            public string Reason;
        }
        readonly Func<int, Material> sourceOf;
        readonly List<Slot> slots = new List<Slot>();
        Material pack;
        bool packChecked; string packProblem;

        public PreviewMaterialView(Func<int, Material> sourceOf) { this.sourceOf = sourceOf ?? throw new ArgumentNullException(nameof(sourceOf)); }

        /// <summary>マテリアルの欄で変えた値（複製に重ねる）。null なら重ねない。</summary>
        public PreviewMaterialEdits Edits { get; set; }

        /// <summary>モデルを読み替えたとき: 複製と詰めたテクスチャを全部捨てる。</summary>
        public void Reset(int slotCount)
        {
            foreach (var s in slots) Release(s);
            slots.Clear();
            for (int i = 0; i < slotCount; i++) slots.Add(new Slot());
        }

        /// <summary>スロットの対応（元のマテリアルが変わっていれば決め直す）。範囲外は null。</summary>
        public PreviewMaterialBinding Binding(int slot)
        {
            if (slot < 0 || slot >= slots.Count) return null;
            var s = slots[slot]; var source = sourceOf(slot);
            if (s.Binding == null || s.Binding.Source != source || source != null && (s.Binding.SourceDirtyCount != UnityEditor.EditorUtility.GetDirtyCount(source) || s.Binding.ShaderId != (source.shader != null ? source.shader.GetInstanceID() : 0)))
                s.Binding = PreviewMaterialBindings.Resolve(source);
            return s.Binding;
        }

        /// <summary>マテリアル表示で使う複製（中立で見せるなら null。理由は <see cref="Reason"/>）。</summary>
        public Material Display(int slot) => slot >= 0 && slot < slots.Count && slots[slot].Reason == null ? slots[slot].Display : null;
        /// <summary>そのスロットを中立で見せる理由（マテリアルで見せていれば null）。</summary>
        public string Reason(int slot) => slot >= 0 && slot < slots.Count ? slots[slot].Reason : null;

        /// <summary>
        /// スロットの複製を用意して、塗った中身を入れる（中身が null なら元のテクスチャのまま）。マテリアルで見せられなければ複製を作らず、
        /// 理由を覚える（呼ぶ側はそのスロットを中立で見せる）。
        /// </summary>
        public void Bind(int slot, PreviewSlotChannels painted)
        {
            if (slot < 0 || slot >= slots.Count) return;
            var s = slots[slot]; var b = Binding(slot);
            if (b == null || !b.CanShow) { s.Reason = b?.Unusable ?? "No binding."; ReleaseDisplay(s); return; }
            bool needsPacking = painted != null && b.Channels.Any(c => c.Packing != PreviewPacking.Normal && painted.Composites.ContainsKey(c.Channel) && (c.Packing != PreviewPacking.Color || QualitySettings.activeColorSpace == ColorSpace.Linear));
            if (needsPacking && PackProblem() != null) { s.Reason = PackProblem(); ReleaseDisplay(s); return; }
            try
            {
                Sync(s, b.Source);
                foreach (var group in b.Channels.GroupBy(c => c.Property))
                {
                    // まず元のテクスチャとキーワードに戻し（使わなくなったチャンネルが残らないように）、塗った中身があれば入れる
                    s.Display.SetTexture(group.Key, b.Source.GetTexture(group.Key));
                    foreach (var k in group.SelectMany(c => c.Keywords).Distinct()) SetKeyword(s.Display, k, b.Source.IsKeywordEnabled(k));
                    Texture bound = painted == null ? null : Pack(s, b, group.Key, group.ToList(), painted);
                    if (bound == null) continue;
                    s.Display.SetTexture(group.Key, bound);
                    foreach (var k in group.Where(c => Has(painted, c)).SelectMany(c => c.Keywords).Distinct()) s.Display.EnableKeyword(k);
                }
                // マテリアルの欄で入れたキーワードは対応のキーワードより優先（複製を合わせ直したときに入っている）
                Edits?.ApplyTo(b.Source, s.Display);
                s.Reason = null;
            }
            catch (Exception ex) { s.Reason = L.Tr("The material view of this slot failed: {0}", ex.Message); ReleaseDisplay(s); }
        }

        static bool Has(PreviewSlotChannels painted, PreviewChannelBinding c) => c.Packing == PreviewPacking.Normal ? painted.NormalOutput != null : painted.Composites.ContainsKey(c.Channel);

        static void SetKeyword(Material m, string keyword, bool on) { if (on) m.EnableKeyword(keyword); else m.DisableKeyword(keyword); }

        /// <summary>複製を元のマテリアルに合わせる（作る・シェーダーの差し替え・元の値の変更・欄の変更のとき）。</summary>
        void Sync(Slot s, Material source)
        {
            int dirty = UnityEditor.EditorUtility.GetDirtyCount(source), edits = Edits?.Version ?? 0;
            if (s.Display == null)
            {
                s.Display = new Material(source) { hideFlags = HideFlags.HideAndDontSave, name = source.name + " (material preview only)" };
                s.SyncedDirty = dirty; s.SyncedEdits = -1; s.SyncedShader = source.shader;
            }
            if (s.SyncedShader != source.shader) { s.Display.shader = source.shader; s.SyncedDirty = -1; s.SyncedShader = source.shader; }
            if (s.SyncedDirty != dirty || s.SyncedEdits != edits)
            {
                s.Display.CopyPropertiesFromMaterial(source);
                Edits?.ApplyTo(source, s.Display);
                s.SyncedDirty = dirty; s.SyncedEdits = edits;
            }
        }

        /// <summary>1 つのプロパティに入れるテクスチャ（塗ったものが無ければ null）。</summary>
        Texture Pack(Slot s, PreviewMaterialBinding b, string property, List<PreviewChannelBinding> group, PreviewSlotChannels painted)
        {
            var first = group[0];
            bool gpu = first.Packing != PreviewPacking.Normal && (first.Packing != PreviewPacking.Color || QualitySettings.activeColorSpace == ColorSpace.Linear);
            if (gpu && PackProblem() != null) throw new InvalidOperationException(PackProblem());
            switch (first.Packing)
            {
                case PreviewPacking.Normal:
                    return painted.NormalOutput;
                case PreviewPacking.Color:
                {
                    if (!painted.Composites.TryGetValue(first.Channel, out var color) || color == null) return null;
                    // ガンマのカラースペースでは sRGB の印は読み方を変えない。リニアでは sRGB の写しを作る（バイトは同じ）
                    if (QualitySettings.activeColorSpace != ColorSpace.Linear) return color;
                    var target = Target(s, property, color.width, color.height, true);
                    Blit(color, target, 2);
                    return target;
                }
                case PreviewPacking.MetallicSmoothness:
                {
                    painted.Composites.TryGetValue(PaintChannel.Metallic, out var metal);
                    painted.Composites.TryGetValue(PaintChannel.Roughness, out var rough);
                    bool hasMetal = metal != null && group.Any(c => c.Channel == PaintChannel.Metallic), hasRough = rough != null && group.Any(c => c.Channel == PaintChannel.Roughness);
                    if (!hasMetal && !hasRough) return null;
                    var original = b.Source.GetTexture(property);
                    int w = Mathf.Max(hasMetal ? metal.width : 1, hasRough ? rough.width : 1), h = Mathf.Max(hasMetal ? metal.height : 1, hasRough ? rough.height : 1);
                    var target = Target(s, property, w, h, false);
                    pack.SetTexture("_SecondTex", hasRough ? rough : Texture2D.blackTexture);
                    pack.SetTexture("_OrigTex", original != null ? original : Texture2D.whiteTexture);
                    pack.SetVector("_Flags", new Vector4(hasMetal ? 1 : 0, hasRough ? 1 : 0, original != null ? 1 : 0, 0));
                    pack.SetVector("_Consts", new Vector4(b.Source.GetFloat("_Metallic"), b.Source.GetFloat("_Glossiness"), 0, 0));
                    try { Blit(hasMetal ? metal : Texture2D.blackTexture, target, 1, hasRough ? rough : null); }
                    finally { pack.SetTexture("_SecondTex", null); pack.SetTexture("_OrigTex", null); }
                    return target;
                }
                default:
                {
                    if (!painted.Composites.TryGetValue(first.Channel, out var value) || value == null) return null;
                    var target = Target(s, property, value.width, value.height, false);
                    pack.SetVector("_Params", new Vector4(first.Packing == PreviewPacking.InvertedValue ? 1 : 0, 0, 0, 0));
                    Blit(value, target, 0);
                    return target;
                }
            }
        }

        RenderTexture Target(Slot s, string property, int w, int h, bool srgb)
        {
            string key = srgb ? property + "|sRGB" : property;
            if (s.Packed.TryGetValue(key, out var rt) && rt != null && rt.width == w && rt.height == h) return rt;
            if (rt != null) DestroyTarget(rt);
            rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear)
            { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Repeat, useMipMap = false, name = "YoluPainter material preview " + property };
            if (!rt.Create()) { Object.DestroyImmediate(rt); throw new InvalidOperationException("RenderTexture.Create failed for " + property + "."); }
            s.Packed[key] = rt;
            return rt;
        }

        /// <summary>入力を読みながら別の描き先へ（同じテクスチャの読み書きはしない）。大きさが同じなら画素の中心を点で読む。</summary>
        void Blit(Texture source, RenderTexture target, int pass, Texture second = null)
        {
            if (ReferenceEquals(source, target) || ReferenceEquals(second, target)) throw new InvalidOperationException("A GPU pass must not read and write the same render texture.");
            var active = RenderTexture.active;
            var filter = source.filterMode; var secondFilter = second != null ? second.filterMode : FilterMode.Point;
            try
            {
                if (source.width == target.width && source.height == target.height) source.filterMode = FilterMode.Point;
                if (second != null && second.width == target.width && second.height == target.height) second.filterMode = FilterMode.Point;
                Graphics.Blit(source, target, pack, pass);
            }
            finally { source.filterMode = filter; if (second != null) second.filterMode = secondFilter; RenderTexture.active = active; }
        }

        /// <summary>詰め直すシェーダーが使えない理由（使えれば null）。</summary>
        public string PackProblem()
        {
            if (packChecked) return packProblem;
            packChecked = true;
            var shader = Shader.Find(PackShaderName);
            packProblem = ShaderHealth.IsUsable(shader) ? null : L.Tr("The preview's channel packing shader ({0}) cannot be used here, so the material view is unavailable; shown neutral.", PackShaderName);
            if (packProblem == null) pack = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return packProblem;
        }

        /// <summary>試験用: スロットの複製の、プロパティに入っている詰めたテクスチャ（無ければ null）。</summary>
        internal RenderTexture PackedTexture(int slot, string property) => slot >= 0 && slot < slots.Count && (slots[slot].Packed.TryGetValue(property, out var rt) || slots[slot].Packed.TryGetValue(property + "|sRGB", out rt)) ? rt : null;

        void ReleaseDisplay(Slot s)
        {
            if (s.Display != null) Object.DestroyImmediate(s.Display);
            s.Display = null; s.SyncedDirty = -1; s.SyncedEdits = -1; s.SyncedShader = null;
            foreach (var rt in s.Packed.Values) DestroyTarget(rt);
            s.Packed.Clear();
        }
        void Release(Slot s) { ReleaseDisplay(s); s.Binding = null; s.Reason = null; }
        static void DestroyTarget(RenderTexture rt)
        {
            if (rt == null) return;
            if (RenderTexture.active == rt) RenderTexture.active = null;
            rt.Release(); Object.DestroyImmediate(rt);
        }

        public void Dispose()
        {
            foreach (var s in slots) Release(s);
            slots.Clear();
            if (pack != null) Object.DestroyImmediate(pack);
            pack = null; packChecked = false;
        }
    }
}
