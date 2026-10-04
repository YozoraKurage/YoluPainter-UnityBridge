using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Paths;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// モデルの差し替え（Substance Painter のメッシュの読み直し）: 描いたものを保ったまま、プロジェクトを新しいモデル（または読み直した同じ
    /// アセット）に付け替える。
    /// <list type="number">
    /// <item>新しいモデルを窓の外のプレビューで読み、何が変わるかを計画する（何も変えない）: テクスチャセットとマテリアルの対応（識別子 →
    /// 名前。<see cref="MatchMaterials"/>。プロジェクト設定で選んだ対応があればそれ）、セットごとの UV が前と同じか（違えば覆う所の重なり）、
    /// 3D のパスを新しいメッシュに描き直せるか（<see cref="SurfacePathRebind"/>。描けなければ画素にする）、今は使える焼いたメッシュマップが古くなるか、
    /// セットの無い新しいマテリアル、ポーズ。</item>
    /// <item>確かめのダイアログに一覧を出し、断られたら何も変えない。セットの無いマテリアルがあれば、空のセットを足すかを尋ねる。</item>
    /// <item>1 回で入れる: セットの鍵を新しいマテリアルに、窓のプレビューに新しいモデルを読み、パスを描き直す・画素にする（セットごとに 1 回の
    /// Undo）、セットを足す。モデルの差し替えそのものは Undo できない（大きさの変更と同じ。保存したファイルは保存するまで前のモデル）。描いた画素と
    /// 各セットの Undo の履歴は残る。元のモデル・マテリアル・メッシュには触れない。</item>
    /// </list>
    /// 形のグラデーション・投影の置き場はモデルのルートの空間なので、そのまま残る（ルートの置き方が同じなら同じ所）。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>プレビューがモデルを読んだ（準備が済んだ）とき: セットをマテリアルの組に結び付け直す（鍵は変えない）。</summary>
        void PreviewLoaded() => ResolveSetMaterials(refineKeys: false);

        /// <summary>別に読んだプレビュー（新規プロジェクトのダイアログ・差し替えの計画）を窓のプレビューにする。前のプレビューは捨て、窓の設定
        /// （描き直しの回数・マテリアルの欄の値・描き方・擬似的なシーン）を入れ直す。</summary>
        void AdoptPreview(IsolatedModelPreview next)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            if (ReferenceEquals(next, preview)) return;
            preview?.Dispose(); preview = next; preview.Loaded += PreviewLoaded;
            // スナップショットの版で覚えたもの（版の数は別のプレビューと重なりうる）を捨てる
            reconciledSnapshot = -1; modelTiles = null;
            ApplyPreviewFrameRate(); preview.MaterialEdits = materialEdits; preview.Shading = previewShading; BindPreviewScene();
            ResolveSetMaterials(refineKeys: false);
            ResetVisibility(false);
        }

        /// <summary>3D のパス 1 つの扱い。</summary>
        internal enum PathOutcome
        {
            /// <summary>新しいメッシュでも同じ三角形と UV（指紋が同じ）。何もしない。</summary>
            Unchanged,
            /// <summary>新しいメッシュに置き直して描き直す。</summary>
            Redrawn,
            /// <summary>置き直せない: 今の画素のまま、パスを外す。</summary>
            Rasterized,
            /// <summary>ロックで描き直しも画素にすることもできない: 前のモデルに結び付いたまま残す。</summary>
            KeptLocked,
        }

        internal sealed class PathPlan
        {
            public Guid Layer; public string LayerName; public PathOutcome Outcome; public string Reason;
            public SurfacePath Path; public SparseTileSurface Rendered;
        }

        /// <summary>差し替えでの 1 つのテクスチャセット。</summary>
        internal sealed class SetPlan
        {
            public TextureSet Set;
            /// <summary>新しいモデルのマテリアルの組（無ければ −1: モデルに無い）。</summary>
            public int Group = -1;
            public string Before, After;
            /// <summary>前のスロットと新しいスロットの UV の比べ（前か後ろが無ければ null）。</summary>
            public UvComparison Uv;
            /// <summary>今は使えて、差し替えで古くなる焼いたマップの数。</summary>
            public int MapsTurningStale;
            public readonly List<PathPlan> Paths = new List<PathPlan>();
        }

        /// <summary>モデルの差し替えの計画（作るだけでは何も変えない）。新しいモデルを読んだプレビュー（<see cref="Candidate"/>）を持つので、
        /// 使い終わったら Dispose する。</summary>
        internal sealed class ModelChangePlan : IDisposable
        {
            public GameObject Model; public bool Reload;
            /// <summary>同じアセットを読み直す（計画したときのモデルと同じ）。</summary>
            public bool SameAsset;
            public IsolatedModelPreview Candidate;
            public readonly List<SetPlan> Sets = new List<SetPlan>();
            /// <summary>新しいモデルのマテリアルのうち、どのセットも描かないもの。</summary>
            public readonly List<PreviewMaterialGroup> Unused = new List<PreviewMaterialGroup>();
            /// <summary>前のモデルにポーズを当てていた（パスは当てた形で比べた）。</summary>
            public bool WasPosed;
            public string Fingerprint;
            public void Dispose() { Candidate?.Dispose(); Candidate = null; }
            public int Redrawn => Sets.Sum(s => s.Paths.Count(p => p.Outcome == PathOutcome.Redrawn));
            public int Rasterized => Sets.Sum(s => s.Paths.Count(p => p.Outcome == PathOutcome.Rasterized));
        }

        /// <summary>差し替えの確かめの一覧の行の上限（それより多いセット・パスは「ほか n」）。</summary>
        const int ModelChangeListLimit = 12;

        /// <summary>
        /// モデルを替える（パネルへのドロップ・ピッカー、プロジェクト設定）。前のモデルが無ければ今までどおり読むだけ（差し替えではない）。
        /// あれば計画し、確かめてから 1 回で入れる。assignment はセットの ID → 新しいモデルのマテリアルの組（プロジェクト設定の対応。null なら鍵で照合）、
        /// reload は同じアセットを読み直す。替えたら true、断った・読めなかったら false（何も変えない）。
        /// </summary>
        internal bool ChangeModel(GameObject next, IReadOnlyDictionary<Guid, int> assignment = null, bool reload = false)
        {
            if (stroke != null) { message = L.Tr("A stroke is in progress."); return false; }
            if (next == model && !reload) return false;
            if (next == null || preview == null || !preview.HasModel)
            {
                // 前のモデルが無い（モデル無しのプロジェクト）・モデルを外す: 差し替えではないので読むだけ（大きなモデルは別スレッドで準備する）
                FinishStroke(false); CancelToolDrag(); CancelShapeDrag(); EndLightingDrag(true);
                previewDisplayCanceled = false; model = next;
                preview.BeginLoad(model); ResetVisibility(false); var notes = new List<string>(); ResolveSetMaterials(notes);
                message = string.Join("; ", preview.Diagnostics.Concat(notes)); repaintPixels = true;
                return true;
            }
            using (var plan = PlanModelChange(next, assignment, reload))
            {
                if (plan == null) return false;
                if (!ConfirmModelChange(plan))
                { message = L.Tr("The model was not changed; nothing changed."); return false; }
                bool add = plan.Unused.Count > 0 && textureSets.Count < YlpFormat.MaxTextureSets
                    && Dialogs.Confirm(L.Tr("Add texture sets?"), AddSetsText(plan), L.Tr("Add"), L.Tr("Don't Add"));
                ApplyModelChange(plan, add);
                return true;
            }
        }

        /// <summary>差し替えを計画する（何も変えない）。新しいモデルが読めなければ null と知らせ。assignment があれば、そこに無いセット
        /// （プロジェクト設定で消すセット）は計画に入れず、alsoTaken の組（プロジェクト設定で足すセット）はセットのあるマテリアルとみなす。</summary>
        internal ModelChangePlan PlanModelChange(GameObject next, IReadOnlyDictionary<Guid, int> assignment = null, bool reload = false, IEnumerable<int> alsoTaken = null)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            SyncCurrentSet();
            var plan = new ModelChangePlan { Model = next, Reload = reload, SameAsset = next == model, Candidate = new IsolatedModelPreview(), WasPosed = preview.Posed };
            try
            {
                var report = plan.Candidate.Load(next);
                if (!plan.Candidate.HasModel)
                {
                    message = L.Tr("The model {0} could not be read; nothing changed.", next.name) + " " + string.Join(" ", report.Diagnostics.Take(2));
                    plan.Dispose(); return null;
                }
                var oldGeometry = preview.Geometry; var newGeometry = plan.Candidate.Geometry;
                string oldPrint = SurfacePathRenderer.Fingerprint(oldGeometry); plan.Fingerprint = SurfacePathRenderer.Fingerprint(newGeometry);
                var groups = plan.Candidate.MaterialGroups;
                var match = assignment == null ? MatchMaterials(textureSets.Select(t => t.Material).ToList(), groups) : null;
                var newInput = BuildMeshBakeInput(newGeometry, plan.Candidate.Attributes);
                for (int i = 0; i < textureSets.Count; i++)
                {
                    var set = textureSets[i];
                    if (assignment != null && !assignment.ContainsKey(set.Id)) continue; // 消すセット
                    int group = assignment != null ? (assignment.TryGetValue(set.Id, out int g) && g >= 0 && g < groups.Count ? g : -1) : match[i];
                    var sp = new SetPlan { Set = set, Group = group, Before = SetMaterialText(set), After = group >= 0 ? BaseSetName(groups[group]) : null };
                    // UV: 前のスロットと新しいスロット
                    if (set.InModel && group >= 0)
                    {
                        var before = oldGeometry.Triangles.Where(t => set.PaintsSlot(t.MaterialSlot)).Select(Uv).ToList();
                        var after = newGeometry.Triangles.Where(t => t.Material == group).Select(Uv).ToList();
                        sp.Uv = UvLayout.Compare(before, after);
                    }
                    // 焼いたマップ: 今使えて、差し替えで古くなるもの
                    if (set.MeshMaps.Count > 0)
                    {
                        var now = MeshMapExpectationFor(set);
                        var slots = group >= 0 ? groups[group].Slots.ToArray() : null;
                        var then = new MeshMapExpectation { MeshHash = newInput.Hash, TopologyHash = newInput.TopologyHash, ReferenceHash = now.ReferenceHash, Width = set.Document.Width, Height = set.Document.Height,
                            TargetSlot = slots != null ? slots[0] : -2, TargetSlots = slots, UvChannel = 0, Settings = meshBakeSettings };
                        sp.MapsTurningStale = set.MeshMaps.Maps.Count(m => m.Provenance.Check(now).State == MeshMapState.Current && m.Provenance.Check(then).State != MeshMapState.Current);
                    }
                    // 3D のパス: 前のモデルに結び付いたものだけ
                    var d = set == currentSet ? document : set.Document;
                    foreach (var layer in d.Layers)
                    {
                        if (!(layer.Path is SurfacePath path) || path.ModelFingerprint != oldPrint) continue;
                        sp.Paths.Add(PlanPath(d, layer, path, oldGeometry, plan, group));
                    }
                    plan.Sets.Add(sp);
                }
                var taken = new HashSet<int>(plan.Sets.Where(s => s.Group >= 0).Select(s => s.Group));
                if (alsoTaken != null) foreach (var g in alsoTaken) if (g >= 0) taken.Add(g); // プロジェクト設定で足すセット
                plan.Unused.AddRange(groups.Where(g => !taken.Contains(g.Index)));
                return plan;
            }
            catch { plan.Dispose(); throw; }
        }

        static UvTriangle Uv(SurfaceTriangle t) => new UvTriangle(t.UvA.x, t.UvA.y, t.UvB.x, t.UvB.y, t.UvC.x, t.UvC.y);

        /// <summary>1 つのパスの扱いを決める（描き直すなら、ここで新しいメッシュに描いておく。描けなければ画素にする）。</summary>
        PathPlan PlanPath(PaintDocument d, PaintLayer layer, SurfacePath path, SurfaceGeometry oldGeometry, ModelChangePlan plan, int group)
        {
            var p = new PathPlan { Layer = layer.Id, LayerName = layer.Name };
            var locks = d.EffectiveLocks(layer.Id);
            bool canRedraw = (locks & (LayerLocks.All | LayerLocks.Pixels | LayerLocks.Transparency)) == 0, canRasterize = (locks & LayerLocks.All) == 0;
            if (plan.Fingerprint == path.ModelFingerprint) { p.Outcome = PathOutcome.Unchanged; return p; }
            string why = null;
            if (!canRedraw) why = L.Tr("the layer is locked");
            else if (SurfacePathRebind.TryRebind(path, oldGeometry, plan.Candidate.Geometry, group, out var rebound, out string reason))
            {
                try
                {
                    var render = SurfacePathRenderer.Render(d, plan.Candidate.Geometry, rebound, plan.Candidate.BrushBudget);
                    p.Outcome = PathOutcome.Redrawn; p.Path = rebound; p.Rendered = render.Surface;
                    if (render.Gaps > 0) p.Reason = L.Tr("{0} sample(s) could not be projected onto the new surface", render.Gaps);
                    return p;
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException) { why = ex.Message; }
            }
            else why = reason;
            p.Outcome = canRasterize ? PathOutcome.Rasterized : PathOutcome.KeptLocked; p.Reason = why;
            return p;
        }

        /// <summary>差し替えの確かめのダイアログ（同じモデルの読み直しなら題とボタンが「読み直す」）。</summary>
        bool ConfirmModelChange(ModelChangePlan plan)
        {
            bool reload = plan.SameAsset;
            return Dialogs.Confirm(reload ? L.Tr("Reload the model?") : L.Tr("Change the model?"), ModelChangeText(plan), reload ? L.Tr("Reload") : L.Tr("Change Model"), L.Tr("Cancel"));
        }

        /// <summary>確かめの文: 何がどう変わるかの一覧と、Undo できないこと。</summary>
        internal string ModelChangeText(ModelChangePlan plan)
        {
            var lines = new List<string>();
            string Percent(double v) => Math.Round(v * 100).ToString(CultureInfo.InvariantCulture) + "%";
            lines.Add(plan.SameAsset ? L.Tr("Reload {0} from its asset.", plan.Model.name) : (model != null ? model.name : L.Tr("Demo cube")) + " → " + plan.Model.name);
            lines.Add("");
            lines.Add(L.Tr("Texture sets (matched by material asset, then by name):"));
            foreach (var sp in plan.Sets.Take(ModelChangeListLimit))
            {
                string head = "• " + sp.Set.Name + ": ";
                if (sp.Group < 0) { lines.Add(head + L.Tr("not in the new model. It is kept with its pixels and not shown on the model; choose its material in File ▸ Project Configuration.")); continue; }
                string uv = sp.Uv == null ? L.Tr("its UVs could not be compared")
                    : sp.Uv.Same ? L.Tr("same UVs, so it looks the same")
                    : L.Tr("the UVs differ ({0} of the old UV area is still covered): the pixels stay as they are, but they will look different on the model", Percent(sp.Uv.Kept));
                lines.Add(head + L.Tr("material {0}, {1}.", sp.After, uv));
            }
            if (plan.Sets.Count > ModelChangeListLimit) lines.Add(L.Tr("… and {0} more.", plan.Sets.Count - ModelChangeListLimit));
            if (plan.Unused.Count > 0) lines.Add(L.Tr("Materials of the new model without a texture set: {0}.", string.Join(", ", plan.Unused.Select(BaseSetName))));
            var paths = plan.Sets.SelectMany(s => s.Paths.Where(p => p.Outcome != PathOutcome.Unchanged).Select(p => (s.Set, p))).ToList();
            if (paths.Count > 0)
            {
                lines.Add("");
                lines.Add(L.Tr("Paths on the model:"));
                foreach (var (set, p) in paths.Take(ModelChangeListLimit))
                {
                    string what = p.Outcome == PathOutcome.Redrawn ? L.Tr("redrawn on the new mesh") + (p.Reason != null ? " (" + p.Reason + ")" : "")
                        : p.Outcome == PathOutcome.Rasterized ? L.Tr("kept as pixels (rasterized), because {0}", p.Reason)
                        : L.Tr("left as it is, bound to the old model, because {0}", p.Reason);
                    lines.Add("• " + string.Format(CultureInfo.InvariantCulture, L.TrIn("model change", "{0}: {1}."), (textureSets.Count > 1 ? set.Name + " / " : "") + p.LayerName, what));
                }
                if (paths.Count > ModelChangeListLimit) lines.Add(L.Tr("… and {0} more.", paths.Count - ModelChangeListLimit));
                if (plan.WasPosed) lines.Add(L.Tr("The model is posed: the paths were matched on its posed shape, not on the shape it was loaded with."));
            }
            int stale = plan.Sets.Sum(s => s.MapsTurningStale);
            lines.Add("");
            if (stale > 0) lines.Add(L.Tr("{0} baked mesh map(s) become stale for the new model. Generators and projections that read them pass their input through until they are baked again.", stale));
            lines.Add(L.Tr("The pose and BlendShapes start from the new model's. Shape gradients and projections keep their place relative to the model root."));
            lines.Add("");
            lines.Add(L.Tr("Changing the model cannot be undone (the saved file keeps the old model until you save). The painted pixels and each texture set's undo history are kept; redrawn and rasterized paths are one undo step in their texture set. The model's assets are not changed."));
            return string.Join("\n", lines);
        }

        /// <summary>セットを足すかの問いの文。</summary>
        string AddSetsText(ModelChangePlan plan)
        {
            int room = YlpFormat.MaxTextureSets - textureSets.Count;
            return L.Tr("The new model has {0} material(s) without a texture set: {1}. Add an empty texture set for each (the size and channels of the open one)?", plan.Unused.Count, string.Join(", ", plan.Unused.Select(BaseSetName)))
                + (plan.Unused.Count > room ? "\n" + L.Tr("A project has at most {0} texture sets; only the first {1} are added.", YlpFormat.MaxTextureSets, room) : "");
        }

        /// <summary>計画を入れる（確かめは済み）。</summary>
        void ApplyModelChange(ModelChangePlan plan, bool addSets)
        {
            FinishStroke(false); CancelToolDrag(); CancelShapeDrag(); EndLightingDrag(true); pathDrag = -1; document?.EndCoalescing();
            previewDisplayCanceled = false;
            var notes = new List<string>();
            if (AbandonMeshBake()) notes.Add(L.Tr("The mesh-map bake was stopped because the model changed."));
            SyncCurrentSet();
            var groups = plan.Candidate.MaterialGroups;
            // セットの鍵を新しいマテリアルに（モデルに無いセットは鍵のまま）
            foreach (var sp in plan.Sets) if (sp.Group >= 0) sp.Set.Material = MaterialKeyOf(groups[sp.Group]);
            var bound = new HashSet<TextureSet>(plan.Sets.Where(s => s.Group >= 0).Select(s => s.Set));
            DeduplicateKeys(bound.Contains);
            model = plan.Model;
            // 計画で読んだプレビューをそのまま窓のプレビューにする（大きなモデルを 2 回読まない。形は計画のときのもの）
            var adopted = plan.Candidate; plan.Candidate = null;
            AdoptPreview(adopted);
            bool same = preview.Geometry != null && SurfacePathRenderer.Fingerprint(preview.Geometry) == plan.Fingerprint && preview.MaterialGroups.Count == groups.Count;
            if (same) foreach (var sp in plan.Sets) sp.Set.Bind(sp.Group, sp.Group >= 0 ? preview.MaterialGroups[sp.Group].Slots : null);
            else notes.Add(L.Tr("The model changed while it was being swapped; the texture sets were matched again by their materials."));
            // パス: セットごとに 1 回の Undo
            foreach (var sp in plan.Sets.Where(s => s.Paths.Any(p => p.Outcome == PathOutcome.Redrawn || p.Outcome == PathOutcome.Rasterized)))
            {
                var d = sp.Set == currentSet ? document : sp.Set.Document;
                try
                {
                    d.Batch(() =>
                    {
                        foreach (var p in sp.Paths)
                        {
                            if (p.Outcome == PathOutcome.Redrawn && same) d.SetPath(p.Layer, p.Path, p.Rendered);
                            else if (p.Outcome == PathOutcome.Redrawn || p.Outcome == PathOutcome.Rasterized) d.Rasterize(p.Layer);
                        }
                    });
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
                {
                    // 描き直しが予算などで断られたら、そのセットのパスは画素にする（パスは外すだけで画素は変えない）
                    notes.Add(SetNotePrefix(sp.Set) + L.Tr("The paths could not be redrawn ({0}); they are kept as pixels.", ex.Message));
                    try { d.Batch(() => { foreach (var p in sp.Paths) if (p.Outcome == PathOutcome.Redrawn || p.Outcome == PathOutcome.Rasterized) d.Rasterize(p.Layer); }); }
                    catch (InvalidOperationException again) { notes.Add(SetNotePrefix(sp.Set) + again.Message); }
                }
            }
            // セットの無いマテリアルに空のセット
            int added = 0;
            if (addSets)
                foreach (var g in plan.Unused)
                {
                    if (textureSets.Count >= YlpFormat.MaxTextureSets) break;
                    if (g.Index >= preview.MaterialGroups.Count) break;
                    var group = preview.MaterialGroups[g.Index];
                    if (textureSets.Any(t => t.MaterialGroup == group.Index)) continue;
                    textureSets.Add(NewEmptySet(MaterialKeyOf(group), DefaultSetName(group, null))); added++;
                }
            ResolveSetMaterials(notes);
            if (same) foreach (var sp in plan.Sets) if (sp.Group >= 0 && sp.Set.MaterialGroup != sp.Group) sp.Set.Bind(sp.Group, preview.MaterialGroups[sp.Group].Slots); // 計画した対応のまま
            setsRevision++;
            var budget = ApplyBudgets(); if (budget != null) notes.Add(budget);
            repaintPixels = true; renderedRevision = -1; lightingRevision = -1; RepaintPanelWindowsSoon();
            int missing = plan.Sets.Count(s => s.Group < 0), differ = plan.Sets.Count(s => s.Uv != null && !s.Uv.Same);
            message = (plan.SameAsset ? L.Tr("Reloaded the model {0}.", plan.Model.name) : L.Tr("Changed the model to {0}.", plan.Model.name))
                + (differ > 0 ? " " + L.Tr("{0} texture set(s) have other UVs; their pixels are unchanged.", differ) : "")
                + (missing > 0 ? " " + L.Tr("{0} texture set(s) are not in the new model.", missing) : "")
                + (plan.Redrawn > 0 ? " " + L.Tr("{0} path(s) redrawn.", plan.Redrawn) : "")
                + (plan.Rasterized > 0 ? " " + L.Tr("{0} path(s) kept as pixels.", plan.Rasterized) : "")
                + (added > 0 ? " " + L.Tr("Added {0} texture set(s).", added) : "")
                + (notes.Count > 0 ? " " + string.Join(" ", notes) : "");
        }

        /// <summary>2 つのセットが同じ識別子の鍵を持たないようにする（保存は重なりを断るので）。preferred のセット（新しいマテリアルに付いたもの）を
        /// 残し、ほかのセットの鍵からは識別子を外して名前だけにする（Unassigned は名前に、スロットの番号はモデルの外の番号に）。</summary>
        void DeduplicateKeys(Func<TextureSet, bool> preferred)
        {
            foreach (var dup in textureSets.Where(t => t.Material.ExclusiveKey != null).GroupBy(t => t.Material.ExclusiveKey).Where(g => g.Count() > 1).ToList())
                foreach (var loser in dup.OrderByDescending(t => preferred(t)).Skip(1))
                    loser.Material = loser.Material.IsMaterial ? YlpMaterialRef.Material(loser.Material.Name)
                        : loser.Material.IsPendingSlot ? YlpMaterialRef.PendingSlot(UnboundPendingSlot()) : YlpMaterialRef.Material(loser.Name);
        }

        /// <summary>読み込んだモデルのどのスロットでもなく、どのセットも使っていないスロットの番号（マテリアルに結び付けないセットの仮の鍵）。</summary>
        int UnboundPendingSlot()
        {
            int slot = Math.Max(FreePendingSlot(), preview != null && preview.HasModel ? preview.MaterialSlotCount : 0);
            while (textureSets.Any(t => t.Material.IsPendingSlot && t.Material.Slot == slot)) slot++;
            return slot;
        }
    }
}
