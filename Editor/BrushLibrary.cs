using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Brushes;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>取り込んだブラシの置き場。Unity プロジェクトの UserSettings/YoluPainter/Brushes に、ブラシごとに
    /// &lt;key&gt;.json（設定）と &lt;key&gt;.tip.png / &lt;key&gt;.texture.png（筆先と紙の質感、白黒）を書く。
    /// UserSettings はユーザーごとの置き場で、パッケージにも Assets にも入らない（取り込んだ他者のブラシを
    /// 配布物に混ぜない）。</summary>
    internal static class BrushLibrary
    {
        [Serializable] sealed class Entry
        {
            public int schema = 1;
            public string name = "", category = "", source = "", tipFile = "", textureFile = "";
            public string[] tipFiles = new string[0]; // 筆先が複数あるとき（GIMP のホースなど）。tipFile より優先
            public int tipSelection;
            public string[] warnings = new string[0];
            public double radius = 16, hardness = .8, spacing = .15, opacity = 1, flow = 1, angle, roundness = 1;
            public double sizeJitter, angleJitter, roundnessJitter, opacityJitter, flowJitter, scatter, textureDepth, textureScale = 1;
            public int count = 1;
            public bool pressureSize = true, pressureOpacity = true, pressureFlow, erase, followDirection;
        }

        const string Prefix = "library:";
        static string folder;
        static Dictionary<string, BrushTips.TipRef> tips;
        static List<Core.BrushPreset> presets;

        /// <summary>保存先。テストは一時フォルダに差し替える（差し替えると読み直す）。</summary>
        public static string Folder
        {
            get => folder ?? (folder = Path.Combine(Directory.GetCurrentDirectory(), "UserSettings", "YoluPainter", "Brushes"));
            set { folder = value; tips = null; presets = null; }
        }

        public static IReadOnlyList<Core.BrushPreset> Presets { get { Load(); return presets; } }

        public static bool IsLibraryPreset(string presetId) => presetId != null && presetId.StartsWith(Prefix, StringComparison.Ordinal);

        public static BrushTips.TipRef ResolveRef(string id)
        {
            if (id == null || !id.StartsWith(Prefix, StringComparison.Ordinal)) return null;
            Load(); return tips.TryGetValue(id, out var tip) ? tip : null;
        }
        public static string IdOf(BrushSettings s)
        { Load(); foreach (var entry in tips) if (entry.Value.Matches(s)) return entry.Key; return null; }

        /// <summary>取り込んだブラシを保存し、プリセットとして返す。同じ名前でも上書きしない（別のキーになる）。</summary>
        public static IReadOnlyList<Core.BrushPreset> Add(IEnumerable<ImportedBrush> brushes, string category)
        {
            Directory.CreateDirectory(Folder);
            var keys = new List<string>();
            foreach (var brush in brushes)
            {
                string key = Sanitize(brush.Name) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                var s = brush.Settings;
                var entry = new Entry
                {
                    name = brush.Name, category = category ?? "", source = brush.Source, warnings = brush.Warnings.ToArray(),
                    radius = s.Radius, hardness = s.Hardness, spacing = s.Spacing, opacity = s.Opacity, flow = s.Flow, angle = s.Angle, roundness = s.Roundness,
                    sizeJitter = s.SizeJitter, angleJitter = s.AngleJitter, roundnessJitter = s.RoundnessJitter, opacityJitter = s.OpacityJitter, flowJitter = s.FlowJitter,
                    scatter = s.Scatter, textureDepth = s.TextureDepth, textureScale = s.TextureScale, count = s.Count,
                    pressureSize = s.PressureSize, pressureOpacity = s.PressureOpacity, pressureFlow = s.PressureFlow, erase = s.Erase, followDirection = s.FollowDirection,
                };
                if (s.Tips != null && s.Tips.Length > 0)
                {
                    entry.tipFiles = new string[s.Tips.Length]; entry.tipSelection = (int)s.TipSelection;
                    for (int i = 0; i < s.Tips.Length; i++) { entry.tipFiles[i] = key + ".tip" + i + ".png"; WritePng(Path.Combine(Folder, entry.tipFiles[i]), s.Tips[i]); }
                }
                else if (s.Tip != null) { entry.tipFile = key + ".tip.png"; WritePng(Path.Combine(Folder, entry.tipFile), s.Tip); }
                if (s.Texture != null) { entry.textureFile = key + ".texture.png"; WritePng(Path.Combine(Folder, entry.textureFile), s.Texture); }
                File.WriteAllText(Path.Combine(Folder, key + ".json"), JsonUtility.ToJson(entry, true));
                keys.Add(key);
            }
            tips = null; presets = null; Load();
            return presets.Where(p => keys.Contains(p.Id.Substring(Prefix.Length))).ToList();
        }

        /// <summary>取り込んだブラシを消す（その json と png）。</summary>
        public static void Remove(string presetId)
        {
            if (presetId == null || !presetId.StartsWith(Prefix, StringComparison.Ordinal)) return;
            string key = presetId.Substring(Prefix.Length);
            foreach (var path in Directory.GetFiles(Folder, key + ".*")) File.Delete(path);
            tips = null; presets = null;
        }

        static void Load()
        {
            if (presets != null) return;
            tips = new Dictionary<string, BrushTips.TipRef>(); presets = new List<Core.BrushPreset>();
            if (!Directory.Exists(Folder)) return;
            foreach (var path in Directory.GetFiles(Folder, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                try
                {
                    string key = Path.GetFileNameWithoutExtension(path);
                    var e = JsonUtility.FromJson<Entry>(File.ReadAllText(path));
                    if (e == null || e.schema != 1) continue;
                    var settings = new BrushSettings
                    {
                        Radius = e.radius, Hardness = e.hardness, Spacing = e.spacing, Opacity = e.opacity, Flow = e.flow, Angle = e.angle, Roundness = e.roundness,
                        SizeJitter = e.sizeJitter, AngleJitter = e.angleJitter, RoundnessJitter = e.roundnessJitter, OpacityJitter = e.opacityJitter, FlowJitter = e.flowJitter,
                        Scatter = e.scatter, TextureDepth = e.textureDepth, TextureScale = e.textureScale, Count = e.count,
                        PressureSize = e.pressureSize, PressureOpacity = e.pressureOpacity, PressureFlow = e.pressureFlow, Erase = e.erase, FollowDirection = e.followDirection,
                    };
                    if (e.tipFiles != null && e.tipFiles.Length > 0)
                    {
                        settings.Tips = e.tipFiles.Select(f => ReadPng(Path.Combine(Folder, f), e.name)).ToArray(); settings.TipSelection = (TipSelection)e.tipSelection;
                        tips[Prefix + key] = new BrushTips.TipRef(null, settings.Tips, settings.TipSelection);
                    }
                    else if (!string.IsNullOrEmpty(e.tipFile)) { settings.Tip = ReadPng(Path.Combine(Folder, e.tipFile), e.name); tips[Prefix + key] = new BrushTips.TipRef(settings.Tip, null, TipSelection.Random); }
                    if (!string.IsNullOrEmpty(e.textureFile)) { settings.Texture = ReadPng(Path.Combine(Folder, e.textureFile), e.name + " texture"); tips[Prefix + key + ":texture"] = new BrushTips.TipRef(settings.Texture, null, TipSelection.Random); }
                    presets.Add(new Core.BrushPreset(Prefix + key, e.name, e.category, settings));
                }
                catch (Exception ex) { Debug.LogWarning("YoluPainter: skipped imported brush " + Path.GetFileName(path) + ": " + ex.Message); }
            }
        }

        static void WritePng(string path, BrushTip tip)
        {
            var texture = new Texture2D(tip.Width, tip.Height, TextureFormat.RGBA32, false, true);
            try
            {
                var pixels = new Color32[tip.Width * tip.Height];
                for (int y = 0; y < tip.Height; y++) for (int x = 0; x < tip.Width; x++) { byte v = tip[x, y]; pixels[y * tip.Width + x] = new Color32(v, v, v, 255); }
                texture.SetPixels32(pixels); texture.Apply(false, false);
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }
        static BrushTip ReadPng(string path, string name)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!texture.LoadImage(File.ReadAllBytes(path))) throw new InvalidDataException("Unreadable tip image " + Path.GetFileName(path));
                var pixels = texture.GetPixels32(); var alpha = new byte[pixels.Length];
                for (int i = 0; i < pixels.Length; i++) alpha[i] = pixels[i].r; // Unity のテクスチャは左下原点なので行はそのまま
                return new BrushTip(name, texture.width, texture.height, alpha);
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }
        static string Sanitize(string name)
        {
            var chars = (name ?? "brush").Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray();
            var s = new string(chars).Trim('_'); if (s.Length == 0) s = "brush"; return s.Length > 40 ? s.Substring(0, 40) : s;
        }
    }
}
