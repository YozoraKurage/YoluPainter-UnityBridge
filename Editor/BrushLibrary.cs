using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Brushes;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>取り込んだブラシの置き場。ブラシごとに &lt;key&gt;.json（設定）と &lt;key&gt;.tip.png / &lt;key&gt;.texture.png
    /// （筆先と紙の質感、白黒）を書く（デュアルブラシの筆先は &lt;key&gt;.dual.png）。schema 2 でカラーダイナミクス・デュアルブラシ・
    /// フェード・傾きを足した（schema 1 のファイルも読める。足した項目は既定値で、以前と同じに描く）。置き場は 2 つ:
    /// <list type="bullet">
    /// <item>個人（<see cref="Personal"/>、ID は "library:"）: 既定は Unity プロジェクトの UserSettings/YoluPainter/Brushes。
    /// UserSettings はユーザーごとの置き場で、パッケージにも Assets にもバージョン管理にも入らない。</item>
    /// <item>プロジェクト共有（<see cref="Project"/>、ID は "project:"）: 共有設定でプロジェクト内のフォルダを指定したときだけ。
    /// バージョン管理で同じプロジェクトの全員に渡る。</item>
    /// </list>
    /// どちらの場所も <see cref="PainterSettings"/> で変えられ、変わったら次に使うときに読み直す。</summary>
    internal sealed class BrushLibrary
    {
        [Serializable] sealed class Entry
        {
            public int schema = 2;
            public string name = "", category = "", source = "", tipFile = "", textureFile = "";
            public string[] tipFiles = new string[0]; // 筆先が複数あるとき（GIMP のホースなど）。tipFile より優先
            public int tipSelection;
            public string[] warnings = new string[0];
            public double radius = 16, hardness = .8, spacing = .15, opacity = 1, flow = 1, angle, roundness = 1;
            public double sizeJitter, angleJitter, roundnessJitter, opacityJitter, flowJitter, scatter, textureDepth, textureScale = 1;
            public int count = 1;
            public bool pressureSize = true, pressureOpacity = true, pressureFlow, erase, followDirection;
            // schema 2
            public double fgBgJitter, hueJitter, saturationJitter, brightnessJitter, purity;
            public bool colorPerTip = true;
            public bool dual; public string dualTipFile = "";
            public double dualRadius = 8, dualHardness = 1, dualSpacing = .25, dualAngle, dualRoundness = 1, dualScatter;
            public int dualCount = 1, dualMode;
            public int fadeSize, fadeOpacity, fadeFlow;
            public bool tiltSize, tiltOpacity, tiltFlow, tiltAngle;
        }

        public static readonly BrushLibrary Personal = new BrushLibrary("library:", "Imported", () => PainterSettings.BrushFolder);
        public static readonly BrushLibrary Project = new BrushLibrary("project:", "Project", () => PainterSettings.ProjectBrushFolder);
        public static IEnumerable<BrushLibrary> All { get { yield return Personal; yield return Project; } }

        readonly string prefix;
        readonly Func<string> configuredFolder;
        string folderOverride, loadedFolder;
        Dictionary<string, BrushTips.TipRef> tips;
        List<Core.BrushPreset> presets;

        BrushLibrary(string prefix, string menuName, Func<string> configuredFolder) { this.prefix = prefix; MenuName = menuName; this.configuredFolder = configuredFolder; }

        /// <summary>ブラシ一覧での見出し。</summary>
        public string MenuName { get; }
        /// <summary>置き場の絶対パス。共有の置き場を使わない設定なら null。テストは差し替える（null で設定に戻す）。</summary>
        public string Folder
        {
            get => folderOverride ?? configuredFolder();
            set { folderOverride = value; presets = null; tips = null; }
        }
        public bool Enabled => Folder != null;

        public IReadOnlyList<Core.BrushPreset> Presets { get { Load(); return presets; } }

        public bool Owns(string id) => id != null && id.StartsWith(prefix, StringComparison.Ordinal);
        /// <summary>取り込んだブラシ（どちらかの置き場）のプリセット ID か。</summary>
        public static bool IsLibraryPreset(string presetId) => All.Any(l => l.Owns(presetId));
        public static BrushLibrary Owning(string id) => All.FirstOrDefault(l => l.Owns(id));

        public BrushTips.TipRef ResolveRef(string id)
        {
            if (!Owns(id)) return null;
            Load(); return tips.TryGetValue(id, out var tip) ? tip : null;
        }
        public string IdOf(BrushSettings s)
        { Load(); foreach (var entry in tips) if (entry.Value.Matches(s)) return entry.Key; return null; }

        /// <summary>取り込んだブラシを保存し、プリセットとして返す。同じ名前でも上書きしない（別のキーになる）。</summary>
        public IReadOnlyList<Core.BrushPreset> Add(IEnumerable<ImportedBrush> brushes, string category)
        {
            if (!Enabled) throw new InvalidOperationException("The shared brush folder is not set in Project Settings > YoluPainter.");
            string folder = Folder;
            Directory.CreateDirectory(folder);
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
                    fgBgJitter = s.ForegroundBackgroundJitter, hueJitter = s.HueJitter, saturationJitter = s.SaturationJitter, brightnessJitter = s.BrightnessJitter, purity = s.Purity,
                    colorPerTip = s.ColorPerTip, fadeSize = s.FadeSize, fadeOpacity = s.FadeOpacity, fadeFlow = s.FadeFlow,
                    tiltSize = s.TiltSize, tiltOpacity = s.TiltOpacity, tiltFlow = s.TiltFlow, tiltAngle = s.TiltAngle,
                };
                if (s.Dual != null)
                {
                    var d = s.Dual; entry.dual = true; entry.dualRadius = d.Radius; entry.dualHardness = d.Hardness; entry.dualSpacing = d.Spacing; entry.dualAngle = d.Angle;
                    entry.dualRoundness = d.Roundness; entry.dualScatter = d.Scatter; entry.dualCount = d.Count; entry.dualMode = (int)d.Mode;
                    if (d.Tip != null) { entry.dualTipFile = key + ".dual.png"; WritePng(Path.Combine(folder, entry.dualTipFile), d.Tip); }
                }
                if (s.Tips != null && s.Tips.Length > 0)
                {
                    entry.tipFiles = new string[s.Tips.Length]; entry.tipSelection = (int)s.TipSelection;
                    for (int i = 0; i < s.Tips.Length; i++) { entry.tipFiles[i] = key + ".tip" + i + ".png"; WritePng(Path.Combine(folder, entry.tipFiles[i]), s.Tips[i]); }
                }
                else if (s.Tip != null) { entry.tipFile = key + ".tip.png"; WritePng(Path.Combine(folder, entry.tipFile), s.Tip); }
                if (s.Texture != null) { entry.textureFile = key + ".texture.png"; WritePng(Path.Combine(folder, entry.textureFile), s.Texture); }
                File.WriteAllText(Path.Combine(folder, key + ".json"), JsonUtility.ToJson(entry, true));
                keys.Add(key);
            }
            tips = null; presets = null; Load();
            return presets.Where(p => keys.Contains(p.Id.Substring(prefix.Length))).ToList();
        }

        /// <summary>取り込んだブラシを消す（その json と png）。</summary>
        public void Remove(string presetId)
        {
            if (!Owns(presetId) || !Enabled || !Directory.Exists(Folder)) return;
            string key = presetId.Substring(prefix.Length);
            foreach (var path in Directory.GetFiles(Folder, key + ".*")) File.Delete(path);
            tips = null; presets = null;
        }

        /// <summary>この置き場のブラシを別のフォルダへ複写する（置き場を変えるとき用。元は消さない。同じキーは上書きしない）。</summary>
        public int CopyTo(string destination)
        {
            if (!Enabled || !Directory.Exists(Folder)) return 0;
            Directory.CreateDirectory(destination);
            int copied = 0;
            foreach (var json in Directory.GetFiles(Folder, "*.json"))
            {
                string key = Path.GetFileNameWithoutExtension(json);
                if (File.Exists(Path.Combine(destination, key + ".json"))) continue;
                foreach (var path in Directory.GetFiles(Folder, key + ".*")) File.Copy(path, Path.Combine(destination, Path.GetFileName(path)), false);
                copied++;
            }
            return copied;
        }

        void Load()
        {
            string folder = Folder;
            if (presets != null && folder == loadedFolder) return;
            loadedFolder = folder;
            tips = new Dictionary<string, BrushTips.TipRef>(); presets = new List<Core.BrushPreset>();
            if (folder == null || !Directory.Exists(folder)) return;
            foreach (var path in Directory.GetFiles(folder, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                try
                {
                    string key = Path.GetFileNameWithoutExtension(path);
                    var e = JsonUtility.FromJson<Entry>(File.ReadAllText(path));
                    if (e == null || e.schema < 1 || e.schema > 2) continue;
                    var settings = new BrushSettings
                    {
                        Radius = e.radius, Hardness = e.hardness, Spacing = e.spacing, Opacity = e.opacity, Flow = e.flow, Angle = e.angle, Roundness = e.roundness,
                        SizeJitter = e.sizeJitter, AngleJitter = e.angleJitter, RoundnessJitter = e.roundnessJitter, OpacityJitter = e.opacityJitter, FlowJitter = e.flowJitter,
                        Scatter = e.scatter, TextureDepth = e.textureDepth, TextureScale = e.textureScale, Count = e.count,
                        PressureSize = e.pressureSize, PressureOpacity = e.pressureOpacity, PressureFlow = e.pressureFlow, Erase = e.erase, FollowDirection = e.followDirection,
                        ForegroundBackgroundJitter = e.fgBgJitter, HueJitter = e.hueJitter, SaturationJitter = e.saturationJitter, BrightnessJitter = e.brightnessJitter, Purity = e.purity,
                        ColorPerTip = e.colorPerTip, FadeSize = e.fadeSize, FadeOpacity = e.fadeOpacity, FadeFlow = e.fadeFlow,
                        TiltSize = e.tiltSize, TiltOpacity = e.tiltOpacity, TiltFlow = e.tiltFlow, TiltAngle = e.tiltAngle,
                    };
                    if (e.dual)
                    {
                        settings.Dual = new DualBrush { Radius = e.dualRadius, Hardness = e.dualHardness, Spacing = e.dualSpacing, Angle = e.dualAngle, Roundness = e.dualRoundness,
                            Scatter = e.dualScatter, Count = e.dualCount, Mode = (DualBrushMode)e.dualMode };
                        if (!string.IsNullOrEmpty(e.dualTipFile)) { settings.Dual.Tip = ReadPng(Path.Combine(folder, e.dualTipFile), e.name + " dual"); tips[prefix + key + ":dual"] = new BrushTips.TipRef(settings.Dual.Tip, null, TipSelection.Random); }
                    }
                    if (e.tipFiles != null && e.tipFiles.Length > 0)
                    {
                        settings.Tips = e.tipFiles.Select(f => ReadPng(Path.Combine(folder, f), e.name)).ToArray(); settings.TipSelection = (TipSelection)e.tipSelection;
                        tips[prefix + key] = new BrushTips.TipRef(null, settings.Tips, settings.TipSelection);
                    }
                    else if (!string.IsNullOrEmpty(e.tipFile)) { settings.Tip = ReadPng(Path.Combine(folder, e.tipFile), e.name); tips[prefix + key] = new BrushTips.TipRef(settings.Tip, null, TipSelection.Random); }
                    if (!string.IsNullOrEmpty(e.textureFile)) { settings.Texture = ReadPng(Path.Combine(folder, e.textureFile), e.name + " texture"); tips[prefix + key + ":texture"] = new BrushTips.TipRef(settings.Texture, null, TipSelection.Random); }
                    presets.Add(new Core.BrushPreset(prefix + key, e.name, e.category, settings));
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
