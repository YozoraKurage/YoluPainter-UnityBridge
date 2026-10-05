using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Brushes;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>パッケージに同梱した他者のブラシ集（BrushSets~/、ライセンスは各セットの README）。読み取り専用。
    /// 各筆先にこのツール側の既定設定を付けてプリセットにする（元アプリのプリセットではない）。</summary>
    internal static class BundledBrushSets
    {
        public const string Prefix = "bundled:";
        static List<Core.BrushPreset> presets;
        static Dictionary<string, BrushTips.TipRef> tips;
        static readonly List<string> loadWarnings = new List<string>();

        /// <summary>パッケージのルート（ディスク上の絶対パス。テストはここを差し替えられる）。パッケージがどこに入っても（Packages の下・ローカルのフォルダ・
        /// VPM・Assets への複製）同じに求まる <see cref="PackagePaths"/> から取る。パッケージの情報が取れない配置（Assets への複製）で、プロジェクトのフォルダを
        /// 根にして同梱のブラシを探し損ねない。</summary>
        public static string PackageRoot
        {
            get => packageRoot ?? (packageRoot = PackagePaths.PhysicalRoot);
            set { packageRoot = value; presets = null; tips = null; }
        }
        static string packageRoot;

        public static IReadOnlyList<Core.BrushPreset> Presets { get { Load(); return presets; } }
        public static IReadOnlyList<string> LoadWarnings { get { Load(); return loadWarnings; } }
        public static BrushTips.TipRef Resolve(string id) { Load(); return id != null && tips.TryGetValue(id, out var t) ? t : null; }
        public static string IdOf(BrushSettings s) { Load(); foreach (var e in tips) if (e.Value.Matches(s)) return e.Key; return null; }

        static void Load()
        {
            if (presets != null) return;
            presets = new List<Core.BrushPreset>(); tips = new Dictionary<string, BrushTips.TipRef>(); loadWarnings.Clear();
            LoadSet("krita4", "Krita", Path.Combine(PackageRoot, "BrushSets~", "Krita4Default", "brushes"));
            foreach (var warning in loadWarnings) Debug.LogWarning("YoluPainter: bundled brushes: " + warning);
        }

        static void LoadSet(string setId, string category, string folder)
        {
            if (!Directory.Exists(folder)) { loadWarnings.Add("Brush set folder is missing: " + folder); return; }
            foreach (var path in Directory.GetFiles(folder).OrderBy(p => p, StringComparer.Ordinal))
            {
                string file = Path.GetFileName(path), ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".gbr" && ext != ".gih" && ext != ".png") continue;
                try
                {
                    var brush = BrushImport.ReadFile(path)[0];
                    var s = brush.Settings;
                    // 原寸（最大 600px 程度）のままでは既定として大きすぎるので、使いやすい大きさにする。
                    s.Radius = Math.Max(4, Math.Min(40, s.Radius)); s.PressureOpacity = false;
                    string id = Prefix + setId + "/" + file;
                    tips[id] = new BrushTips.TipRef(s.Tip, s.Tips, s.TipSelection);
                    presets.Add(new Core.BrushPreset(id, brush.Name == "Layer" || string.IsNullOrEmpty(brush.Name) ? BrushImport.PrettyName(file) : brush.Name, category, s));
                }
                catch (Exception ex) { loadWarnings.Add(file + ": " + ex.Message); }
            }
        }
    }
}
