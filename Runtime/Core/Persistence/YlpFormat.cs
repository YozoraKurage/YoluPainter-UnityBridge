using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Yozolab.YoluPainter.Core.MeshMaps;

namespace Yozolab.YoluPainter.Core.Persistence
{
    /// <summary>.ylp を書いたアプリ（名前・版・Unity の版）。</summary>
    public sealed class YlpWriterInfo
    {
        public string App { get; }
        public string Version { get; }
        public string Unity { get; }
        public YlpWriterInfo(string app, string version, string unity)
        {
            App = Check(app, nameof(app)); Version = Check(version, nameof(version)); Unity = Check(unity, nameof(unity));
        }
        static string Check(string value, string name)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("Empty " + name + ".", name);
            if (value.Length > YlpFormat.MaxText) throw new ArgumentException(name + " is longer than " + YlpFormat.MaxText + " characters.", name);
            return value;
        }
        public override string ToString() => App + " " + Version + " (Unity " + Unity + ")";
    }

    /// <summary>ylp.json の中身: 中身の形式の版と、最後に保存したアプリ・最初に作ったアプリ（分からなければ null）。</summary>
    public sealed class YlpFormatInfo
    {
        public int Format { get; }
        public YlpWriterInfo SavedBy { get; }
        public YlpWriterInfo CreatedBy { get; }
        public YlpFormatInfo(int format, YlpWriterInfo savedBy, YlpWriterInfo createdBy)
        {
            if (format < 1) throw new ArgumentOutOfRangeException(nameof(format));
            Format = format; SavedBy = savedBy; CreatedBy = createdBy;
        }
    }

    /// <summary>
    /// テクスチャセットが描くマテリアル（project.json の "material"。形式 7 から）。テクスチャセットはモデルの中のマテリアル 1 つで、そのマテリアルを
    /// 使うスロット（レンダラー × サブメッシュ）を全部受け持つ（Substance Painter と同じ）。3 つの形のどれか 1 つ:
    /// <list type="bullet">
    /// <item>マテリアル: 名前と、分かれば Unity のアセットの GUID と localFileId（埋め込みのマテリアルは FBX の GUID と、その中の番号）。
    /// モデルのマテリアルへの照合は識別子 → 名前の順（窓がする）。</item>
    /// <item>Unassigned: マテリアルの無いスロットの全部（Substance の DefaultMaterial に当たる。モデルに 1 つまで）。</item>
    /// <item>スロットの番号: まだマテリアルに結び付けていない（形式 6 までのファイルを開いたがモデルが無かった・モデル無しで作った）。
    /// モデルを読んだとき、平らにしたスロットの並びのその番号のスロットのマテリアルに読み替える。</item>
    /// </list>
    /// </summary>
    public sealed class YlpMaterialRef : IEquatable<YlpMaterialRef>
    {
        /// <summary>マテリアルの名前（マテリアルの形のときだけ。空でもよい）。</summary>
        public string Name { get; }
        /// <summary>Unity のアセットの GUID（小文字の 16 進 32 文字）。アセットでないマテリアル（シーンの中で作ったもの）は null。</summary>
        public string AssetGuid { get; }
        /// <summary>アセットの中の localFileId（<see cref="AssetGuid"/> があるときだけ意味を持つ）。</summary>
        public long LocalFileId { get; }
        /// <summary>マテリアルの無いスロットの組か。</summary>
        public bool Unassigned { get; }
        /// <summary>まだマテリアルに結び付けていないスロットの番号（それ以外は −1）。</summary>
        public int Slot { get; }
        public bool IsMaterial => !Unassigned && Slot < 0;
        public bool IsPendingSlot => Slot >= 0;
        /// <summary>識別子（GUID と localFileId）。無ければ null。</summary>
        public string Identity => AssetGuid != null ? AssetGuid + ":" + LocalFileId.ToString(CultureInfo.InvariantCulture) : null;

        YlpMaterialRef(string name, string guid, long fileId, bool unassigned, int slot) { Name = name; AssetGuid = guid; LocalFileId = fileId; Unassigned = unassigned; Slot = slot; }

        /// <summary>マテリアル（名前は 0〜256 文字で制御文字を含まない。GUID は小文字の 16 進 32 文字か null）。</summary>
        public static YlpMaterialRef Material(string name, string assetGuid = null, long localFileId = 0)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (name.Length > YlpFormat.MaxText) throw new ArgumentException("A material name is at most " + YlpFormat.MaxText + " characters.", nameof(name));
            if (name.Any(c => c < 0x20 || c == 0x7f)) throw new ArgumentException("A material name cannot hold control characters.", nameof(name));
            if (assetGuid != null && !IsGuid(assetGuid)) throw new ArgumentException("An asset GUID is 32 lower-case hexadecimal digits.", nameof(assetGuid));
            return new YlpMaterialRef(name, assetGuid, assetGuid != null ? localFileId : 0, false, -1);
        }
        public static readonly YlpMaterialRef UnassignedSlots = new YlpMaterialRef(null, null, 0, true, -1);
        public static YlpMaterialRef PendingSlot(int slot)
        {
            if (slot < 0 || slot > YlpFormat.MaxMaterialSlot) throw new ArgumentOutOfRangeException(nameof(slot), "The material slot must be 0–" + YlpFormat.MaxMaterialSlot + ".");
            return new YlpMaterialRef(null, null, 0, false, slot);
        }
        static bool IsGuid(string s) => s.Length == 32 && s.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');

        /// <summary>同じものを指す鍵か（識別子のあるマテリアルは識別子で、無ければ名前で。Unassigned 同士、同じスロットの番号同士）。
        /// project.json で 2 つのセットが同じ鍵を持つことは許さない（名前だけのマテリアルは除く: 同じ名前の別のマテリアルがありうる）。</summary>
        public string ExclusiveKey => Unassigned ? "unassigned" : IsPendingSlot ? "slot:" + Slot.ToString(CultureInfo.InvariantCulture) : Identity != null ? "asset:" + Identity : null;

        public bool Equals(YlpMaterialRef other) => other != null && Name == other.Name && AssetGuid == other.AssetGuid && LocalFileId == other.LocalFileId && Unassigned == other.Unassigned && Slot == other.Slot;
        public override bool Equals(object obj) => Equals(obj as YlpMaterialRef);
        public override int GetHashCode() { unchecked { return ((Name?.GetHashCode() ?? 0) * 397 ^ (AssetGuid?.GetHashCode() ?? 0)) * 397 ^ LocalFileId.GetHashCode() ^ Slot * 31 ^ (Unassigned ? 7 : 0); } }
        public override string ToString() => Unassigned ? "(unassigned)" : IsPendingSlot ? "slot " + Slot : Name + (Identity != null ? " [" + Identity + "]" : "");
    }

    /// <summary>project.json の 1 つのテクスチャセット: ID（エントリの置き場 sets/&lt;ID&gt;/ の名前）、名前、描くマテリアル。</summary>
    public sealed class YlpTextureSetInfo
    {
        public Guid Id { get; }
        public string Name { get; }
        /// <summary>描くマテリアル（形式 7 から。形式 6 までのファイルは移行でスロットの番号になる）。</summary>
        public YlpMaterialRef Material { get; }
        public YlpTextureSetInfo(Guid id, string name, YlpMaterialRef material)
        {
            if (id == Guid.Empty) throw new ArgumentException("A texture set needs an ID.", nameof(id));
            YlpFormat.CheckSetName(name);
            Id = id; Name = name; Material = material ?? throw new ArgumentNullException(nameof(material));
        }
    }

    /// <summary>project.json の中身: テクスチャセットの並び（1 つ以上。ID・名前は重ならない。マテリアルは識別子・Unassigned・スロットの番号が
    /// 重ならない）と、今のセット。</summary>
    public sealed class YlpProjectInfo
    {
        public IReadOnlyList<YlpTextureSetInfo> Sets { get; }
        public Guid CurrentSet { get; }
        public YlpProjectInfo(IEnumerable<YlpTextureSetInfo> sets, Guid currentSet)
        {
            var list = (sets ?? throw new ArgumentNullException(nameof(sets))).ToList();
            if (list.Count == 0) throw new ArgumentException("A project has at least one texture set.", nameof(sets));
            if (list.Count > YlpFormat.MaxTextureSets) throw new ArgumentException("A project has at most " + YlpFormat.MaxTextureSets + " texture sets.", nameof(sets));
            if (list.Any(s => s == null)) throw new ArgumentException("Null texture set.", nameof(sets));
            if (list.Select(s => s.Id).Distinct().Count() != list.Count) throw new ArgumentException("Two texture sets have the same ID.", nameof(sets));
            var keys = list.Select(s => s.Material.ExclusiveKey).Where(k => k != null).ToList();
            if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count) throw new ArgumentException("Two texture sets paint the same material.", nameof(sets));
            if (list.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Count) throw new ArgumentException("Two texture sets have the same name.", nameof(sets));
            if (!list.Any(s => s.Id == currentSet)) throw new ArgumentException("The current texture set is not in the project.", nameof(currentSet));
            Sets = list.AsReadOnly(); CurrentSet = currentSet;
        }
    }

    /// <summary>開いた .ylp の中身を今の形式にしたもの。</summary>
    public sealed class YlpOpened
    {
        /// <summary>今の形式（<see cref="YlpFormat.Current"/>）の並びのエントリ。ylp.json は含まない（<see cref="Info"/> に読んだ）。</summary>
        public Dictionary<string, byte[]> Files { get; internal set; }
        /// <summary>ファイルに書かれていた形式と書いたアプリ（形式 1 のファイルは書いたアプリが分からない）。</summary>
        public YlpFormatInfo Info { get; internal set; }
        /// <summary>テクスチャセットの並び（project.json。形式 2 までのファイルは移行で作った 1 つのセット）。</summary>
        public YlpProjectInfo Project { get; internal set; }
        /// <summary>古い形式から今の形式へ移したか（保存すると今の形式で書き、古い YoluPainter では開けなくなる）。</summary>
        public bool Upgraded => Info.Format < YlpFormat.Current;
        /// <summary>この版の YoluPainter が知らないエントリ（保存すると残らない）。</summary>
        public IReadOnlyList<string> UnknownEntries { get; internal set; }
        /// <summary>移すときの知らせ。</summary>
        public IReadOnlyList<string> Notes { get; internal set; }
        /// <summary>プロジェクトのリソースの並び（resources.json。形式 4 から。無ければ空）。画素はまだ読んでいない
        /// （<see cref="ResourceIndex.Load"/> が読んで確かめる）。</summary>
        public IReadOnlyList<YlpResourceEntry> Resources { get; internal set; }

        /// <summary>テクスチャセットのエントリ（sets/&lt;ID&gt;/ を取った名前 → 中身）。</summary>
        public Dictionary<string, byte[]> SetFiles(Guid set)
        {
            string folder = YlpFormat.SetFolder(set);
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in Files) if (entry.Key.StartsWith(folder, StringComparison.Ordinal)) result.Add(entry.Key.Substring(folder.Length), entry.Value);
            return result;
        }
    }

    /// <summary>
    /// .ylp の中身の形式（仕様はスタンドアロン版のリポジトリの docs/YLP_FORMAT.md）。外側（zip・mimetype・manifest の SHA-256）は <see cref="YlpArchive"/>
    /// の層で、ここはその中のエントリの並びと、それを書いたアプリの記録（ylp.json）とテクスチャセットの並び（project.json）を受け持つ。
    /// <list type="bullet">
    /// <item>形式 1: ylp.json の無いもの（2026-10-03 より前の YoluPainter）。</item>
    /// <item>形式 2: ylp.json を足した（エントリの並びは形式 1 と同じ）。</item>
    /// <item>形式 3: テクスチャセット。根に project.json（セットの並びと今のセット）、正本と合成とメッシュマップはセットごとに
    /// sets/&lt;ID&gt;/ の下。view.json の materialSlot は使わない（スロットは project.json に）。</item>
    /// <item>形式 4: プロジェクトのリソース（全部のセットで共通の画像）。根に resources.json（並び）、画素は中身ごとに 1 つの
    /// resources/&lt;中身の SHA-256&gt;.png（<see cref="ResourceIndex"/>）。どちらも正本。manifest は YOLUPAINTER-YLP-3。</item>
    /// <item>形式 5: リソースの種類にスマートマテリアルとスマートマスク（resources.json の kind が smartMaterial / smartMask、ファイルはそのまま
    /// resources/&lt;ファイルの SHA-256&gt;.ylsmart。<see cref="SmartMaterialFile"/>）。どちらも正本。名前の決まりは同じなので manifest は YLP-3 のまま。</item>
    /// <item>形式 6: リソースの種類にブラシ・マテリアル、Unity のアセットの localFileId、自分の置き場の下位の階層（並びは同じ）。</item>
    /// <item>形式 7: テクスチャセットはマテリアルごと。project.json の各セットの materialSlot（平らにしたスロットの番号）をやめ、material
    /// （<see cref="YlpMaterialRef"/>: 名前と GUID・localFileId、Unassigned、まだ結び付けていないスロットの番号）にした。移行ではスロットの番号を
    /// そのまま持ち、窓がモデルを読んだときにそのスロットのマテリアルへ読み替える。</item>
    /// </list>
    /// 開くときは <see cref="Open"/> が形式を読み、古い形式なら <see cref="Steps"/> を順に通して今の形式の並びにする（メモリの上だけで、
    /// ファイルは書き換えない）。今より新しい形式は、どのエントリにも触れずに断る。保存は <see cref="Stamp"/> でいつも今の形式で書く。
    /// エントリの中身の版（document.utpaint の版 1〜10 など）は、それぞれの読み手が読み替える（ここでは扱わない）。
    /// </summary>
    public static class YlpFormat
    {
        /// <summary>今の形式。</summary>
        public const int Current = 7;
        /// <summary>形式と書いたアプリの記録（形式 2 から）。</summary>
        public const string InfoName = "ylp.json";
        /// <summary>テクスチャセットの並び（形式 3 から）。</summary>
        public const string ProjectName = "project.json";
        public const string ViewName = "view.json", BrushName = "brush.json", ThumbnailName = "thumbnail.png", ImportedOriginalName = "imported-original.psd";
        /// <summary>テクスチャセットごとのエントリの置き場（sets/&lt;ID&gt;/。ID は小文字のハイフン付きの 36 文字）。</summary>
        public const string SetsFolder = "sets/";
        /// <summary>形式 2 までのファイルを移すときに付ける、ただ 1 つのセットの名前（YoluPainter はモデルのマテリアルの名前に付け直す）。</summary>
        public const string MigratedSetName = "Texture Set 1";
        public const int MaxTextureSets = 64, MaxMaterialSlot = 65535, MaxTextureSetNameLength = MaxText;
        internal const int MaxText = 256, MaxInfoBytes = 64 * 1024;
        const int GuidLength = 36;

        /// <summary>形式 k から k+1 へ移す段（Steps[k - 1]）。エントリの並びを変え、知らせを足す。</summary>
        static readonly Action<Dictionary<string, byte[]>, List<string>>[] Steps =
        {
            (files, notes) => { }, // 1 → 2: 並びは同じ（ylp.json を足しただけ）
            ToTextureSets,         // 2 → 3: 1 つのテクスチャセットにする
            (files, notes) => { }, // 3 → 4: 並びは同じ（形式 3 のファイルにはリソースが無い。resources.json の無いファイルはリソース無し）
            (files, notes) => { }, // 4 → 5: 並びは同じ（形式 4 のリソースは画像だけ。スマートマテリアルの種類が増えただけ）
            (files, notes) => { }, // 5 → 6: ブラシ・マテリアルと localFileID、下位階層の置き場（画像とスマートの旧リソースはそのまま）
            ToMaterials,           // 6 → 7: project.json の materialSlot をスロットの番号のマテリアル（まだ結び付けていない）に
        };

        /// <summary>エントリの種類。</summary>
        public enum EntryKind
        {
            /// <summary>形式と書いたアプリの記録。</summary>
            Info,
            /// <summary>正本と、描き手の作業そのもの（失うと作業を失う）。テクスチャセットの並びもこれ。</summary>
            Source,
            /// <summary>ウィンドウの状態（モデル・選んだチャンネル・ブラシ）。失うと開いた後の状態が既定に戻る。</summary>
            State,
            /// <summary>正本から作り直せるもの（合成の PNG・サムネイル・焼いたメッシュマップ）。</summary>
            Derived,
        }

        public static string SetFolder(Guid set) => SetsFolder + set.ToString("D") + "/";
        public static string SetEntry(Guid set, string name) => SetFolder(set) + name;

        /// <summary>sets/&lt;ID&gt;/&lt;名前&gt; なら ID と残りの名前。ID は小文字のハイフン付きの形だけを認める（同じセットに 2 つの名前を作らない）。</summary>
        public static bool TrySplitSetEntry(string name, out Guid set, out string leaf)
        {
            set = Guid.Empty; leaf = null;
            if (name == null || !name.StartsWith(SetsFolder, StringComparison.Ordinal) || name.Length <= SetsFolder.Length + GuidLength + 1 || name[SetsFolder.Length + GuidLength] != '/') return false;
            string id = name.Substring(SetsFolder.Length, GuidLength);
            if (!Guid.TryParseExact(id, "D", out set) || set.ToString("D") != id || set == Guid.Empty) { set = Guid.Empty; return false; }
            leaf = name.Substring(SetsFolder.Length + GuidLength + 1);
            return true;
        }

        /// <summary>今の形式で知っているエントリなら種類を返す（セットの下の名前は、どのセットかを見ない。並びにないセットは <see cref="Open"/> が知らせる）。</summary>
        public static EntryKind? KindOf(string name)
        {
            if (name == InfoName) return EntryKind.Info;
            if (name == ProjectName || name == ResourceIndex.EntryName || ResourceIndex.TryParseContentEntry(name, out _) || ResourceIndex.TryParseSmartEntry(name, out _) || ResourceIndex.TryParseBrushEntry(name, out _)) return EntryKind.Source;
            if (name == ViewName || name == BrushName) return EntryKind.State;
            if (name == ThumbnailName) return EntryKind.Derived;
            return TrySplitSetEntry(name, out _, out var leaf) ? SetEntryKind(leaf) : null;
        }

        /// <summary>テクスチャセットの下のエントリの種類（形式 2 までは根にあった名前）。</summary>
        static EntryKind? SetEntryKind(string leaf)
        {
            if (leaf == YlpArchive.NativeName || leaf == SelectionBinary.EntryName || leaf == ImportedOriginalName) return EntryKind.Source;
            if (leaf.StartsWith(YlpArchive.CompositeFolder, StringComparison.Ordinal) && leaf.EndsWith(".png", StringComparison.Ordinal))
            {
                string channel = leaf.Substring(YlpArchive.CompositeFolder.Length, leaf.Length - YlpArchive.CompositeFolder.Length - 4);
                return Enum.TryParse(channel, false, out PaintChannel c) && Enum.IsDefined(typeof(PaintChannel), c) && c.ToString() == channel ? EntryKind.Derived : (EntryKind?)null;
            }
            if (leaf.StartsWith(MeshMapBinary.EntryPrefix, StringComparison.Ordinal) && leaf.EndsWith(MeshMapBinary.EntrySuffix, StringComparison.Ordinal) && leaf.IndexOf('/') < 0) return EntryKind.Derived;
            return null;
        }

        /// <summary>形式 2 までの根のエントリのうち、形式 3 でテクスチャセットの下へ移るもの。</summary>
        static bool MovesIntoSet(string name) =>
            name == YlpArchive.NativeName || name == SelectionBinary.EntryName || name == ImportedOriginalName
            || name.StartsWith(YlpArchive.CompositeFolder, StringComparison.Ordinal)
            || name.StartsWith(MeshMapBinary.EntryPrefix, StringComparison.Ordinal) && name.IndexOf('/') < 0;

        /// <summary>
        /// 2 → 3: 根の正本・選択範囲・取り込んだ PSD・合成・メッシュマップを sets/&lt;ID&gt;/ へ動かし、view.json の materialSlot から 1 つの
        /// セットの project.json を作る。ID は document.utpaint の頭にある文書の ID（同じファイルはいつも同じ ID になる）。view.json が
        /// 読めなければスロット 0 にして知らせる（view.json は状態で、正本ではない）。
        /// </summary>
        static void ToTextureSets(Dictionary<string, byte[]> files, List<string> notes)
        {
            if (!files.TryGetValue(YlpArchive.NativeName, out var native)) throw new InvalidDataException("The file has no native document (" + YlpArchive.NativeName + ").");
            var id = DocumentBinary.ReadId(native);
            int slot = 0;
            if (files.TryGetValue(ViewName, out var view))
            {
                try { slot = LegacyMaterialSlot(view); }
                catch (InvalidDataException ex) { notes.Add("The material slot in view.json could not be read (" + ex.Message + "); the texture set uses slot 0."); }
            }
            string folder = SetFolder(id);
            foreach (var name in files.Keys.Where(MovesIntoSet).ToList())
            {
                files.Add(folder + name, files[name]);
                files.Remove(name);
            }
            files[ProjectName] = WriteProject(new YlpProjectInfo(new[] { new YlpTextureSetInfo(id, MigratedSetName, YlpMaterialRef.PendingSlot(slot)) }, id));
        }

        /// <summary>6 → 7: project.json の各セットの materialSlot を、まだマテリアルに結び付けていないスロットの番号（"material": { "slot": n }）に
        /// する。どのマテリアルかはモデルが要るので、窓が読んだモデルで決める（同じマテリアルに 2 つが落ちれば片方を「モデルに無い」として残す）。
        /// ほかのエントリは変えない。</summary>
        static void ToMaterials(Dictionary<string, byte[]> files, List<string> notes)
        {
            if (!files.TryGetValue(ProjectName, out var bytes)) return; // 無ければ Open が断る
            files[ProjectName] = WriteProject(ReadProject(bytes, legacySlots: true));
        }

        /// <summary>形式 2 までの view.json の materialSlot（無ければ 0。読めなければ InvalidDataException）。移行とインポーターが使う。</summary>
        public static int LegacyMaterialSlot(byte[] bytes)
        {
            var root = ParseObject(bytes, ViewName);
            if (!root.TryGetValue("materialSlot", out var value) || value == null) return 0;
            if (!(value is long slot) || slot < 0 || slot > MaxMaterialSlot) throw new InvalidDataException("\"materialSlot\" is not an integer of 0–" + MaxMaterialSlot);
            return (int)slot;
        }

        /// <summary>今より新しい形式なら、理由（形式の番号と書いたアプリ）を添えて断る。</summary>
        public static void CheckReadable(YlpFormatInfo info)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            if (info.Format > Current)
                throw new InvalidDataException("This file uses .ylp format " + info.Format + (info.SavedBy != null ? ", saved by " + info.SavedBy : "") +
                    ". This YoluPainter reads up to format " + Current + "; update YoluPainter to open it. The file was not changed.");
        }

        /// <summary>
        /// 開いたエントリを今の形式の並びにする。形式が新しすぎる・記録やセットの並びが壊れている・セットの正本が無いときは
        /// InvalidDataException（理由と書いたアプリを添える）。渡した辞書は変えない。
        /// </summary>
        public static YlpOpened Open(IReadOnlyDictionary<string, byte[]> files)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));
            var info = files.TryGetValue(InfoName, out var bytes) ? ReadInfo(bytes) : new YlpFormatInfo(1, null, null);
            CheckReadable(info);
            var upgraded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in files) if (entry.Key != InfoName) upgraded.Add(entry.Key, entry.Value);
            var notes = new List<string>();
            for (int format = info.Format; format < Current; format++) Steps[format - 1](upgraded, notes);
            if (!upgraded.TryGetValue(ProjectName, out var projectBytes)) throw new InvalidDataException("The file has no " + ProjectName + " (its list of texture sets).");
            var project = ReadProject(projectBytes);
            foreach (var set in project.Sets)
                if (!upgraded.ContainsKey(SetEntry(set.Id, YlpArchive.NativeName)))
                    throw new InvalidDataException("Texture set \"" + set.Name + "\" has no native document (" + SetEntry(set.Id, YlpArchive.NativeName) + ").");
            var listed = new HashSet<Guid>(project.Sets.Select(s => s.Id));
            // リソース（形式 4）: 並びにある中身の PNG が無ければ断る。並びに無い PNG は知らないエントリとして知らせる
            var resources = upgraded.TryGetValue(ResourceIndex.EntryName, out var resourceBytes) ? ResourceIndex.Read(resourceBytes) : (IReadOnlyList<YlpResourceEntry>)new YlpResourceEntry[0];
            foreach (var resource in resources)
            {
                if (resource.IsFile)
                {
                    if (!upgraded.ContainsKey(ResourceIndex.FileEntry(resource))) throw new InvalidDataException("Smart material \"" + resource.Name + "\" has no file (" + ResourceIndex.FileEntry(resource) + ").");
                }
                else if (!upgraded.ContainsKey(ResourceIndex.ContentEntry(resource.Content)))
                    throw new InvalidDataException("Resource \"" + resource.Name + "\" has no pixels (" + ResourceIndex.ContentEntry(resource.Content) + ").");
            }
            var contents = new HashSet<string>(resources.Where(r => !r.IsFile).Select(r => r.Content), StringComparer.Ordinal);
            var smartFiles = new HashSet<string>(resources.Where(r => r.IsSmart).Select(r => r.Content), StringComparer.Ordinal);
            var unknown = upgraded.Keys.Where(k => KindOf(k) == null || TrySplitSetEntry(k, out var set, out _) && !listed.Contains(set) || ResourceIndex.TryParseContentEntry(k, out var hash) && !contents.Contains(hash)
                    || ResourceIndex.TryParseSmartEntry(k, out var smartHash) && !smartFiles.Contains(smartHash)
                    || ResourceIndex.TryParseBrushEntry(k, out var brushHash) && !resources.Any(r => r.Kind == Shelf.ResourceKind.Brush && r.Content == brushHash))
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            return new YlpOpened { Files = upgraded, Info = info, Project = project, UnknownEntries = unknown, Notes = notes, Resources = resources };
        }

        /// <summary>保存するエントリに ylp.json（今の形式・保存したアプリ・最初に作ったアプリ）を足す。既にあれば置き換える。</summary>
        public static void Stamp(IDictionary<string, byte[]> files, YlpWriterInfo savedBy, YlpWriterInfo createdBy)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));
            if (savedBy == null) throw new ArgumentNullException(nameof(savedBy));
            files[InfoName] = WriteInfo(new YlpFormatInfo(Current, savedBy, createdBy));
        }

        // ───────── ylp.json ─────────

        public static byte[] WriteInfo(YlpFormatInfo info)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            if (info.Format < 2) throw new ArgumentException("Format 1 has no ylp.json.", nameof(info));
            if (info.SavedBy == null) throw new ArgumentException("ylp.json records the application that saved the file.", nameof(info));
            var s = new StringBuilder("{\n  \"format\": ").Append(info.Format.ToString(CultureInfo.InvariantCulture));
            void Append(string key, YlpWriterInfo w)
            {
                s.Append(",\n  \"").Append(key).Append("\": { \"app\": ").Append(Quote(w.App)).Append(", \"version\": ").Append(Quote(w.Version))
                 .Append(", \"unity\": ").Append(Quote(w.Unity)).Append(" }");
            }
            Append("savedBy", info.SavedBy);
            if (info.CreatedBy != null) Append("createdBy", info.CreatedBy);
            return Encoding.UTF8.GetBytes(s.Append("\n}\n").ToString());
        }

        /// <summary>ylp.json を読む。知らないキーは読み飛ばす（形式の版が同じなら、足したキーは古い読み手に要らない約束）。</summary>
        public static YlpFormatInfo ReadInfo(byte[] bytes)
        {
            var root = ParseObject(bytes, InfoName);
            if (!root.TryGetValue("format", out var formatValue) || !(formatValue is long format) || format < 2 || format > int.MaxValue)
                throw new InvalidDataException("ylp.json has no valid \"format\" (an integer of at least 2).");
            return new YlpFormatInfo((int)format, Writer(root, "savedBy", required: true), Writer(root, "createdBy", required: false));
        }

        static YlpWriterInfo Writer(Dictionary<string, object> root, string key, bool required)
        {
            if (!root.TryGetValue(key, out var value) || value == null)
            {
                if (required) throw new InvalidDataException("ylp.json has no \"" + key + "\".");
                return null;
            }
            if (!(value is Dictionary<string, object> o)) throw new InvalidDataException("ylp.json \"" + key + "\" is not an object.");
            string Text(string field)
            {
                if (!o.TryGetValue(field, out var v) || !(v is string t) || t.Length == 0 || t.Length > MaxText)
                    throw new InvalidDataException("ylp.json \"" + key + "." + field + "\" is not a string of 1–" + MaxText + " characters.");
                return t;
            }
            return new YlpWriterInfo(Text("app"), Text("version"), Text("unity"));
        }

        // ───────── project.json ─────────

        /// <summary>セットの名前の決まり: 1〜256 文字、空白だけでない、制御文字を含まない。合わなければ ArgumentException。</summary>
        public static void CheckSetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A texture set needs a name.", nameof(name));
            if (name.Length > MaxText) throw new ArgumentException("A texture set name is at most " + MaxText + " characters.", nameof(name));
            if (name.Any(c => c < 0x20 || c == 0x7f)) throw new ArgumentException("A texture set name cannot hold control characters.", nameof(name));
        }

        public static byte[] WriteProject(YlpProjectInfo project)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            var s = new StringBuilder("{\n  \"sets\": [");
            for (int i = 0; i < project.Sets.Count; i++)
            {
                var set = project.Sets[i];
                s.Append(i == 0 ? "\n" : ",\n").Append("    { \"id\": ").Append(Quote(set.Id.ToString("D"))).Append(", \"name\": ").Append(Quote(set.Name))
                 .Append(", \"material\": ").Append(WriteMaterial(set.Material)).Append(" }");
            }
            s.Append("\n  ],\n  \"current\": ").Append(Quote(project.CurrentSet.ToString("D"))).Append("\n}\n");
            return Encoding.UTF8.GetBytes(s.ToString());
        }

        static string WriteMaterial(YlpMaterialRef m)
        {
            if (m.Unassigned) return "{ \"unassigned\": true }";
            if (m.IsPendingSlot) return "{ \"slot\": " + m.Slot.ToString(CultureInfo.InvariantCulture) + " }";
            var s = new StringBuilder("{ \"name\": ").Append(Quote(m.Name));
            if (m.AssetGuid != null) s.Append(", \"guid\": ").Append(Quote(m.AssetGuid)).Append(", \"fileId\": ").Append(m.LocalFileId.ToString(CultureInfo.InvariantCulture));
            return s.Append(" }").ToString();
        }

        /// <summary>project.json の "material" を読む。形は 1 つだけ（unassigned が true、slot、name のどれか）。知らないキーは読み飛ばす。</summary>
        static YlpMaterialRef ReadMaterial(object value, string set)
        {
            if (!(value is Dictionary<string, object> o)) throw new InvalidDataException("project.json: texture set \"" + set + "\" has no \"material\" object.");
            bool unassigned = o.TryGetValue("unassigned", out var u) && u is bool b && b;
            if (o.TryGetValue("unassigned", out u) && !(u is bool)) throw new InvalidDataException("project.json: texture set \"" + set + "\": \"unassigned\" is not true or false.");
            bool hasSlot = o.TryGetValue("slot", out var slotValue), hasName = o.TryGetValue("name", out var nameValue);
            if ((unassigned ? 1 : 0) + (hasSlot ? 1 : 0) + (hasName ? 1 : 0) != 1)
                throw new InvalidDataException("project.json: texture set \"" + set + "\": \"material\" needs exactly one of \"name\", \"slot\" and \"unassigned\": true.");
            if (unassigned) return YlpMaterialRef.UnassignedSlots;
            if (hasSlot)
            {
                if (!(slotValue is long slot) || slot < 0 || slot > MaxMaterialSlot) throw new InvalidDataException("project.json: texture set \"" + set + "\": \"slot\" is not an integer of 0–" + MaxMaterialSlot + ".");
                return YlpMaterialRef.PendingSlot((int)slot);
            }
            if (!(nameValue is string name)) throw new InvalidDataException("project.json: texture set \"" + set + "\": the material \"name\" is not a string.");
            string guid = null; long fileId = 0;
            bool hasGuid = o.TryGetValue("guid", out var guidValue), hasFile = o.TryGetValue("fileId", out var fileValue);
            if (hasGuid != hasFile) throw new InvalidDataException("project.json: texture set \"" + set + "\": a material's \"guid\" and \"fileId\" go together.");
            if (hasGuid)
            {
                if (!(guidValue is string g)) throw new InvalidDataException("project.json: texture set \"" + set + "\": the material \"guid\" is not a string.");
                if (!(fileValue is long f)) throw new InvalidDataException("project.json: texture set \"" + set + "\": the material \"fileId\" is not an integer.");
                guid = g; fileId = f;
            }
            try { return YlpMaterialRef.Material(name, guid, fileId); }
            catch (ArgumentException ex) { throw new InvalidDataException("project.json: texture set \"" + set + "\": " + Reason(ex), ex); }
        }

        /// <summary>project.json を読む。知らないキーは読み飛ばす。ID は小文字のハイフン付きの形、セットは 1〜64、ID・名前（大文字小文字を
        /// 区別しない）・マテリアル（識別子・Unassigned・スロットの番号）の重なり、今のセットが並びに無いものは断る（InvalidDataException）。</summary>
        public static YlpProjectInfo ReadProject(byte[] bytes) => ReadProject(bytes, legacySlots: false);
        /// <summary>移行せずに project.json を読む（インポーターの一覧）: 形式 6 までの "materialSlot" も、まだ結び付けていないスロットの番号として読む。</summary>
        public static YlpProjectInfo ReadProjectOfAnyFormat(byte[] bytes) => ReadProject(bytes, legacySlots: true);

        /// <summary>legacySlots: 形式 6 までの "materialSlot"（整数）も読む（"material" が無いセットだけ。移行の段が使う）。</summary>
        static YlpProjectInfo ReadProject(byte[] bytes, bool legacySlots)
        {
            var root = ParseObject(bytes, ProjectName);
            if (!root.TryGetValue("sets", out var setsValue) || !(setsValue is List<object> items)) throw new InvalidDataException("project.json has no \"sets\" list.");
            var sets = new List<YlpTextureSetInfo>();
            foreach (var item in items)
            {
                if (!(item is Dictionary<string, object> o)) throw new InvalidDataException("project.json \"sets\" holds something that is not an object.");
                var id = ReadGuid(o, "id", "a texture set");
                if (!o.TryGetValue("name", out var nameValue) || !(nameValue is string name)) throw new InvalidDataException("project.json: a texture set has no \"name\".");
                YlpMaterialRef material;
                if (legacySlots && !o.ContainsKey("material"))
                {
                    if (!o.TryGetValue("materialSlot", out var slotValue) || !(slotValue is long slot) || slot < 0 || slot > MaxMaterialSlot)
                        throw new InvalidDataException("project.json: texture set \"" + name + "\" has no \"materialSlot\" of 0–" + MaxMaterialSlot + ".");
                    material = YlpMaterialRef.PendingSlot((int)slot);
                }
                else material = ReadMaterial(o.TryGetValue("material", out var m) ? m : null, name);
                try { sets.Add(new YlpTextureSetInfo(id, name, material)); }
                catch (ArgumentException ex) { throw new InvalidDataException("project.json: " + Reason(ex), ex); }
            }
            var current = ReadGuid(root, "current", "the current texture set");
            try { return new YlpProjectInfo(sets, current); }
            catch (ArgumentException ex) { throw new InvalidDataException("project.json: " + Reason(ex), ex); }
        }

        /// <summary>ArgumentException の文から引数の名前の行を除いたもの。</summary>
        static string Reason(ArgumentException ex) => ex.Message.Split('\n')[0].Split(new[] { " (Parameter" }, StringSplitOptions.None)[0];

        static Guid ReadGuid(Dictionary<string, object> o, string key, string what)
        {
            if (!o.TryGetValue(key, out var value) || !(value is string text) || !Guid.TryParseExact(text, "D", out var id) || id.ToString("D") != text || id == Guid.Empty)
                throw new InvalidDataException("project.json: " + what + " has no valid \"" + key + "\" (a lower-case GUID with hyphens).");
            return id;
        }

        // ───────── 共通 ─────────

        /// <summary>UTF-8 の JSON のオブジェクトを読む（<paramref name="maxBytes"/>、既定は <see cref="MaxInfoBytes"/> まで）。</summary>
        internal static Dictionary<string, object> ParseObject(byte[] bytes, string entry, int maxBytes = MaxInfoBytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length > maxBytes) throw new InvalidDataException(entry + " is larger than " + (maxBytes >> 10) + " KiB.");
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { throw new InvalidDataException(entry + " is not valid UTF-8."); }
            if (!(new Json(text, entry).ParseDocument() is Dictionary<string, object> root)) throw new InvalidDataException(entry + " is not a JSON object.");
            return root;
        }

        internal static string Quote(string value)
        {
            var s = new StringBuilder("\"");
            foreach (char c in value)
            {
                if (c == '"' || c == '\\') s.Append('\\').Append(c);
                else if (c < 0x20) s.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else s.Append(c);
            }
            return s.Append('"').ToString();
        }

        /// <summary>小さな JSON の読み手（オブジェクト・配列・文字列・整数・小数・true/false/null。深さと長さに上限）。</summary>
        sealed class Json
        {
            readonly string text, entry; int at;
            const int MaxDepth = 16;
            public Json(string text, string entry) { this.text = text; this.entry = entry; }

            public object ParseDocument()
            {
                var value = Value(0); Space();
                if (at != text.Length) throw Error("unexpected text after the value");
                return value;
            }
            InvalidDataException Error(string what) => new InvalidDataException(entry + " is not valid JSON (" + what + " at character " + at + ").");
            void Space() { while (at < text.Length && (text[at] == ' ' || text[at] == '\t' || text[at] == '\n' || text[at] == '\r')) at++; }
            bool Take(char c) { Space(); if (at < text.Length && text[at] == c) { at++; return true; } return false; }
            void Expect(char c) { if (!Take(c)) throw Error("'" + c + "' expected"); }

            object Value(int depth)
            {
                if (depth > MaxDepth) throw Error("nested too deeply");
                Space();
                if (at >= text.Length) throw Error("a value expected");
                char c = text[at];
                if (c == '{')
                {
                    at++; var o = new Dictionary<string, object>(StringComparer.Ordinal);
                    if (Take('}')) return o;
                    do
                    {
                        Space(); if (at >= text.Length || text[at] != '"') throw Error("a key expected");
                        string key = String(); Expect(':');
                        if (o.ContainsKey(key)) throw Error("duplicate key \"" + key + "\"");
                        o.Add(key, Value(depth + 1));
                    } while (Take(','));
                    Expect('}'); return o;
                }
                if (c == '[')
                {
                    at++; var list = new List<object>();
                    if (Take(']')) return list;
                    do list.Add(Value(depth + 1)); while (Take(','));
                    Expect(']'); return list;
                }
                if (c == '"') return String();
                if (Word("true")) return true;
                if (Word("false")) return false;
                if (Word("null")) return null;
                return Number();
            }
            bool Word(string w) { if (string.CompareOrdinal(text, at, w, 0, w.Length) == 0) { at += w.Length; return true; } return false; }
            string String()
            {
                at++; var s = new StringBuilder();
                while (true)
                {
                    if (at >= text.Length) throw Error("unterminated string");
                    char c = text[at++];
                    if (c == '"') break;
                    if (c < 0x20) throw Error("control character in a string");
                    if (c != '\\') { s.Append(c); if (s.Length > MaxText * 4) throw Error("string too long"); continue; }
                    if (at >= text.Length) throw Error("unterminated escape");
                    char e = text[at++];
                    switch (e)
                    {
                        case '"': s.Append('"'); break; case '\\': s.Append('\\'); break; case '/': s.Append('/'); break;
                        case 'b': s.Append('\b'); break; case 'f': s.Append('\f'); break; case 'n': s.Append('\n'); break;
                        case 'r': s.Append('\r'); break; case 't': s.Append('\t'); break;
                        case 'u':
                            if (at + 4 > text.Length || !ushort.TryParse(text.Substring(at, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort u)) throw Error("bad \\u escape");
                            s.Append((char)u); at += 4; break;
                        default: throw Error("unknown escape \\" + e);
                    }
                }
                return s.ToString();
            }
            object Number()
            {
                int start = at;
                if (at < text.Length && text[at] == '-') at++;
                while (at < text.Length && (char.IsDigit(text[at]) || text[at] == '.' || text[at] == 'e' || text[at] == 'E' || text[at] == '+' || text[at] == '-')) at++;
                string n = text.Substring(start, at - start);
                if (n.Length == 0) throw Error("a value expected");
                if (long.TryParse(n, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
                if (double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !double.IsInfinity(d)) return d;
                throw Error("bad number");
            }
        }
    }
}
