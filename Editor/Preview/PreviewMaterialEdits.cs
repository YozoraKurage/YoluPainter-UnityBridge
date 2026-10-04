using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Editor.LilToon;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>マテリアルの欄での見せ方（シェーダーのプロパティの型と属性から決める）。</summary>
    public enum MaterialPropertyControl { Toggle, Slider, IntSlider, Float, Int, Color, Vector, Texture, Enum }

    /// <summary>
    /// シェーダーのプロパティ 1 つの読み方（<see cref="MaterialPropertyInfo.Describe"/>）。Unity の MaterialPropertyDrawer の属性のうち、値の
    /// 意味を決めるものだけを読む: [Toggle(KEY)]・[MaterialToggle]（値が 0 でなければ KEY か「名前_ON」のキーワードを有効に）、[ToggleOff(KEY)]
    /// （0 なら KEY か「名前_OFF」を有効に）、[ToggleUI]・lilToon の [lilToggle]・[lilToggleLeft]（キーワードなし）、[IntRange]、[Enum(名前, 値, …)]、
    /// lilToon の [lilEnum]（説明の「見出し|選択肢|…」）、[HDR]・[lilHDR]。lilToonMulti の 4 つのトグルは、確かめた表（LilToonVerified の
    /// KeywordSourceToggle）どおりにキーワードも決める（lilToon のインスペクターが lilMaterialUtils.SetupMultiMaterial で付けるもの）。
    /// </summary>
    public sealed class MaterialPropertyInfo
    {
        public int Index { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public ShaderPropertyType Type { get; private set; }
        public ShaderPropertyFlags Flags { get; private set; }
        public IReadOnlyList<string> Attributes { get; private set; }
        public MaterialPropertyControl Control { get; private set; }
        public Vector2 Range { get; private set; }
        public bool Hdr { get; private set; }
        /// <summary>切り替えが決めるキーワード（無ければ null）と、値が 0 のときに有効か（[ToggleOff]）。</summary>
        public string Keyword { get; private set; }
        public bool KeywordWhenOff { get; private set; }
        /// <summary>選択肢（名前と値）。Enum のときだけ。</summary>
        public IReadOnlyList<(string label, float value)> Options { get; private set; } = Array.Empty<(string, float)>();
        public bool Hidden => (Flags & ShaderPropertyFlags.HideInInspector) != 0;

        /// <summary>欄に出す名前: 人が読む説明があればそれ、lilToon の訳の鍵（"sColor" など）や空なら名前。</summary>
        public string Label => string.IsNullOrWhiteSpace(Description) || Regex.IsMatch(Description, "^s[A-Z][A-Za-z0-9]*$") ? Name : Control == MaterialPropertyControl.Enum && Description.Contains("|") ? Description.Split('|')[0] : Description;

        /// <summary>値からキーワードを有効にするか（キーワードが無ければ null）。</summary>
        public bool? KeywordEnabled(float value) => Keyword == null ? (bool?)null : (value != 0) != KeywordWhenOff;

        static readonly Regex Call = new Regex("^(?<name>[A-Za-z_][A-Za-z0-9_.]*)\\s*(\\((?<args>.*)\\))?$", RegexOptions.Compiled);

        /// <summary>シェーダーのプロパティを並びの順に（隠したものも。<see cref="Hidden"/> で見分ける）。</summary>
        public static List<MaterialPropertyInfo> Describe(Shader shader, LilToonReport lilToon = null)
        {
            var list = new List<MaterialPropertyInfo>();
            if (shader == null) return list;
            var multiKeywords = MultiKeywords(lilToon);
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                var p = new MaterialPropertyInfo
                {
                    Index = i, Name = shader.GetPropertyName(i), Description = shader.GetPropertyDescription(i), Type = shader.GetPropertyType(i),
                    Flags = shader.GetPropertyFlags(i), Attributes = shader.GetPropertyAttributes(i),
                };
                if ((p.Flags & ShaderPropertyFlags.HDR) != 0) p.Hdr = true;
                switch (p.Type)
                {
                    case ShaderPropertyType.Color: p.Control = MaterialPropertyControl.Color; break;
                    case ShaderPropertyType.Vector: p.Control = MaterialPropertyControl.Vector; break;
                    case ShaderPropertyType.Texture: p.Control = MaterialPropertyControl.Texture; break;
                    case ShaderPropertyType.Int: p.Control = MaterialPropertyControl.Int; break;
                    case ShaderPropertyType.Range: p.Control = MaterialPropertyControl.Slider; p.Range = shader.GetPropertyRangeLimits(i); break;
                    default: p.Control = MaterialPropertyControl.Float; break;
                }
                foreach (var attribute in p.Attributes) p.Read(attribute.Trim());
                if (multiKeywords.TryGetValue(p.Name, out var keyword) && (p.Type == ShaderPropertyType.Float || p.Type == ShaderPropertyType.Int || p.Type == ShaderPropertyType.Range))
                { p.Control = MaterialPropertyControl.Toggle; p.Keyword = keyword; p.KeywordWhenOff = false; }
                list.Add(p);
            }
            return list;
        }

        void Read(string attribute)
        {
            var m = Call.Match(attribute);
            if (!m.Success) return;
            string name = m.Groups["name"].Value, args = m.Groups["args"].Success ? m.Groups["args"].Value.Trim() : null;
            bool number = Type == ShaderPropertyType.Float || Type == ShaderPropertyType.Range || Type == ShaderPropertyType.Int;
            switch (name)
            {
                case "HDR": case "lilHDR": if (Type == ShaderPropertyType.Color) Hdr = true; break;
                case "Toggle": case "MaterialToggle":
                    if (number) { Control = MaterialPropertyControl.Toggle; Keyword = string.IsNullOrEmpty(args) ? Name.ToUpperInvariant() + "_ON" : args; KeywordWhenOff = false; }
                    break;
                case "ToggleOff":
                    if (number) { Control = MaterialPropertyControl.Toggle; Keyword = string.IsNullOrEmpty(args) ? Name.ToUpperInvariant() + "_OFF" : args; KeywordWhenOff = true; }
                    break;
                case "ToggleUI": case "lilToggle": case "lilToggleLeft":
                    if (number) { Control = MaterialPropertyControl.Toggle; Keyword = null; }
                    break;
                case "IntRange": if (Type == ShaderPropertyType.Range) Control = MaterialPropertyControl.IntSlider; break;
                case "Enum":
                    if (number && args != null)
                    {
                        var parts = args.Split(',').Select(s => s.Trim()).ToArray();
                        var options = new List<(string, float)>();
                        // [Enum(名前, 値, 名前, 値, …)]。型の名前だけ（[Enum(UnityEngine.Rendering.CullMode)]）は列挙型から
                        if (parts.Length >= 2 && parts.Length % 2 == 0)
                        {
                            for (int k = 0; k + 1 < parts.Length; k += 2)
                                if (float.TryParse(parts[k + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) options.Add((parts[k], v));
                        }
                        else if (parts.Length == 1 && FindEnum(parts[0]) is Type type)
                            foreach (var value in Enum.GetValues(type)) options.Add((Enum.GetName(type, value), Convert.ToSingle(value, CultureInfo.InvariantCulture)));
                        if (options.Count > 0) { Control = MaterialPropertyControl.Enum; Options = options; }
                    }
                    break;
                case "lilEnum":
                    if (number && Description != null && Description.Contains("|"))
                    {
                        var labels = Description.Split('|').Skip(1).ToArray();
                        if (labels.Length > 0) { Control = MaterialPropertyControl.Enum; Options = labels.Select((l, k) => (l, (float)k)).ToList(); }
                    }
                    break;
            }
        }

        static Type FindEnum(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try { type = assembly.GetType(name, false) ?? (name.Contains('.') ? null : assembly.GetType("UnityEngine." + name, false)); } catch (Exception) { }
                if (type != null && type.IsEnum) return type;
            }
            return null;
        }

        /// <summary>lilToonMulti の、値でキーワードが決まるトグル（確かめた表のもの）。</summary>
        static Dictionary<string, string> MultiKeywords(LilToonReport report)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (report == null || !report.IsApplicable || !LilToonVerified.TryGet(report.Version, out var release) || !release.Variants.TryGetValue(report.ShaderName, out var variant)) return map;
            foreach (var spec in variant.Channels ?? Array.Empty<ChannelSpec>())
                if (spec.KeywordSourceToggle != null && spec.Keywords.Length == 1) map[spec.KeywordSourceToggle] = spec.Keywords[0];
            return map;
        }
    }

    public enum MaterialEditKind { Property, Keyword, RenderQueue, Instancing, DoubleSidedGI, GlobalIllumination, OverrideTag, ShaderPass }

    /// <summary>複製だけに入れた値。Property = 0 は以前のウィンドウの記録も読める。</summary>
    [Serializable]
    public sealed class MaterialPropertyEdit
    {
        public Material material;
        public string property;
        public ShaderPropertyType type;
        public MaterialEditKind kind;
        public Texture texture;
        public string text;
        public int integer;
        public bool hasInteger;
        /// <summary>Float・Range・Int は x、Color は rgba、Vector は xyzw。Texture は scale.xy・offset.xy。</summary>
        public Vector4 value;
        /// <summary>切り替えが決めるキーワード（無ければ空）と、それを有効にするか。</summary>
        public string keyword;
        public bool keywordEnabled;

        public float Float => value.x;
        public Color Color => new Color(value.x, value.y, value.z, value.w);

        /// <summary>マテリアルの今の値（この型で）。</summary>
        public static Vector4 Read(Material m, string property, ShaderPropertyType type)
        {
            switch (type)
            {
                case ShaderPropertyType.Color: { var c = m.GetColor(property); return new Vector4(c.r, c.g, c.b, c.a); }
                case ShaderPropertyType.Vector: return m.GetVector(property);
                case ShaderPropertyType.Int: return new Vector4(m.GetInteger(property), 0, 0, 0);
                case ShaderPropertyType.Texture:
                    var scale = m.GetTextureScale(property); var offset = m.GetTextureOffset(property);
                    return new Vector4(scale.x, scale.y, offset.x, offset.y);
                default: return new Vector4(m.GetFloat(property), 0, 0, 0);
            }
        }

        /// <summary>この値（とキーワード）を target に入れる。</summary>
        public void WriteTo(Material target)
        {
            if (target == null) return;
            switch (kind)
            {
                case MaterialEditKind.Keyword: if (keywordEnabled) target.EnableKeyword(keyword); else target.DisableKeyword(keyword); return;
                case MaterialEditKind.RenderQueue: target.renderQueue = (int)value.x; return;
                case MaterialEditKind.Instancing: target.enableInstancing = value.x != 0; return;
                case MaterialEditKind.DoubleSidedGI: target.doubleSidedGI = value.x != 0; return;
                case MaterialEditKind.GlobalIllumination: target.globalIlluminationFlags = (MaterialGlobalIlluminationFlags)(int)value.x; return;
                case MaterialEditKind.OverrideTag: target.SetOverrideTag(keyword, text ?? ""); return;
                case MaterialEditKind.ShaderPass: target.SetShaderPassEnabled(text, value.x != 0); return;
            }
            int index = target.shader != null ? target.shader.FindPropertyIndex(property) : -1;
            if (index < 0 || target.shader.GetPropertyType(index) != type) return;
            if (type == ShaderPropertyType.Texture && texture != null && target.shader.GetPropertyTextureDimension(index) != TextureDimension.Any && texture.dimension != target.shader.GetPropertyTextureDimension(index)) return;
            switch (type)
            {
                case ShaderPropertyType.Color: target.SetColor(property, Color); break;
                case ShaderPropertyType.Vector: target.SetVector(property, value); break;
                case ShaderPropertyType.Int: target.SetInteger(property, hasInteger ? integer : Mathf.RoundToInt(value.x)); break;
                case ShaderPropertyType.Texture:
                    target.SetTexture(property, texture);
                    target.SetTextureScale(property, new Vector2(value.x, value.y));
                    target.SetTextureOffset(property, new Vector2(value.z, value.w)); break;
                default: target.SetFloat(property, value.x); break;
            }
            if (!string.IsNullOrEmpty(keyword)) { if (keywordEnabled) target.EnableKeyword(keyword); else target.DisableKeyword(keyword); }
        }

        public MaterialPropertyEdit ReadCurrent(Material m)
        {
            var e = new MaterialPropertyEdit { material = m, property = property, type = type, kind = kind, keyword = keyword, text = text };
            switch (kind)
            {
                case MaterialEditKind.Keyword: e.keywordEnabled = m.IsKeywordEnabled(keyword); break;
                case MaterialEditKind.RenderQueue:
                    using (var serialized = new UnityEditor.SerializedObject(m)) e.value.x = serialized.FindProperty("m_CustomRenderQueue").intValue;
                    break;
                case MaterialEditKind.Instancing: e.value.x = m.enableInstancing ? 1 : 0; break;
                case MaterialEditKind.DoubleSidedGI: e.value.x = m.doubleSidedGI ? 1 : 0; break;
                case MaterialEditKind.GlobalIllumination: e.value.x = (int)m.globalIlluminationFlags; break;
                case MaterialEditKind.OverrideTag: e.text = m.GetTag(keyword, false, ""); break;
                case MaterialEditKind.ShaderPass: e.value.x = m.GetShaderPassEnabled(text) ? 1 : 0; break;
                default:
                    e.value = Read(m, property, type);
                    if (type == ShaderPropertyType.Int) { e.integer = m.GetInteger(property); e.hasInteger = true; }
                    if (type == ShaderPropertyType.Texture) e.texture = m.GetTexture(property);
                    if (!string.IsNullOrEmpty(keyword)) e.keywordEnabled = m.IsKeywordEnabled(keyword);
                    break;
            }
            return e;
        }

        internal bool SameValue(MaterialPropertyEdit other) => other != null && kind == other.kind && type == other.type && value.Equals(other.value)
            && texture == other.texture && text == other.text && keyword == other.keyword && keywordEnabled == other.keywordEnabled
            && hasInteger == other.hasInteger && (!hasInteger || integer == other.integer);
        internal MaterialPropertyEdit Snapshot() => (MaterialPropertyEdit)MemberwiseClone();

        public string DescribeValue()
        {
            if (kind == MaterialEditKind.Property && type == ShaderPropertyType.Int && hasInteger) return integer.ToString(CultureInfo.InvariantCulture);
            if (kind == MaterialEditKind.Keyword) return L.Tr(keywordEnabled ? "on" : "off");
            if (kind == MaterialEditKind.OverrideTag) return text ?? "";
            if (kind == MaterialEditKind.RenderQueue) return value.x.ToString("0", CultureInfo.InvariantCulture);
            if (kind == MaterialEditKind.Instancing || kind == MaterialEditKind.DoubleSidedGI || kind == MaterialEditKind.ShaderPass) return L.Tr(value.x != 0 ? "on" : "off");
            if (kind == MaterialEditKind.GlobalIllumination) return ((MaterialGlobalIlluminationFlags)(int)value.x).ToString();
            if (kind == MaterialEditKind.Property && type == ShaderPropertyType.Texture)
                return (texture != null ? texture.name : L.Tr("None")) + " · " + Format(value, ShaderPropertyType.Vector);
            return Format(value, type);
        }

        /// <summary>表に出す値（元と比べる一覧に使う）。</summary>
        public static string Format(Vector4 v, ShaderPropertyType type)
        {
            string F(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
            switch (type)
            {
                case ShaderPropertyType.Color: return "RGBA(" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + ", " + F(v.w) + ")";
                case ShaderPropertyType.Vector: return "(" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + ", " + F(v.w) + ")";
                default: return F(v.x);
            }
        }
    }

    /// <summary>
    /// マテリアルの欄で変えた値（元のマテリアルごと）。プレビューの複製にだけ入り、元のマテリアルには「マテリアルに反映…」でしか入らない。
    /// .ylp には保存しない（ウィンドウのシリアライズでリロードはまたぐ）。<see cref="Version"/> が変わったら複製を作り直す。
    /// </summary>
    [Serializable]
    public sealed class PreviewMaterialEdits
    {
        [SerializeField] List<MaterialPropertyEdit> edits = new List<MaterialPropertyEdit>();
        [NonSerialized] int version;

        /// <summary>変えるたびに増える（プレビューが複製を合わせ直す目印）。</summary>
        public int Version => version;
        public int Count { get { Prune(); return edits.Count; } }
        public IReadOnlyList<MaterialPropertyEdit> All { get { Prune(); return edits; } }
        public IEnumerable<MaterialPropertyEdit> For(Material material) => material == null ? Enumerable.Empty<MaterialPropertyEdit>() : edits.Where(e => e != null && e.material == material);
        public MaterialPropertyEdit Find(Material material, string property) => material == null ? null : edits.FirstOrDefault(e => e != null && e.material == material && e.property == property);
        public IEnumerable<Material> Materials => edits.Where(e => e != null && e.material != null).Select(e => e.material).Distinct();

        /// <summary>値を入れる。元のマテリアルと同じ値（とキーワード）になったら、変えた印ごと外す。</summary>
        public void Set(Material material, MaterialPropertyInfo info, Vector4 value)
        {
            if (material == null) throw new ArgumentNullException(nameof(material));
            if (info == null || info.Type == ShaderPropertyType.Texture) throw new ArgumentException("Textures are not edited in the Material panel.", nameof(info));
            if (!material.HasProperty(info.Name)) throw new ArgumentException("The material has no property " + info.Name + ".", nameof(info));
            var edit = new MaterialPropertyEdit { material = material, property = info.Name, type = info.Type, value = value };
            bool? keyword = info.Type == ShaderPropertyType.Color || info.Type == ShaderPropertyType.Vector ? null : info.KeywordEnabled(value.x);
            if (keyword.HasValue) { edit.keyword = info.Keyword; edit.keywordEnabled = keyword.Value; }
            var original = MaterialPropertyEdit.Read(material, info.Name, info.Type);
            bool same = original == value && (!keyword.HasValue || material.IsKeywordEnabled(info.Keyword) == keyword.Value);
            int at = edits.FindIndex(e => e != null && e.material == material && e.property == info.Name);
            if (same) { if (at >= 0) { edits.RemoveAt(at); version++; } return; }
            if (at >= 0) { if (edits[at].value == value && edits[at].keyword == edit.keyword && edits[at].keywordEnabled == edit.keywordEnabled) return; edits[at] = edit; }
            else edits.Add(edit);
            version++;
        }

        /// <summary>シェーダーインスペクターの複製と元の差。塗ったテクスチャを入れる前の複製だけを渡す。</summary>
        public bool Capture(Material source, Material copy)
        {
            if (source == null || copy == null || source.shader != copy.shader) throw new ArgumentException("The inspector copy must use the source shader.");
            var next = new List<MaterialPropertyEdit>();
            void Add(MaterialPropertyEdit key)
            {
                var original = key.ReadCurrent(source); var changed = key.ReadCurrent(copy);
                changed.material = source;
                if (!changed.SameValue(original)) next.Add(changed);
            }
            foreach (var p in MaterialPropertyInfo.Describe(source.shader).GroupBy(p => p.Name).Select(g => g.First()))
                Add(new MaterialPropertyEdit { property = p.Name, type = p.Type });
            foreach (var k in source.shaderKeywords.Union(copy.shaderKeywords).OrderBy(k => k, StringComparer.Ordinal))
                Add(new MaterialPropertyEdit { property = "$keyword:" + k, kind = MaterialEditKind.Keyword, keyword = k });
            foreach (var k in new[] { MaterialEditKind.RenderQueue, MaterialEditKind.Instancing, MaterialEditKind.DoubleSidedGI, MaterialEditKind.GlobalIllumination })
                Add(new MaterialPropertyEdit { property = "$" + k, kind = k });
            foreach (var tag in TagNames(source).Union(TagNames(copy)).OrderBy(t => t, StringComparer.Ordinal))
                Add(new MaterialPropertyEdit { property = "$tag:" + tag, kind = MaterialEditKind.OverrideTag, keyword = tag });
            foreach (string pass in Enumerable.Range(0, source.passCount).Select(source.GetPassName).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal))
            {
                Add(new MaterialPropertyEdit { property = "$pass:" + pass, kind = MaterialEditKind.ShaderPass, text = pass });
            }
            var old = For(source).ToList();
            if (old.Count == next.Count && old.All(e => next.Any(n => n.property == e.property && n.SameValue(e)))) return false;
            edits.RemoveAll(e => e != null && e.material == source); edits.AddRange(next); version++;
            return true;
        }

        static IEnumerable<string> TagNames(Material material)
        {
            using (var serialized = new UnityEditor.SerializedObject(material))
            {
                var map = serialized.FindProperty("stringTagMap");
                if (map == null) yield break;
                for (int i = 0; i < map.arraySize; i++) yield return map.GetArrayElementAtIndex(i).FindPropertyRelative("first").stringValue;
            }
        }

        /// <summary>1 つを元の値に戻す（印を外す）。戻したら true。</summary>
        public bool Revert(Material material, string property)
        {
            int removed = edits.RemoveAll(e => e != null && e.material == material && e.property == property);
            if (removed > 0) version++;
            return removed > 0;
        }

        /// <summary>そのマテリアル（null なら全部）の変更をすべて元に戻す。戻した数。</summary>
        public int RevertAll(Material material = null)
        {
            int removed = edits.RemoveAll(e => e == null || material == null || e.material == material);
            if (removed > 0) version++;
            return removed;
        }

        /// <summary>そのマテリアルの変更を target（プレビューの複製）に入れる。</summary>
        public void ApplyTo(Material source, Material target, ISet<string> paintedProperties = null)
        {
            foreach (var e in For(source))
            {
                if (e.kind == MaterialEditKind.Property && e.type == ShaderPropertyType.Texture && paintedProperties?.Contains(e.property) == true)
                { target.SetTextureScale(e.property, new Vector2(e.value.x, e.value.y)); target.SetTextureOffset(e.property, new Vector2(e.value.z, e.value.w)); }
                else e.WriteTo(target);
            }
        }

        /// <summary>消えたマテリアル（削除・再読み込み）の変更を捨てる。</summary>
        void Prune() { if (edits.RemoveAll(e => e == null || e.material == null) > 0) version++; }
        /// <summary>リロードの後: 版を進めて、複製を合わせ直させる。</summary>
        public void Touch() => version++;
    }
}
