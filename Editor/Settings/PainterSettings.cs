using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>Unity プロジェクトごとの YoluPainter の設定。2 つのファイルに分ける。
    /// <list type="bullet">
    /// <item>共有（ProjectSettings/Packages/net.yozolab.yolupainter/Settings.json）: バージョン管理に入り、同じプロジェクトを
    /// 開く全員で揃えるもの。パスはプロジェクトのフォルダからの相対パスだけ。</item>
    /// <item>個人（UserSettings/YoluPainter/Settings.json）: その人のその環境だけのもの（Photoshop / CLIP STUDIO の環境設定に
    /// 当たる）。マシンの事情で変わるメモリ予算や、プロジェクトの外を指せるブラシ置き場。</item>
    /// </list>
    /// 読めないファイルは既定値で動いて理由を残し、保存するときに .broken へ退避してから書く。新しい版の YoluPainter が
    /// 書いたファイル（schema が大きい）は既定値で動き、上書きを拒否する。</summary>
    internal static class PainterSettings
    {
        public const string PackageName = "net.yozolab.yolupainter";
        public const int Schema = 1;

        [Serializable] internal sealed class Shared
        {
            public int schema = Schema;
            /// <summary>New で作るドキュメントの既定の大きさ。</summary>
            public int defaultResolution = 1024;
            /// <summary>プロジェクトで共有するブラシ置き場（プロジェクトのフォルダからの相対パス）。空なら使わない。</summary>
            public string projectBrushFolder = "";
        }

        [Serializable] internal sealed class Personal
        {
            public int schema = Schema;
            /// <summary>自分の取り込んだブラシの置き場。空なら UserSettings/YoluPainter/Brushes。相対パスはプロジェクトのフォルダから。</summary>
            public string brushFolder = "";
            public bool showBundledBrushes = true;
            /// <summary>未保存の作業の復旧 checkpoint を書く間隔。</summary>
            public int recoveryIntervalSeconds = 15;
            /// <summary>ドキュメントのメモリ予算（MiB）: Undo 履歴、レイヤーの画素の合計、1 回の操作（ストローク・塗りつぶし・変形）の
            /// 巻き戻し用。-1 は自動（このマシンのメモリから決める。<see cref="AutomaticBudgetMiB"/>）。</summary>
            public int undoBudgetMiB = Automatic, sourceBudgetMiB = Automatic, strokeBudgetMiB = Automatic;
            /// <summary>合成を速くするために GPU に残す写し（レイヤーのブロックと下の合成結果）の上限（MiB）。-1 は自動（VRAM から）。
            /// 0 は残さない（結果は同じで、スライダー操作などが遅くなるだけ）。</summary>
            public int gpuCacheMiB = Automatic;
            /// <summary>Undo の予算を超えても残す直近の段数（GIMP の「最小のアンドゥ段数」と同じ考え方。既定も GIMP と同じ 5）。</summary>
            public int minUndoSteps = 5;
            public string brushImportFolder = "";
            /// <summary>.ylp を上書き保存するとき退避した直前の版をいくつ残すか。-1 ですべて、0 で退避しない。</summary>
            public int backupsToKeep = -1;
        }

        public static readonly int[] Resolutions = { 256, 512, 1024, 2048, 4096 };
        /// <summary>パスの比較（Windows と macOS の既定は大文字小文字を区別しないので、区切りが \ の環境では区別しない）。</summary>
        public static StringComparison PathComparison => Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        public const int MinRecoverySeconds = 5, MaxRecoverySeconds = 600;
        public const int MaxUndoMiB = 16384, MinSourceMiB = 16, MaxSourceMiB = 32768, MinStrokeMiB = 8, MaxStrokeMiB = 8192, MaxMinUndoSteps = 100;
        /// <summary>予算の値で「自動」を表す。</summary>
        public const int Automatic = -1;
        internal enum Budget { Undo, Source, Stroke, GpuCache }
        public const int MaxGpuCacheMiB = 16384;
        /// <summary>この GPU のメモリ（MiB）。テストは差し替える。</summary>
        internal static int? GraphicsMemoryMiBOverride;
        public static int GraphicsMemoryMiB => GraphicsMemoryMiBOverride ?? Math.Max(256, SystemInfo.graphicsMemorySize);

        /// <summary>このマシンの物理メモリ（MiB）。テストは差し替える。</summary>
        internal static int? SystemMemoryMiBOverride;
        public static int SystemMemoryMiB => SystemMemoryMiBOverride ?? Math.Max(1024, SystemInfo.systemMemorySize);
        /// <summary>自動のときの予算（MiB）。物理メモリの一定の割合を、小さいマシンでも作業できる下限と、Unity Editor・モデル・
        /// 他のアセットの分を残す上限で挟む（仕様 15「予算を機器に適応させる」）。16 GB で Undo 1024（GIMP 3 の既定 1 GiB と同じ）・
        /// 画素 2048（4K の全面 RGBA8 で 32 枚）・1 回の操作 512。</summary>
        public static int AutomaticBudgetMiB(Budget budget)
        {
            int ram = SystemMemoryMiB;
            switch (budget)
            {
                case Budget.Undo: return Mathf.Clamp(ram / 16, 256, 2048);
                case Budget.Source: return Mathf.Clamp(ram / 8, 256, 8192);
                case Budget.Stroke: return Mathf.Clamp(ram / 32, 64, 1024);
                // 申告された VRAM の 1/8。上限まで取らない（仕様 15）。8 GB で 1024、4 GB で 512
                case Budget.GpuCache: return Mathf.Clamp(GraphicsMemoryMiB / 8, 128, 1024);
                default: throw new ArgumentOutOfRangeException(nameof(budget));
            }
        }
        static long Bytes(int mib, Budget budget) => (mib == Automatic ? AutomaticBudgetMiB(budget) : mib) * 1024L * 1024;

        static string projectRoot;
        static Shared shared;
        static Personal personal;
        static readonly List<string> warnings = new List<string>();
        static bool sharedLocked, personalLocked, sharedBroken, personalBroken;

        /// <summary>設定が変わったとき（保存後・読み直し後）。ブラシ置き場の読み直しなどに使う。</summary>
        public static event Action Changed;

        /// <summary>Unity プロジェクトのフォルダ。テストは一時フォルダに差し替える（差し替えると読み直す）。</summary>
        public static string ProjectRoot
        {
            get => projectRoot ?? (projectRoot = Directory.GetCurrentDirectory());
            set { projectRoot = value; Reload(); }
        }
        public static string SharedPath => Path.Combine(ProjectRoot, "ProjectSettings", "Packages", PackageName, "Settings.json");
        public static string PersonalPath => Path.Combine(ProjectRoot, "UserSettings", "YoluPainter", "Settings.json");
        public static string DefaultBrushFolder => Path.Combine(ProjectRoot, "UserSettings", "YoluPainter", "Brushes");

        /// <summary>今の共有設定の写し。変えるときは写しを直して <see cref="Save(Shared, Personal)"/> に渡す。</summary>
        public static Shared SharedSettings { get { Load(); return JsonUtility.FromJson<Shared>(JsonUtility.ToJson(shared)); } }
        public static Personal PersonalSettings { get { Load(); return JsonUtility.FromJson<Personal>(JsonUtility.ToJson(personal)); } }
        /// <summary>読み込みで直したこと・読めなかったこと。</summary>
        public static IReadOnlyList<string> Warnings { get { Load(); return warnings; } }
        public static bool SharedIsReadOnly { get { Load(); return sharedLocked; } }
        public static bool PersonalIsReadOnly { get { Load(); return personalLocked; } }

        /// <summary>自分のブラシ置き場の絶対パス。</summary>
        public static string BrushFolder
        {
            get { Load(); return string.IsNullOrWhiteSpace(personal.brushFolder) ? DefaultBrushFolder : Path.GetFullPath(Path.Combine(ProjectRoot, personal.brushFolder)); }
        }
        /// <summary>共有のブラシ置き場の絶対パス。使わない設定なら null。</summary>
        public static string ProjectBrushFolder
        {
            get { Load(); return string.IsNullOrWhiteSpace(shared.projectBrushFolder) ? null : Path.GetFullPath(Path.Combine(ProjectRoot, shared.projectBrushFolder)); }
        }
        public static bool ShowBundledBrushes { get { Load(); return personal.showBundledBrushes; } }
        public static int DefaultResolution { get { Load(); return shared.defaultResolution; } }
        public static int RecoveryIntervalSeconds { get { Load(); return personal.recoveryIntervalSeconds; } }
        public static long UndoBudgetBytes { get { Load(); return Bytes(personal.undoBudgetMiB, Budget.Undo); } }
        public static long SourceBudgetBytes { get { Load(); return Bytes(personal.sourceBudgetMiB, Budget.Source); } }
        public static long StrokeBudgetBytes { get { Load(); return Bytes(personal.strokeBudgetMiB, Budget.Stroke); } }
        public static int MinUndoSteps { get { Load(); return personal.minUndoSteps; } }
        public static long GpuCacheBytes { get { Load(); return Bytes(personal.gpuCacheMiB, Budget.GpuCache); } }
        public static string BrushImportFolder { get { Load(); return personal.brushImportFolder; } }
        public static int BackupsToKeep { get { Load(); return personal.backupsToKeep; } }
        public const int MaxBackups = 1000;

        public static void Reload() { shared = null; personal = null; Changed?.Invoke(); }

        static void Load()
        {
            if (shared != null && personal != null) return;
            warnings.Clear();
            shared = Read<Shared>(SharedPath, "Shared settings", s => s.schema, out sharedLocked, out sharedBroken) ?? new Shared();
            personal = Read<Personal>(PersonalPath, "Personal settings", p => p.schema, out personalLocked, out personalBroken) ?? new Personal();
            foreach (var problem in Check(shared, personal, fix: true)) warnings.Add(problem + " The default is used.");
        }

        static T Read<T>(string path, string what, Func<T, int> schemaOf, out bool locked, out bool broken) where T : class
        {
            locked = false; broken = false;
            if (!File.Exists(path)) return null;
            try
            {
                var value = JsonUtility.FromJson<T>(File.ReadAllText(path));
                if (value == null) throw new InvalidDataException("empty file");
                int schema = schemaOf(value);
                if (schema > Schema)
                {
                    locked = true;
                    warnings.Add(what + " were written by a newer YoluPainter (schema " + schema + "); defaults are used and the file is not overwritten.");
                    return null;
                }
                if (schema < 1) throw new InvalidDataException("schema " + schema);
                return value;
            }
            catch (Exception ex)
            {
                broken = true;
                warnings.Add(what + " could not be read (" + ex.Message + "); defaults are used. Saving keeps the old file as " + Path.GetFileName(path) + ".broken.");
                return null;
            }
        }

        /// <summary>値の検査。fix なら範囲外を既定値か最寄りの値に直し、直したことを返す。</summary>
        internal static List<string> Check(Shared s, Personal p, bool fix)
        {
            var problems = new List<string>();
            var defaultsShared = new Shared(); var defaultsPersonal = new Personal();
            if (Array.IndexOf(Resolutions, s.defaultResolution) < 0)
            { problems.Add("Default resolution " + s.defaultResolution + " is not one of " + string.Join(", ", Resolutions) + "."); if (fix) s.defaultResolution = defaultsShared.defaultResolution; }
            string folderProblem = CheckProjectBrushFolder(s.projectBrushFolder);
            if (folderProblem != null) { problems.Add(folderProblem); if (fix) s.projectBrushFolder = ""; }
            string personalFolderProblem = CheckPersonalBrushFolder(p.brushFolder);
            if (personalFolderProblem != null) { problems.Add(personalFolderProblem); if (fix) p.brushFolder = ""; }
            void Range(ref int value, int min, int max, int fallback, string what)
            {
                if (value >= min && value <= max) return;
                problems.Add(what + " " + value + " is outside " + min + ".." + max + ".");
                if (fix) value = fallback;
            }
            Range(ref p.recoveryIntervalSeconds, MinRecoverySeconds, MaxRecoverySeconds, defaultsPersonal.recoveryIntervalSeconds, "Recovery interval (s)");
            void Budget(ref int value, int min, int max, string what) { if (value != Automatic) Range(ref value, min, max, Automatic, what + " (-1 = automatic)"); }
            Budget(ref p.undoBudgetMiB, 0, MaxUndoMiB, "Undo budget (MiB)");
            Budget(ref p.sourceBudgetMiB, MinSourceMiB, MaxSourceMiB, "Layer pixel budget (MiB)");
            Budget(ref p.strokeBudgetMiB, MinStrokeMiB, MaxStrokeMiB, "One operation budget (MiB)");
            Range(ref p.minUndoSteps, 0, MaxMinUndoSteps, defaultsPersonal.minUndoSteps, "Minimum undo steps");
            Budget(ref p.gpuCacheMiB, 0, MaxGpuCacheMiB, "GPU cache (MiB)");
            Range(ref p.backupsToKeep, -1, MaxBackups, defaultsPersonal.backupsToKeep, "Backups to keep");
            if (p.brushImportFolder == null && fix) p.brushImportFolder = "";
            if (s.projectBrushFolder == null && fix) s.projectBrushFolder = "";
            if (p.brushFolder == null && fix) p.brushFolder = "";
            return problems;
        }

        /// <summary>共有のブラシ置き場の検査。全員の環境で同じ場所を指すようにプロジェクト内の相対パスに限り、Unity が
        /// 取り込んでしまう Assets / Packages の下（チルダで終わるフォルダの中を除く）は断る。</summary>
        internal static string CheckProjectBrushFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            if (Path.IsPathRooted(folder)) return "The shared brush folder must be a path inside the project folder (relative), so it is the same for everyone.";
            string full = Path.GetFullPath(Path.Combine(ProjectRoot, folder)), root = Path.GetFullPath(ProjectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, root, PathComparison)) return "The brush folder cannot be the project folder itself.";
            if (!full.StartsWith(root, PathComparison)) return "The shared brush folder must be inside the project folder.";
            return InsideImportedFolder(full.Substring(root.Length));
        }
        internal static string CheckPersonalBrushFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            string full; try { full = Path.GetFullPath(Path.Combine(ProjectRoot, folder)); } catch (Exception ex) { return "The brush folder path is invalid (" + ex.Message + ")."; }
            string root = Path.GetFullPath(ProjectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, root, PathComparison)) return "The brush folder cannot be the project folder itself.";
            return full.StartsWith(root, PathComparison) ? InsideImportedFolder(full.Substring(root.Length)) : null;
        }
        static string InsideImportedFolder(string relative)
        {
            var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "The brush folder cannot be the project folder itself.";
            if (parts[0] != "Assets" && parts[0] != "Packages") return null;
            for (int i = 1; i < parts.Length; i++) if (parts[i].EndsWith("~", StringComparison.Ordinal) || parts[i].StartsWith(".", StringComparison.Ordinal)) return null;
            return "A brush folder under " + parts[0] + "/ would be imported by Unity as assets; use a folder outside it, or one whose name ends with '~'.";
        }

        /// <summary>設定を検査して保存する。範囲外があれば何も書かずに例外。null を渡した方は書かない。</summary>
        public static void Save(Shared newShared, Personal newPersonal)
        {
            Load();
            var s = newShared ?? shared; var p = newPersonal ?? personal;
            var problems = Check(s, p, fix: false);
            if (problems.Count > 0) throw new ArgumentException(string.Join(" ", problems));
            if (newShared != null && sharedLocked) throw new InvalidOperationException("The shared settings file was written by a newer YoluPainter and is not overwritten.");
            if (newPersonal != null && personalLocked) throw new InvalidOperationException("The personal settings file was written by a newer YoluPainter and is not overwritten.");
            if (newShared != null) { newShared.schema = Schema; Write(SharedPath, JsonUtility.ToJson(newShared, true), sharedBroken); }
            if (newPersonal != null) { newPersonal.schema = Schema; Write(PersonalPath, JsonUtility.ToJson(newPersonal, true), personalBroken); }
            Reload();
        }

        /// <summary>個人設定の 1 項目だけ変える（最後に開いたフォルダなど）。新しい版のファイルなら黙って何もしない。</summary>
        public static void UpdatePersonal(Action<Personal> change)
        {
            Load();
            if (personalLocked) return;
            var p = PersonalSettings; change(p); Save(null, p);
        }

        static void Write(string path, string json, bool keepBroken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (keepBroken && File.Exists(path)) File.Copy(path, path + ".broken", true);
            string temp = path + ".tmp";
            File.WriteAllText(temp, json + "\n");
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
    }
}
