using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// Generator（メッシュマップから値を作る段）。フィルターと同じ層のスタック（画素へ・マスクへ）に入り、足す・消す・並べ替え・オン/オフ・強さ・
    /// パラメーターの編集はフィルターと同じ口（文書の Undo に 1 回ずつ。スライダーのドラッグは 1 回にまとめる）。
    /// <para>入力: テクスチャセットごとに <see cref="SetGeneratorInputs"/> を文書の <see cref="PaintDocument.GeneratorInputs"/> に差す。答えるのは
    /// そのセットの焼いたマップのうち今の条件で焼いたものだけ（<see cref="MeshMapSet.TryGetUsable"/>。モデル・高ポリ・ベイクの設定・大きさ・
    /// スロットが同じ）で、古い・無い・照合できないマップは理由付きで断る。文書は断られた Generator を入力のまま通す（黒にしない）。</para>
    /// <para>変更の伝播: 口の版は、マップの組の版・モデルと高ポリの読み込み・ベイクの設定・スロット・文書のどれかが変わると上がる。窓は
    /// エディタの update で 0.1 秒ごとに全部のセットの文書に問い合わせ（<see cref="PollGeneratorInputs"/>）、読むマップが変わった文書があれば
    /// 合成し直させ、ほかのセットの 3D の表示・サムネイル・マテリアル表示の写し・照明を作り直させる。文書の側も合成のたびに問い合わせる
    /// （合成器の TryGetChangedTiles）ので、窓の update を待たずに合成したときも古いマップのタイルは使わない。</para>
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>テクスチャセットの焼いたマップを、そのセットの文書の Generator に渡す口。</summary>
        internal sealed class SetGeneratorInputs : IGeneratorInputs, IGeneratorModelFrame
        {
            readonly TexturePaintWindow window; readonly TextureSet set;
            long revision, mapsRevision = long.MinValue; int slot = int.MinValue;
            object input, highPoly; string settings; PaintDocument doc; Vector3 rootPosition; Quaternion rootRotation; bool hasRoot;
            /// <summary>窓が最後に見た文書と、その文書の <see cref="PaintDocument.GeneratorInputsRevision"/>（合成器が先に問い合わせて
            /// 変化を受け取っていても、窓の写しを作り直し損ねないように、窓は文書の版で比べる）。</summary>
            internal PaintDocument SeenDocument; internal long SeenRevision;
            public SetGeneratorInputs(TexturePaintWindow window, TextureSet set) { this.window = window; this.set = set; }

            /// <summary>答えが変わりうるものの指紋が変わるたびに上がる（ベイクの設定は全部の値を JSON にして比べる。設定の値を足しても漏れない）。</summary>
            public long Revision
            {
                get
                {
                    if (window == null) return revision; // 閉じた窓: もう変わらない
                    var nowInput = window.CurrentMeshBakeInput(); var nowHigh = window.highPolyModel != null ? window.CurrentHighPolyInput() : null;
                    string nowSettings = JsonUtility.ToJson(window.meshBakeSettings);
                    bool current = set == window.currentSet;
                    int nowSlot = current ? window.materialSlot : set.MaterialSlot;
                    var nowDoc = current && window.document != null ? window.document : set.Document;
                    bool nowHasRoot = window.preview != null && window.preview.HasModel;
                    var nowRootPosition = nowHasRoot ? window.preview.ModelRootPosition : Vector3.zero; var nowRootRotation = nowHasRoot ? window.preview.ModelRootRotation : Quaternion.identity;
                    if (set.MeshMaps.Revision != mapsRevision || !ReferenceEquals(nowInput, input) || !ReferenceEquals(nowHigh, highPoly) || nowSettings != settings || nowSlot != slot || !ReferenceEquals(nowDoc, doc)
                        || nowHasRoot != hasRoot || nowRootPosition != rootPosition || nowRootRotation != rootRotation)
                    {
                        mapsRevision = set.MeshMaps.Revision; input = nowInput; highPoly = nowHigh; settings = nowSettings; slot = nowSlot; doc = nowDoc;
                        hasRoot = nowHasRoot; rootPosition = nowRootPosition; rootRotation = nowRootRotation;
                        revision++;
                    }
                    return revision;
                }
            }
            public bool TryGetMap(MeshMapKind kind, out BakedMeshMap map, out string reason)
            {
                if (window == null) { map = null; reason = "The YoluPainter window that held the mesh maps was closed."; return false; }
                return set.MeshMaps.TryGetUsable(kind, window.MeshMapExpectationFor(set), out map, out reason);
            }
            /// <summary>読み込んだモデルのルートの位置と向き（プレビューの空間 = 焼いたマップの空間）。モデルが無ければ null（そのときマップも
            /// 照合できないので使われない）。形のグラデーションが形をルートの空間に置くのに使う。</summary>
            public GeneratorModelFrame ModelFrame
            {
                get
                {
                    if (window == null || window.preview == null || !window.preview.HasModel) return null;
                    var p = window.preview.ModelRootPosition; var q = window.preview.ModelRootRotation;
                    return new GeneratorModelFrame(p.x, p.y, p.z, q.x, q.y, q.z, q.w);
                }
            }
        }

        readonly Dictionary<TextureSet, SetGeneratorInputs> generatorInputs = new Dictionary<TextureSet, SetGeneratorInputs>();
        bool generatorTickHooked; double lastGeneratorPoll;
        /// <summary>窓の update で Generator の入力を見る間隔（秒）。</summary>
        const double GeneratorPollInterval = .1;

        /// <summary>全部のテクスチャセットの文書に、そのセットのマップの口を差す（差してあれば何もしない）。消えたセットの口は捨てる。
        /// 窓の update に問い合わせを 1 度だけつなぐ（ドメインのリロードでつなぎ直す: OnEnable がセットを読むとここを通る）。</summary>
        void ConnectGeneratorInputs()
        {
            foreach (var set in textureSets)
            {
                var d = set == currentSet && document != null ? document : set.Document;
                if (d == null) continue;
                if (!generatorInputs.TryGetValue(set, out var inputs)) generatorInputs[set] = inputs = new SetGeneratorInputs(this, set);
                if (!ReferenceEquals(d.GeneratorInputs, inputs)) d.GeneratorInputs = inputs;
            }
            if (generatorInputs.Count > textureSets.Count) foreach (var gone in generatorInputs.Keys.Where(s => !textureSets.Contains(s)).ToList()) generatorInputs.Remove(gone);
            if (!generatorTickHooked) { EditorApplication.update -= GeneratorTick; EditorApplication.update += GeneratorTick; generatorTickHooked = true; }
        }

        void GeneratorTick()
        {
            if (this == null) { EditorApplication.update -= GeneratorTick; return; } // 閉じた窓
            if (document == null || stroke != null) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - lastGeneratorPoll < GeneratorPollInterval) return;
            lastGeneratorPoll = now;
            PollGeneratorInputs();
        }

        /// <summary>全部のセットの文書に、Generator が読むマップが変わったかを問い合わせる。前に見たときから読むマップが変わった文書（合成器が
        /// 先に受け取った変化も含む）があれば、合成し直させ、表示の写し（ほかのセットの 3D・サムネイル・マテリアル表示・照明）を作り直させて
        /// true。Generator の無い文書は問い合わせない。</summary>
        internal bool PollGeneratorInputs()
        {
            if (document == null || currentSet == null) return false;
            ConnectGeneratorInputs();
            bool changed = false;
            foreach (var set in textureSets)
            {
                var d = set == currentSet ? document : set.Document;
                if (d == null || !generatorInputs.TryGetValue(set, out var inputs)) continue;
                long revision = d.GeneratorInputsRevision; // 文書が入力を問い合わせ、読むマップが変わっていれば Generator の層を「変わった」にする
                if (ReferenceEquals(inputs.SeenDocument, d) && inputs.SeenRevision == revision) continue;
                bool first = !ReferenceEquals(inputs.SeenDocument, d);
                inputs.SeenDocument = d; inputs.SeenRevision = revision;
                if (first && !d.HasGenerators) continue; // 初めて見た文書で、Generator も無い
                changed = true;
                set.DisplayKey = null; set.LightingKey = null; set.ThumbnailRevision = long.MinValue;
                foreach (var c in set.MaterialChannels.Values) c.Revision = long.MinValue;
            }
            if (!changed) return false;
            repaintPixels = true; lightingRevision = -1;
            Repaint(); RepaintPanelWindowsSoon();
            return true;
        }

        // ───────── 足す ─────────

        static readonly GeneratorType[] GeneratorMenu = { GeneratorType.EdgeWear, GeneratorType.Dirt, GeneratorType.PositionGradient, GeneratorType.ShapeGradient, GeneratorType.Thickness, GeneratorType.Direction, GeneratorType.IdColor };

        /// <summary>選んだ層のスタックに Generator を足す（画素なら今のチャンネルだけ）。足せたら知らせに、読むマップが無ければその理由も添える。</summary>
        internal FilterEffect AddGenerator(FilterTarget target, GeneratorType type)
        {
            var added = AddFilter(target, FilterSettings.FromGenerator(NewGeneratorSettings(type)));
            if (added == null) return null;
            ConnectGeneratorInputs();
            if (type == GeneratorType.ShapeGradient) ShapeEditFilter = added.Id; // 足したらすぐ 3D ビューで形を動かせるように
            var status = document.GetGeneratorStatus(added.Settings.Generator);
            if (!status.Active) message += " " + L.Tr("It has no effect until its mesh maps are baked: {0}", status.Reason);
            return added;
        }

        /// <summary>Generator を足すメニューの項目: 訳した名前、種類、断られるならその理由（チャンネルの型・層の種類・マスクの有無）。</summary>
        internal List<(string label, GeneratorType type, string refusal)> GeneratorChoices(Guid layerId, FilterTarget target)
        {
            var choices = new List<(string, GeneratorType, string)>();
            foreach (var type in GeneratorMenu)
            {
                string why;
                try { why = document.FilterRefusal(layerId, target, FilterSettings.FromGenerator(GeneratorSettings.Default(type)), channel); }
                catch (KeyNotFoundException) { why = L.Tr("no layer"); }
                choices.Add((GeneratorName(type), type, why));
            }
            return choices;
        }
        void ShowGeneratorMenu(Rect at, Guid layerId, FilterTarget target)
        {
            var menu = new GenericMenu();
            foreach (var (label, type, why) in GeneratorChoices(layerId, target))
            {
                if (why == null) menu.AddItem(new GUIContent(label), false, () => { AddGenerator(target, type); Repaint(); });
                else menu.AddDisabledItem(new GUIContent(label + " — " + why));
            }
            menu.DropDown(at);
        }

        // ───────── 名前 ─────────

        internal static string GeneratorName(GeneratorType type)
        {
            switch (type)
            {
                case GeneratorType.EdgeWear: return L.Tr("Edge wear");
                case GeneratorType.Dirt: return L.Tr("Dirt");
                case GeneratorType.PositionGradient: return L.Tr("Position gradient");
                case GeneratorType.ShapeGradient: return L.Tr("Shape gradient");
                case GeneratorType.Thickness: return L.TrIn("generator", "Thickness");
                case GeneratorType.IdColor: return L.Tr("ID color");
                default: return L.TrIn("generator", "Direction");
            }
        }
        static string GeneratorBlendName(GeneratorBlend blend)
        {
            switch (blend)
            {
                case GeneratorBlend.Multiply: return L.TrIn("generator", "Multiply");
                case GeneratorBlend.Replace: return L.TrIn("generator", "Replace");
                case GeneratorBlend.Screen: return L.TrIn("generator", "Screen");
                case GeneratorBlend.Max: return L.TrIn("generator", "Max");
                case GeneratorBlend.Min: return L.TrIn("generator", "Min");
                case GeneratorBlend.Add: return L.TrIn("generator", "Add");
                default: return L.TrIn("generator", "Subtract");
            }
        }
        static string NoiseSpaceName(GeneratorNoiseSpace space) => space == GeneratorNoiseSpace.Model ? L.Tr("On the model (3D)") : L.Tr("UV (seams show)");
        static string AxisName(int axis) => axis == 0 ? "X" : axis == 1 ? "Y" : "Z";
        /// <summary>向きの選択肢（ワールドの軸の 6 方向）。</summary>
        static readonly (double x, double y, double z)[] DirectionChoices = { (0, 1, 0), (0, -1, 0), (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1) };
        static string DirectionName((double x, double y, double z) d)
        {
            if (d.y > 0) return L.Tr("Up (+Y)"); if (d.y < 0) return L.Tr("Down (−Y)");
            if (d.x > 0) return "+X"; if (d.x < 0) return "−X";
            return d.z > 0 ? "+Z" : "−Z";
        }
        /// <summary>一覧の 1 行の要約（名前は <see cref="FilterLabel"/> が付ける）: 効いていなければ印。</summary>
        string GeneratorSummary(FilterEffect e)
        {
            var g = e.Settings.Generator;
            string text = "  " + GeneratorBlendName(g.Blend);
            if (e.IsActive && !document.GetGeneratorStatus(g).Active) text += "  · " + L.Tr("no maps");
            return text;
        }

        /// <summary>焼いたマップの由来の要約（ツールチップ）: 大きさ・スロット・焼く元・その種類の条件。</summary>
        static string ProvenanceSummary(BakedMeshMap map)
        {
            var p = map.Provenance;
            string source = p.Source == MeshBaker.Source ? L.Tr("baked from the model") : L.Tr("baked from a high poly");
            return MeshMapLabel(p.Kind) + " · " + p.Width + " × " + p.Height + " · " + L.Tr("slot {0}", p.TargetSlot) + " · " + source + (string.IsNullOrEmpty(p.SettingsKey) ? "" : " · " + p.SettingsKey.Replace(";", "; "))
                + "\n" + L.Tr("Bake {0}", p.ConditionKey.Substring(0, 12) + "…");
        }

        // ───────── 設定の欄 ─────────

        /// <summary>選んだ Generator の設定: 読むマップと状態（使える/無い/古い と理由、ベイクの窓を開く口、このベイクだけを読む）、種類ごとの値、
        /// レベル（低・高・やわらかさ）と反転、崩し（量・大きさ・シード・置き場）、入力との合成。強さはフィルターと共通の行。</summary>
        void DrawGeneratorParameters(UiRows rows, FilterEffect e, float indent)
        {
            var g = e.Settings.Generator; GeneratorSettings next = g;
            var status = document.GetGeneratorStatus(g);
            PaintGui.GroupLabel(Indent(rows.Row(16), indent), L.Tr("Reads"), L.Tr("The texture set's baked mesh maps this generator reads. They come from the model; painting (Height too) does not change them."));
            foreach (var use in status.Maps)
            {
                var row = Indent(rows.Row(20, 2), indent);
                var shown = use.Map ?? use.Available;
                string state = use.Usable ? (use.Pin != null ? L.Tr("this bake") : L.TrIn("mesh map", "current")) : use.Pin != null && use.Available != null ? L.Tr("other bake")
                    : MeshMapStateName(meshMaps.Check(use.Kind, CurrentMeshMapExpectation()).State);
                var color = use.Usable ? new Color(.35f, .78f, .42f) : PaintTheme.Warning;
                PaintGui.Dot(new Rect(row.x, row.y, 12, row.height), color);
                float right = PaintGui.TextWidth(state, PaintTheme.LabelDim) + 4;
                PaintGui.Text(new Rect(row.x + 18, row.y, row.width - 18 - right, row.height), PaintGui.Fit(MeshMapLabel(use.Kind), row.width - 22 - right, PaintTheme.Label), PaintTheme.Label);
                PaintGui.Text(new Rect(row.xMax - right, row.y, right, row.height), state, StateStyle, color);
                PaintGui.Tooltip(Spot("generator.map." + use.Kind, row), shown != null ? ProvenanceSummary(shown) : use.Reason);
            }
            if (!status.Active)
            {
                NoteRow(rows, L.Tr("No effect now: the input passes through unchanged. {0}", status.Reason), NoteKind.Warning, indent);
                if (meshBakeJob == null && PaintGui.Button(Spot("generator.bake", Indent(rows.Row(24), indent)), L.Tr("Bake Mesh Maps…"), false, GUI.enabled && stroke == null,
                        L.Tr("Open the bake window: check the maps, set them up and bake them from the loaded model"), "local_fire_department"))
                    TryAction(() => OpenMeshBakeWindow());
            }
            bool pinned = g.Pins.Count > 0;
            bool pin = PaintGui.FitToggle(Spot("generator.pin", Indent(rows.Row(), indent)), L.Tr("Only this bake"), pinned,
                L.Tr("On: keep reading exactly the bake shown above. A rebake under other conditions is then not used (the generator has no effect until you turn this on again). Off: follow the latest bake."),
                pinned || status.Maps.All(m => m.Usable));
            try
            {
                if (pin != pinned)
                {
                    var pinnedNext = g.WithoutPins();
                    if (pin) foreach (var use in status.Maps) pinnedNext = pinnedNext.WithPin(use.Kind, use.Map.Provenance.ConditionKey);
                    next = pinnedNext;
                }
                switch (g.Type)
                {
                    case GeneratorType.EdgeWear:
                    case GeneratorType.Dirt:
                        if (g.Type == GeneratorType.Dirt)
                            next = next.WithBalance(PaintGui.KeepSlider(Spot("generator.balance", Indent(rows.Row(), indent)), L.Tr("AO ↔ Cavities"), next.Balance, 0, 1, "0", "%",
                                L.Tr("0 %: ambient occlusion only. 100 %: cavities (concave curvature) only."), true, 100));
                        NoteRow(rows, L.Tr("Curvature comes from the model's bake; painted Height does not change it."), NoteKind.Plain, indent);
                        break;
                    case GeneratorType.PositionGradient:
                        ChoiceDropdown(Spot("generator.axis", Indent(rows.Row(), indent)), L.Tr("Axis"), next.Axis, new[] { 0, 1, 2 }, AxisName,
                            a => { if (a != g.Axis) ApplyFilterSettings(e.Id, e.Settings.WithGenerator(g.WithAxis(a))); },
                            L.Tr("0 at the bounding box's minimum, 1 at its maximum along this world axis"));
                        break;
                    case GeneratorType.ShapeGradient:
                        next = ShapeGradientRows(rows, e, next, indent); // 形・置き場・シーンから写す（TexturePaintWindow.ShapeGradient.cs）
                        break;
                    case GeneratorType.IdColor:
                        next = IdColorRows(rows, e, next, indent); // 色の一覧・スポイト・許容の幅（Tools/TexturePaintWindow.IdSelect.cs）
                        break;
                    case GeneratorType.Direction:
                    {
                        (double x, double y, double z) current = (next.DirectionX, next.DirectionY, next.DirectionZ);
                        var choices = DirectionChoices.Contains(current) ? DirectionChoices : DirectionChoices.Concat(new[] { current }).ToArray();
                        ChoiceDropdown(Spot("generator.direction", Indent(rows.Row(), indent)), L.TrIn("generator", "Direction"), current, choices,
                            d => DirectionChoices.Contains(d) ? DirectionName(d) : string.Format(CultureInfo.InvariantCulture, "({0:0.##}, {1:0.##}, {2:0.##})", d.x, d.y, d.z),
                            d => { if (d != current) ApplyFilterSettings(e.Id, e.Settings.WithGenerator(g.WithDirection(d.x, d.y, d.z))); },
                            L.Tr("Faces turned toward this world direction get 1, faces at right angles 0.5, faces turned away 0"));
                        next = next.WithBentNormal(PaintGui.FitToggle(Spot("generator.bent", Indent(rows.Row(), indent)), L.TrIn("mesh map", "Bent normal"), next.UseBentNormal,
                            L.Tr("Read the bent normal (the open directions) instead of the surface normal")));
                        break;
                    }
                }
                // レベル: 低と高は入れ違わない（0.001 以上離す）。ID の色は 0 か 1 なので、範囲とやわらかさは出さない（反転は出す）
                if (g.Type != GeneratorType.IdColor)
                {
                    var c = PaintGui.LabeledColumns(Indent(rows.Row(), indent), L.TrIn("generator", "Range"), PropertyLabelWidth, 2);
                    double low = PaintGui.KeepSlider(Spot("generator.low", c[0]), L.TrIn("generator", "Low"), next.Low, 0, 1, "0.###", "", L.Tr("Base values at or below this give 0"));
                    double high = PaintGui.KeepSlider(Spot("generator.high", c[1]), L.TrIn("generator", "High"), next.High, 0, 1, "0.###", "", L.Tr("Base values at or above this give 1"));
                    if (low != next.Low) low = Math.Max(0, Math.Min(low, next.High - GeneratorSettings.MinLevelRange));
                    if (high != next.High) high = Math.Min(1, Math.Max(high, low + GeneratorSettings.MinLevelRange));
                    double softness = PaintGui.KeepSlider(Spot("generator.softness", Indent(rows.Row(), indent)), L.TrIn("generator", "Softness"), next.Softness, 0, 1, "0", "%",
                        L.Tr("0 %: a straight ramp from low to high. 100 %: a smooth S curve."), true, 100);
                    next = next.WithLevels(low, high, softness);
                }
                next = next.WithInvert(PaintGui.FitToggle(Spot("generator.invert", Indent(rows.Row(), indent)), L.TrIn("generator", "Invert"), next.Invert, L.Tr("Swap 0 and 1 after the range")));
                // 崩し
                PaintGui.GroupLabel(Indent(rows.Row(16), indent), L.Tr("Breakup"), L.Tr("Seeded noise that wears the result away in patches, so edges and dirt do not look uniform (100 %: gone where the noise is strongest). The same seed always gives the same result."));
                var b = UiRows.Split(Indent(rows.Row(), indent), 2, 6);
                double amount = PaintGui.KeepSlider(Spot("generator.noise", b[0]), L.TrIn("filter", "Amount"), next.NoiseAmount, 0, 1, "0", "%", null, true, 100);
                int seed = PaintGui.KeepIntField(Spot("generator.seed", b[1]), L.TrIn("filter", "Seed"), next.NoiseSeed, int.MinValue, int.MaxValue, L.Tr("The same seed gives the same noise. Drag to change, click to type."));
                double scale = PaintGui.KeepSlider(Spot("generator.scale", Indent(rows.Row(), indent)), L.TrIn("generator", "Size"), next.NoiseScale, .005, .5, "0.###", "", L.Tr("The size of the largest features, as a fraction of the model's bounding-box diagonal (or of the UV square)"), amount > 0);
                next = next.WithNoise(amount, scale, seed, next.NoiseSpace);
                ChoiceDropdown(Spot("generator.space", Indent(rows.Row(), indent)), L.TrIn("generator", "Placed"), next.NoiseSpace, (GeneratorNoiseSpace[])Enum.GetValues(typeof(GeneratorNoiseSpace)), NoiseSpaceName,
                    s => { if (s != g.NoiseSpace) ApplyFilterSettings(e.Id, e.Settings.WithGenerator(g.WithNoise(g.NoiseAmount, g.NoiseScale, g.NoiseSeed, s))); },
                    L.Tr("On the model: continuous across UV seams (reads the Position map). UV: needs no map, but jumps where UV islands meet."), amount > 0);
                ChoiceDropdown(Spot("generator.blend", Indent(rows.Row(), indent)), L.Tr("Combine"), g.Blend, (GeneratorBlend[])Enum.GetValues(typeof(GeneratorBlend)), GeneratorBlendName,
                    m => { if (m != g.Blend) ApplyFilterSettings(e.Id, e.Settings.WithGenerator(g.WithBlend(m))); },
                    L.Tr("How the value meets what is below it in the stack (on a mask: how much of the layer shows; white shows)"));
            }
            catch (ArgumentException ex) { message = ex.Message; next = g; }
            if (!next.Equals(g)) ApplyFilterSettings(e.Id, e.Settings.WithGenerator(next), coalesce: true);
        }

        /// <summary>プロパティの欄の区画（"layer"・"mask"・"filters" など）を開く・閉じる（オフスクリーンで描く試験が、見たい区画を上に出すため）。</summary>
        internal void SetSectionOpen(string key, bool open) => sectionOpen[key] = open;
        /// <summary>選んだ層のフィルターの区画の中身だけを area に描く（欄と同じ部品と幅。オフスクリーンで見た目を確かめる試験用）。使った高さ。</summary>
        internal float DrawFilterSectionOnly(Rect area)
        {
            var active = document.Layers.FirstOrDefault(l => l.Id == selectedLayer);
            PaintGui.Fill(area, PaintTheme.PanelBg);
            if (active == null) return 0;
            var rows = new UiRows(area, 6);
            DrawFilters(rows, active);
            return rows.Used;
        }

        /// <summary>書き出し・保存の前に: 効いていない Generator（マップが無い・古い）の一覧。無ければ null。</summary>
        static string InactiveGeneratorList(IEnumerable<(string set, PaintDocument document)> documents, bool namedSets)
        {
            var lines = new List<string>();
            foreach (var (set, d) in documents) foreach (var line in d.InactiveGenerators()) lines.Add((namedSets ? set + ": " : "") + line);
            return lines.Count == 0 ? null : string.Join("\n", lines.Take(12)) + (lines.Count > 12 ? "\n… " + (lines.Count - 12) : "");
        }
        /// <summary>書き出す前に、効いていない Generator があれば確かめる（入力のまま書き出すか）。続けてよければ true。</summary>
        bool ConfirmInactiveGenerators(IEnumerable<(string set, PaintDocument document)> documents)
        {
            ConnectGeneratorInputs();
            string list = InactiveGeneratorList(documents, textureSets.Count > 1);
            if (list == null) return true;
            if (Dialogs.Confirm(L.Tr("Generators without mesh maps"), L.Tr("These generators have no usable mesh maps, so the exported images would not have their effect (the layers pass through unchanged):\n{0}\n\nBake the mesh maps first, or export anyway?", list), L.Tr("Export Anyway"), L.Tr("Cancel")))
                return true;
            message = L.Tr("Nothing was exported: some generators have no usable mesh maps. Bake them (Properties ▸ Filters ▸ the generator) first.");
            return false;
        }
        /// <summary>保存の知らせに添える文（.ylp には Generator がそのまま残る。合成の画像だけが効きなし）。無ければ空。</summary>
        string InactiveGeneratorSaveNote()
        {
            int count = textureSets.Sum(s => (s == currentSet ? document : s.Document).InactiveGenerators().Count);
            return count == 0 ? "" : " " + L.Tr("{0} generator(s) have no usable mesh maps; they are kept in the file, and the preview images inside it are without their effect.", count);
        }
    }
}
