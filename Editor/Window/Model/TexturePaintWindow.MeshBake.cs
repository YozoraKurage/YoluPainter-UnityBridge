using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// メッシュマップのベイクの仕事と、ベイクの窓（<see cref="MeshBakeWindow"/>）の中身。
    /// <list type="bullet">
    /// <item>ベイクは別のスレッドで走る。始めるときに設定（の写し）・入力・ドキュメント・スロット・モデルのスナップショット・高ポリを固定し、
    /// 終わったときにそれらが今と違えば結果を捨てる（前のマップはそのまま）。取消・時間切れ・予算の拒否でも今のマップは変えない。</item>
    /// <item>断る条件（<see cref="MeshBakeRefusal"/>）は窓・プロパティの欄・<see cref="BakeMeshMaps"/> で同じ。</item>
    /// <item>窓から焼くと、EditorApplication.update で進め（GPU の仕事は主スレッドでしかできないので、そこで回す）、窓の中に進み具合と取消を
    /// 出す。窓を閉じてもベイクは続き、プロパティの欄に進み具合と取消が出る。持ち主を閉じる・リロード（OnDisable）・ドキュメントを替えると、
    /// 取り消して別のスレッドが終わるまで待ち、結果は捨てる。</item>
    /// <item><see cref="BakeMeshMaps"/>（新規プロジェクトの「作った後にベイク」など）は呼んだ所で待つ（取消できる Unity の進捗バー）。</item>
    /// <item>テクスチャセットごとに焼く: 窓の上の一覧でチェックしたセットを並びの順に 1 つずつ焼く。仕事（<see cref="MeshBakeJob"/>）は 1 つの
    /// セットの文書とスロットに結び付き、終わるたびにそのセットについて変わっていないかを確かめてから、そのセットのマップに入れる。取り消すと
    /// 残りのセットも焼かない。</item>
    /// </list>
    /// 窓の配置は Substance Painter の Baker と同じ: 上に焼くテクスチャセット、左に共通の設定と焼くマップの一覧（チェックで焼くものを選び、
    /// 押すとその設定を右に出す。焼いた・古い・未のしるし）、右に選んだ項目の設定、下に進み具合・取消・「チェックしたマップをベイク」・閉じる。
    /// 値は持ち主の <see cref="MeshBakeSettings"/> そのもの（保存したマップの由来・古さの判定と同じ値）。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>1 回のベイク。始めたときの条件を持つ。</summary>
        internal sealed class MeshBakeJob
        {
            public readonly MeshBakeSettings Settings;
            public readonly MeshBakeInput Input, Reference;
            public readonly MeshBakeBudget Budget;
            /// <summary>焼くテクスチャセット（結果を入れる先）と、始めたときのその文書。</summary>
            public readonly TextureSet Set;
            public readonly PaintDocument Document;
            public readonly SurfaceGeometry Geometry;
            /// <summary>AO・ベントノーマル・厚みのレイの GPU。使わなければ null（使えない GPU でも作り、ベイクが CPU に戻す）。</summary>
            public readonly GpuMeshBakeRayTracer Tracer;
            public readonly CancellationTokenSource Cancel = new CancellationTokenSource();
            public readonly System.Diagnostics.Stopwatch Clock = new System.Diagnostics.Stopwatch();
            public Task<MeshBakeResult> Work { get; private set; }
            readonly object gate = new object(); double fraction; string phase = "Preparing";

            public MeshBakeJob(MeshBakeSettings settings, MeshBakeInput input, MeshBakeInput reference, MeshBakeBudget budget, TextureSet set, PaintDocument document, SurfaceGeometry geometry, GpuMeshBakeRayTracer tracer)
            { Settings = settings; Input = input; Reference = reference; Budget = budget; Set = set; Document = document; Geometry = geometry; Tracer = tracer; }

            /// <summary>別のスレッドで焼き始める。拒否などの例外は <see cref="Work"/> に入る。</summary>
            public void Start()
            {
                Clock.Start();
                Work = Task.Run(() => MeshBaker.Bake(Input, Settings, Budget, Report, Cancel.Token, Reference, Tracer));
            }
            bool Report(double f, string p) { lock (gate) { fraction = f; phase = p; } return true; }
            public double Fraction { get { lock (gate) return fraction; } }
            public string Phase { get { lock (gate) return phase; } }
            public bool Canceling => Cancel.IsCancellationRequested;
        }

        /// <summary>窓から始めて走っているベイク（無ければ null）。</summary>
        MeshBakeJob meshBakeJob;
        /// <summary>窓から始めたベイクで、今の仕事の後に焼くテクスチャセット（並びの順）と、全体の数・済んだ数。</summary>
        readonly List<TextureSet> meshBakeQueue = new List<TextureSet>();
        int meshBakeTotal, meshBakeFinished;
        /// <summary>ベイクの窓の一覧でチェックを外したテクスチャセット（既定は全部焼く）。</summary>
        readonly HashSet<Guid> meshBakeSkipped = new HashSet<Guid>();
        /// <summary>窓の下に出す、最後のベイクの結果の一文と、それが成功か。</summary>
        string meshBakeOutcome; bool meshBakeOutcomeOk;
        double meshBakeRepainted;
        /// <summary>1 回の更新で GPU の仕事を回す時間の上限（ミリ秒）。長いほど GPU のベイクは速く、エディタの反応は鈍い。</summary>
        internal const int MeshBakePumpMilliseconds = 30;

        internal bool IsBakingMeshMaps => meshBakeJob != null;
        internal MeshBakeJob RunningMeshBake => meshBakeJob;
        internal string MeshBakeOutcome => meshBakeOutcome;

        /// <summary>今ベイクを始められない理由（始められれば null）。窓のボタン・<see cref="StartMeshBake"/>・<see cref="BakeMeshMaps"/> が同じ理由で断る。</summary>
        internal string MeshBakeRefusal()
        {
            if (document == null) return L.Tr("There is no document to bake mesh maps for.");
            if (meshBakeJob != null) return L.Tr("Mesh maps are already being baked.");
            if (stroke != null) return L.Tr("Finish the stroke before baking mesh maps.");
            if (posePending) return L.Tr("The pose is being changed; bake after it is applied (when the slider is released).");
            if (preview == null || !preview.HasModel) return L.Tr("Load a model (or the demo cube) before baking mesh maps.");
            if (!preview.CanPaint) return L.Tr("Mesh maps are baked only from a complete static snapshot that can be painted; this one is incomplete (see the load diagnostics).");
            if (highPolyModel != null && CurrentHighPolyInput() == null) return L.Tr("Mesh maps cannot be baked: {0}", highPolyNote ?? L.Tr("the high poly has no readable mesh."));
            if (meshBakeSettings.Maps == null || meshBakeSettings.Maps.Length == 0) return L.Tr("Check at least one map to bake.");
            if (CheckedBakeSets().Count == 0) return L.Tr("Check at least one texture set to bake.");
            return null;
        }

        /// <summary>焼くテクスチャセット（一覧でチェックしたもの、並びの順）。</summary>
        List<TextureSet> CheckedBakeSets() { SyncCurrentSet(); return textureSets.Where(s => !meshBakeSkipped.Contains(s.Id)).ToList(); }

        /// <summary>テクスチャセットのベイクの仕事を用意する（まだ走らせない）。断るときは null と理由。</summary>
        MeshBakeJob PrepareMeshBake(TextureSet set, out string refusal)
        {
            refusal = MeshBakeRefusal();
            if (refusal != null) return null;
            SyncCurrentSet();
            if (!textureSets.Contains(set)) { refusal = L.Tr("The texture set {0} is no longer in the project.", set.Name); return null; }
            if (!set.InModel) { refusal = L.Tr("The texture set {0} paints a material the loaded model does not have.", set.Name); return null; }
            var d = set.Document;
            var settings = meshBakeSettings.WithIdContext(CurrentMeshBakeInput(), CurrentHighPolyInput(), d.IdColors);
            // マテリアルを使う全部のスロットを焼く（1 つなら前と同じ条件の鍵）
            settings.Width = d.Width; settings.Height = d.Height; settings.TargetSlot = set.FirstSlot; settings.TargetSlots = set.Slots.Count > 1 ? set.Slots.ToArray() : null;
            try { settings.Validate(); }
            catch (ArgumentException ex) { refusal = L.Tr("Mesh maps were not baked: {0}", ex.Message); return null; }
            return new MeshBakeJob(settings, CurrentMeshBakeInput(), CurrentHighPolyInput(), new MeshBakeBudget { MaxBytes = PainterSettings.StrokeBudgetBytes },
                set, d, preview.Geometry, meshBakeUseGpu ? new GpuMeshBakeRayTracer(PainterSettings.GpuCacheBytes) : null);
        }

        /// <summary>選んだ mesh map を、チェックしたテクスチャセットごとに今のモデル・スロット・ドキュメントの大きさで焼き、同じ種類のマップを置き換える。
        /// 呼んだ所で待つ（取消できる進捗バー）。取消・時間切れ・予算の拒否ではそのセットのマップを変えず、残りのセットも焼かない。断る条件は
        /// <see cref="MeshBakeRefusal"/>（理由はステータスに出す）。</summary>
        /// <returns>全部のセットを焼けたら Completed、途中で止まったらそのセットの状態。始めなかった・使わなかったら null。</returns>
        internal MeshBakeStatus? BakeMeshMaps()
        {
            var sets = CheckedBakeSets();
            MeshBakeStatus? status = null;
            for (int i = 0; i < sets.Count; i++)
            {
                var job = PrepareMeshBake(sets[i], out string refusal);
                if (job == null) { message = refusal; return i == 0 ? null : status; }
                try { RunMeshBakeModal(job, i, sets.Count); }
                finally { Dialogs.ClearProgress(); }
                status = FinishMeshBake(job);
                if (status != MeshBakeStatus.Completed) return status;
            }
            if (sets.Count > 1) MeshBakeAllDone(sets.Count);
            return status;
        }

        /// <summary>何枚目のセットか（「セット 2/3: 名前 · 」。1 つなら空）。</summary>
        static string MeshBakeSetText(TextureSet set, int index, int count) => count > 1 ? L.Tr("Texture set {0}/{1}: {2}", index + 1, count, set.Name) + " · " : "";
        /// <summary>複数のセットを焼き終えたときの知らせ。</summary>
        void MeshBakeAllDone(int count)
        {
            string text = L.Tr("Baked the mesh maps of {0} texture sets.", count);
            message = text + " " + message; meshBakeOutcome = text; meshBakeOutcomeOk = true; RepaintMeshBakeWindow();
        }

        /// <summary>別のスレッドで焼き、ここ（主スレッド）で進捗バーを出して取消を受ける。始める前にも 1 回尋ねる。</summary>
        void RunMeshBakeModal(MeshBakeJob job, int index, int count)
        {
            string title = L.Tr("Baking mesh maps");
            if (MeshBakeProgress(title, L.Tr("Preparing…"), 0)) job.Cancel.Cancel();
            job.Start();
            var shown = System.Diagnostics.Stopwatch.StartNew();
            // GPU の呼び出しは主スレッドでしかできないので、別のスレッドのベイクから回ってきた仕事をこのループで行う
            while (!WaitQuietly(job.Work, job.Tracer != null ? 2 : 50))
            {
                job.Tracer?.Pump();
                if (job.Tracer != null && shown.ElapsedMilliseconds < 50) continue;
                shown.Restart();
                if (MeshBakeProgress(title, MeshBakeSetText(job.Set, index, count) + MeshBakeProgressText(job) + " (" + job.Settings.Width + "×" + job.Settings.Height + ", " + string.Join(", ", job.Settings.Maps.Select(MeshMapLabel)) + ")", (float)job.Fraction))
                    job.Cancel.Cancel();
            }
            job.Tracer?.Pump();
        }
        static bool WaitQuietly(Task task, int milliseconds) { try { return task.Wait(milliseconds); } catch (AggregateException) { return true; } }

        /// <summary>ベイクの窓の「チェックしたマップをベイク」: 別のスレッドで焼き始め、EditorApplication.update で進める。断ったらその理由
        /// （ステータスにも出す）、始めたら null。</summary>
        internal string StartMeshBake()
        {
            string refusal = MeshBakeRefusal();
            var sets = refusal == null ? CheckedBakeSets() : null;
            var job = refusal == null ? PrepareMeshBake(sets[0], out refusal) : null;
            if (job == null) { message = refusal; RepaintMeshBakeWindow(); return refusal; }
            meshBakeQueue.Clear(); meshBakeQueue.AddRange(sets.Skip(1)); meshBakeTotal = sets.Count; meshBakeFinished = 0;
            meshBakeJob = job; meshBakeOutcome = null;
            job.Start();
            EditorApplication.update -= MeshBakeTick; EditorApplication.update += MeshBakeTick;
            message = L.Tr("Baking mesh maps") + "…";
            Repaint(); RepaintMeshBakeWindow();
            return null;
        }

        /// <summary>窓から始めたベイクの次のテクスチャセットを焼き始める（並びから消えたセットは飛ばす）。始めたら true。断ったら refused も true
        /// （理由は知らせてある）。</summary>
        bool StartNextMeshBake(out bool refused)
        {
            refused = false;
            while (meshBakeQueue.Count > 0)
            {
                var set = meshBakeQueue[0]; meshBakeQueue.RemoveAt(0);
                if (!textureSets.Contains(set)) { meshBakeFinished++; continue; }
                var job = PrepareMeshBake(set, out string refusal);
                if (job == null) { meshBakeQueue.Clear(); MeshBakeEnded(null, refusal, false); refused = true; return false; }
                meshBakeJob = job; job.Start();
                EditorApplication.update -= MeshBakeTick; EditorApplication.update += MeshBakeTick;
                Repaint(); RepaintMeshBakeWindow();
                return true;
            }
            return false;
        }

        void MeshBakeTick()
        {
            if (this == null) { EditorApplication.update -= MeshBakeTick; return; }
            PumpMeshBake(MeshBakePumpMilliseconds);
        }

        /// <summary>走っているベイクを進める: GPU の仕事を主スレッドで最大 milliseconds 回し、終わっていれば結果を入れる。まだ走っていれば true。</summary>
        internal bool PumpMeshBake(int milliseconds)
        {
            var job = meshBakeJob;
            if (job == null) return false;
            if (job.Tracer != null)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                do job.Tracer.Pump(); while (!WaitQuietly(job.Work, 1) && clock.ElapsedMilliseconds < milliseconds);
            }
            if (!job.Work.IsCompleted)
            {
                // プロパティの欄の進み具合（欄が開いているときだけ。持ち主の描き直しは 3D の表示も描くので間を空ける）
                if (ShowMeshMapPanel && EditorApplication.timeSinceStartup - meshBakeRepainted > .1) { meshBakeRepainted = EditorApplication.timeSinceStartup; Repaint(); }
                return true;
            }
            job.Tracer?.Pump();
            meshBakeJob = null; EditorApplication.update -= MeshBakeTick;
            MeshBakeStatus? status = null;
            try { status = FinishMeshBake(job); }
            catch (Exception ex) { MeshBakeEnded(null, ex.Message, false); Debug.LogWarning("Texture Painter: " + ex.Message); }
            meshBakeFinished++;
            // 次のテクスチャセット（取消・時間切れ・拒否・条件の変化で止まったら、残りは焼かない）
            bool refused = false;
            if (status == MeshBakeStatus.Completed && StartNextMeshBake(out refused)) return true;
            if (status == MeshBakeStatus.Completed && !refused && meshBakeTotal > 1) MeshBakeAllDone(meshBakeTotal);
            meshBakeQueue.Clear();
            Repaint();
            return false;
        }

        /// <summary>走っているベイクの取消を頼む（止まったら <see cref="PumpMeshBake"/> が「取り消した」と知らせる。前のマップはそのまま）。</summary>
        internal void CancelMeshBake()
        {
            if (meshBakeJob == null) return;
            meshBakeQueue.Clear();
            meshBakeJob.Cancel.Cancel();
            message = L.Tr("Canceling the mesh-map bake…");
            Repaint(); RepaintMeshBakeWindow();
        }

        /// <summary>結果を捨てて止める: 取り消し、別のスレッドが終わるまで（GPU の仕事を回しながら）待つ。持ち主を閉じる・リロード（OnDisable）・
        /// ドキュメントを替えるときに。走っていなければ何もしない。</summary>
        /// <returns>止めたベイクがあったか。</returns>
        bool AbandonMeshBake()
        {
            var job = meshBakeJob;
            meshBakeQueue.Clear();
            if (job == null) return false;
            meshBakeJob = null; EditorApplication.update -= MeshBakeTick;
            job.Cancel.Cancel();
            while (!WaitQuietly(job.Work, 2)) job.Tracer?.Pump();
            job.Tracer?.Pump();
            meshBakeOutcome = null;
            return true;
        }

        /// <summary>終わったベイクの結果を入れる（主スレッド）。完了して条件が変わっていなければ同じ種類のマップを置き換える。取消・時間切れ・拒否・
        /// 条件の変化では今のマップを変えない。</summary>
        MeshBakeStatus? FinishMeshBake(MeshBakeJob job)
        {
            job.Clock.Stop();
            MeshBakeResult result;
            try { result = job.Work.GetAwaiter().GetResult(); } // 拒否などの例外はそのまま（AggregateException に包まない）
            catch (Exception ex) when (ex is MeshBakeRefusedException || ex is ArgumentException)
            { return MeshBakeEnded(null, L.Tr("Mesh maps were not baked: {0}", ex.Message), false); }
            job.Set.LastMeshBakeReport = result.Report;
            if (result.Status != MeshBakeStatus.Completed)
                return MeshBakeEnded(result.Status, result.Status == MeshBakeStatus.TimedOut ? L.Tr("The mesh-map bake hit its time limit; the previous maps are unchanged.") : L.Tr("The mesh-map bake was canceled; the previous maps are unchanged."), false);
            string changed = MeshBakeChanged(job);
            if (changed != null) return MeshBakeEnded(null, L.Tr("The baked maps were not used because {0} changed during the bake; the previous maps are unchanged. Bake again.", changed), false);
            job.Set.MeshMaps.Put(result.Maps);
            if (job.Set == currentSet && meshMapView == MeshMapView.None) meshMapView = MeshMapView.Coverage;
            Repaint();
            var s = job.Settings; var report = result.Report;
            // ステータスには全部、窓の下の帯には短く（詳しい記録は窓の共通の設定の「最後のベイク」）
            string summary = L.Tr("Baked {0} map(s) in {1} s.", result.Maps.Count, report.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture))
                + (report.OverlapTexels > 0 ? " " + L.Tr("{0} texels have overlapping UVs (the lower triangle index wins).", report.OverlapTexels.ToString("N0", CultureInfo.InvariantCulture)) : "");
            return MeshBakeEnded(result.Status, (textureSets.Count > 1 ? job.Set.Name + ": " : "") + L.Tr("Baked {0} at {1}×{2} for slot {3} (rays: {4}): {5}. Mesh maps are derived data, not layers; Save keeps them in the .ylp.",
                string.Join(", ", result.Maps.Select(m => MeshMapLabel(m.Kind))), s.Width, s.Height, s.TargetSlot, report.RayBackend, report.Summary()), true, summary);
        }
        MeshBakeStatus? MeshBakeEnded(MeshBakeStatus? status, string text, bool ok, string summary = null)
        {
            message = text; meshBakeOutcome = summary ?? text; meshBakeOutcomeOk = ok;
            RepaintMeshBakeWindow();
            return status;
        }

        /// <summary>始めたときから、結果の意味を変えるもの（テクスチャセット・その文書と大きさ・スロット・モデルのスナップショット（ポーズを含む）・
        /// 高ポリ）が変わったか。変わったものの名前、同じなら null。焼いている間の描画・設定の変更・今のセットの切り替えは結果の意味を変えない
        /// （由来に始めたときの設定が残る）。</summary>
        string MeshBakeChanged(MeshBakeJob job)
        {
            SyncCurrentSet();
            if (!textureSets.Contains(job.Set)) return L.Tr("the texture set");
            var d = job.Set.Document;
            if (!ReferenceEquals(d, job.Document) || d.Width != job.Settings.Width || d.Height != job.Settings.Height) return L.Tr("the document");
            if (!job.Set.Slots.SequenceEqual(job.Settings.Targets() ?? new int[0])) return L.Tr("the material slot");
            if (preview == null || !ReferenceEquals(preview.Geometry, job.Geometry)) return L.Tr("the model or its pose");
            if (!ReferenceEquals(CurrentHighPolyInput(), job.Reference)) return L.Tr("the high poly");
            return null;
        }

        static string MeshBakePhaseName(string phase)
        {
            switch (phase)
            {
                case "Preparing": return L.TrIn("bake phase", "Preparing");
                case "Baking": return L.TrIn("bake phase", "Baking");
                case "Padding": return L.TrIn("bake phase", "Padding");
                case "Done": return L.TrIn("bake phase", "Done");
                default: return phase;
            }
        }
        /// <summary>「段階… nn%」（取消を頼んだ後は「取り消し中…」）。</summary>
        static string MeshBakeProgressText(MeshBakeJob job)
            => (job.Canceling ? L.Tr("Canceling") : MeshBakePhaseName(job.Phase)) + "… " + (int)(job.Fraction * 100) + "%";

        /// <summary>ベイクの窓を開く（開いていれば前に出す）。3D メニュー・プロパティの欄から。</summary>
        internal MeshBakeWindow OpenMeshBakeWindow() => MeshBakeWindow.Open(this);
        void RepaintMeshBakeWindow() { var w = MeshBakeWindow.For(this); if (w != null) w.Repaint(); }

        // ───────── テクスチャセット ─────────

        /// <summary>焼くテクスチャセット（窓の上の一覧の 1 行）。</summary>
        internal readonly struct MeshBakeTarget
        {
            public readonly Guid Id;
            public readonly int Width, Height;
            /// <summary>セットが受け持つスロット（モデルに無ければ空）。</summary>
            public readonly IReadOnlyList<int> Slots;
            public readonly string Name;
            /// <summary>焼くか（一覧のチェック）。</summary>
            public readonly bool Bake;
            /// <summary>チェックを外せないか（プロジェクトにテクスチャセットが 1 つしかない・チェックしたものが最後の 1 つ）。</summary>
            public readonly bool Fixed;
            public MeshBakeTarget(Guid id, IReadOnlyList<int> slots, string name, int width, int height, bool bake, bool @fixed) { Id = id; Slots = slots; Name = name; Width = width; Height = height; Bake = bake; Fixed = @fixed; }
        }

        /// <summary>焼くテクスチャセットの一覧（プロジェクトのセットの並び。チェックは <see cref="SetMeshBakeTarget"/>、既定は全部）。</summary>
        internal IReadOnlyList<MeshBakeTarget> MeshBakeTargets()
        {
            SyncCurrentSet();
            int checkedCount = textureSets.Count(s => !meshBakeSkipped.Contains(s.Id));
            return textureSets.Select(s =>
            {
                bool bake = !meshBakeSkipped.Contains(s.Id);
                return new MeshBakeTarget(s.Id, s.Slots, s.Name, s.Document.Width, s.Document.Height, bake, textureSets.Count == 1 || bake && checkedCount == 1);
            }).ToList();
        }

        /// <summary>テクスチャセットを焼くかどうかを変える。最後にチェックしたセットは外さない（焼くものが無い状態を作らない）。</summary>
        internal void SetMeshBakeTarget(Guid id, bool bake)
        {
            SyncCurrentSet();
            var set = textureSets.FirstOrDefault(s => s.Id == id); if (set == null) return;
            if (bake) meshBakeSkipped.Remove(set.Id);
            else if (textureSets.Count(s => s != set && !meshBakeSkipped.Contains(s.Id)) > 0) meshBakeSkipped.Add(set.Id);
            Repaint(); RepaintMeshBakeWindow();
        }

        /// <summary>焼くマップに kind を入れる・外す。最後の 1 つは外さない（焼くものが無い状態を作らない）。</summary>
        internal void SetMeshBakeKind(MeshMapKind kind, bool bake)
        {
            var s = meshBakeSettings; var maps = (s.Maps ?? Array.Empty<MeshMapKind>()).ToList();
            if (bake == maps.Contains(kind)) return;
            if (bake) maps.Add(kind); else if (maps.Count > 1) maps.Remove(kind); else return;
            // 一覧の順（MeshBakeSettings.AllKinds）にそろえる
            s.Maps = MeshBakeSettings.AllKinds.Where(maps.Contains).ToArray();
            Repaint(); RepaintMeshBakeWindow();
        }

        // ───────── 窓の中身 ─────────

        const float BakeHeaderHeight = 40, BakeFooterHeight = 60, BakeListRow = 24;
        /// <summary>右の設定の、名前の列の幅（ドロップダウン・値の箱）。</summary>
        const float BakeLabelWidth = 150;
        static readonly Color CurrentColor = new Color(.35f, .78f, .42f);
        static GUIStyle s_bakeTitle;
        static GUIStyle BakeTitleStyle => s_bakeTitle ?? (s_bakeTitle = new GUIStyle(PaintTheme.LabelBold) { fontSize = 14 });

        /// <summary>窓の中身を r に描く（窓の OnGUI と、オフスクリーンの描画から）。</summary>
        internal void DrawMeshBakeWindow(Rect r, MeshBakeWindow host)
        {
            if (document == null) return;
            PaintGui.Fill(r, PaintTheme.PanelBg);
            bool baking = meshBakeJob != null;
            string refusal = baking ? null : MeshBakeRefusal();
            var expected = CurrentMeshMapExpectation();
            // 見出し
            var head = new Rect(r.x, r.y, r.width, BakeHeaderHeight);
            PaintGui.Fill(head, PaintTheme.PanelHeader); PaintGui.HLine(r.x, r.xMax, head.yMax - 1, PaintTheme.Border);
            PaintGui.Icon(new Rect(r.x + 12, head.y, 22, head.height), "local_fire_department", PaintTheme.Text, 19);
            PaintGui.Text(new Rect(r.x + 40, head.y, r.width - 52, head.height), L.Tr("Bake Mesh Maps"), BakeTitleStyle);
            // 焼くテクスチャセット
            var sets = new UiRows(new Rect(r.x, head.yMax, r.width, 1e6f), 8);
            PaintGui.GroupLabel(sets.Row(16), L.Tr("Texture Sets to Bake"));
            var targets = MeshBakeTargets();
            for (int i = 0; i < targets.Count; i++) DrawMeshBakeTarget(sets.Row(BakeListRow, 2), host, targets[i], i, baking);
            float top = Mathf.Round(head.yMax + sets.Used + 6);
            // 左の一覧・右の設定・下の帯
            var foot = new Rect(r.x, r.yMax - BakeFooterHeight, r.width, BakeFooterHeight);
            float listWidth = Mathf.Clamp(Mathf.Round(r.width * .32f), 220, 300);
            var list = new Rect(r.x, top, listWidth, foot.y - top);
            var page = new Rect(list.xMax + 1, top, r.xMax - list.xMax - 1, foot.y - top);
            PaintGui.HLine(r.x, r.xMax, top - 1, PaintTheme.Border);
            PaintGui.Fill(list, PaintTheme.MenuBg);
            PaintGui.VLine(list.xMax, top, foot.y, PaintTheme.Border);
            BakeScroll(list, host, ref host.ListScroll, ref host.ListHeight, rows => DrawMeshBakeList(rows, host, expected, baking));
            BakeScroll(page, host, ref host.PageScroll, ref host.PageHeight, rows =>
            {
                bool was = GUI.enabled; GUI.enabled = was && !baking;
                try
                {
                    if (host.Page != MeshBakeWindow.CommonPage && Enum.IsDefined(typeof(MeshMapKind), host.Page)) DrawMeshBakeKind(rows, host, (MeshMapKind)host.Page, expected);
                    else DrawMeshBakeCommon(rows, host);
                }
                finally { GUI.enabled = was; }
            });
            DrawMeshBakeFooter(foot, host, refusal);
        }

        /// <summary>はみ出したらスクロールする縦の区画（プロパティの欄と同じ作り）。中身の高さは Repaint で測る。</summary>
        static void BakeScroll(Rect viewport, MeshBakeWindow host, ref Vector2 scroll, ref float contentHeight, Action<UiRows> draw)
        {
            var e = Event.current;
            bool overflow = contentHeight > viewport.height + .5f;
            scroll.y = Mathf.Clamp(scroll.y, 0, Mathf.Max(0, contentHeight - viewport.height));
            PaintGui.BeginScroll(viewport, scroll);
            var rows = new UiRows(new Rect(0, 0, viewport.width - (overflow ? 8 : 0), 1e6f), 8);
            draw(rows);
            rows.Space(8);
            if (e.type == EventType.Repaint && Mathf.Abs(rows.Used - contentHeight) > .5f) { contentHeight = rows.Used; host.Repaint(); }
            PaintGui.EndScroll();
            if (e.type == EventType.ScrollWheel && viewport.Contains(e.mousePosition))
            { scroll.y = Mathf.Clamp(scroll.y + e.delta.y * 14, 0, Mathf.Max(0, contentHeight - viewport.height)); e.Use(); host.Repaint(); }
            if (overflow) PaintGui.Rounded(new Rect(viewport.xMax - 6, viewport.y + viewport.height * scroll.y / contentHeight, 4, viewport.height * viewport.height / contentHeight), PaintTheme.ControlActive, 2);
        }

        void DrawMeshBakeTarget(Rect row, MeshBakeWindow host, MeshBakeTarget target, int index, bool baking)
        {
            var check = new Rect(row.x + 4, row.y, 22, row.height);
            bool next = PaintGui.Toggle(host.Spot("bake.set." + index, check), "", target.Bake,
                textureSets.Count == 1 ? L.Tr("This project has one texture set: the material the document paints.") : target.Fixed ? L.Tr("At least one texture set stays checked.") : L.Tr("Bake this texture set"), !baking && !target.Fixed);
            if (next != target.Bake) SetMeshBakeTarget(target.Id, next);
            string size = target.Width + " × " + target.Height;
            float sizeWidth = PaintGui.TextWidth(size, PaintTheme.LabelDim) + 4;
            PaintGui.Icon(new Rect(check.xMax + 2, row.y, 18, row.height), "texture", PaintTheme.TextDim, 15);
            float nameX = check.xMax + 24;
            bool current = target.Id == currentSet.Id;
            PaintGui.Text(new Rect(nameX, row.y, row.xMax - sizeWidth - 8 - nameX, row.height), PaintGui.Fit(target.Name, row.xMax - sizeWidth - 8 - nameX, current ? PaintTheme.LabelBold : PaintTheme.Label, false), current ? PaintTheme.LabelBold : PaintTheme.Label);
            PaintGui.Text(new Rect(row.xMax - sizeWidth, row.y, sizeWidth, row.height), size, StateStyle);
            var set = textureSets.FirstOrDefault(s => s.Id == target.Id);
            PaintGui.Tooltip(new Rect(nameX, row.y, row.xMax - nameX, row.height), target.Name + " · " + (set != null ? SetMaterialText(set) : "") + "\n" + L.Tr("Mesh maps are baked at the document size, for every mesh that uses this texture set's material"));
        }

        // ───────── 左の一覧 ─────────

        void DrawMeshBakeList(UiRows rows, MeshBakeWindow host, MeshMapExpectation expected, bool baking)
        {
            var e = Event.current;
            // 共通の設定
            var row = rows.Row(BakeListRow, 6);
            bool selected = host.Page == MeshBakeWindow.CommonPage || !Enum.IsDefined(typeof(MeshMapKind), host.Page);
            ListRowBackground(row, selected);
            PaintGui.Icon(new Rect(row.x + 4, row.y, 22, row.height), "tune", selected ? Color.white : PaintTheme.TextDim, 16);
            PaintGui.Text(new Rect(row.x + 30, row.y, row.width - 34, row.height), L.Tr("Common Settings"), PaintTheme.Label, selected ? Color.white : PaintTheme.Text);
            host.Spot("bake.page.common", row);
            if (e.type == EventType.MouseDown && e.button == 0 && row.Contains(e.mousePosition)) { host.Page = MeshBakeWindow.CommonPage; e.Use(); }
            // 焼くマップ
            PaintGui.GroupLabel(rows.Row(16), L.Tr("Maps to Bake"));
            var s = meshBakeSettings;
            foreach (var kind in MeshBakeSettings.AllKinds)
            {
                row = rows.Row(BakeListRow, 2);
                selected = host.Page == (int)kind;
                ListRowBackground(row, selected);
                var check = new Rect(row.x + 4, row.y, 22, row.height);
                bool on = s.Includes(kind), last = on && s.Maps.Length == 1;
                bool next = PaintGui.Toggle(host.Spot("bake.kind." + kind, check), "", on, last ? L.Tr("At least one map stays checked.") : L.Tr("Bake this map"), !baking && !last);
                if (next != on) SetMeshBakeKind(kind, next);
                var (state, color, stateName) = MeshMapListState(kind, expected);
                // 状態のしるし（右端の点。焼いていなければ輪）と、入るなら状態の名前
                var dot = new Rect(row.xMax - 18, row.y, 14, row.height);
                if (state == MeshMapState.Missing) PaintGui.Outline(new Rect(dot.center.x - 4, dot.center.y - 4, 8, 8), PaintTheme.TextDisabled, 1, 4);
                else PaintGui.Dot(dot, color);
                float nameX = check.xMax + 6, space = dot.x - 4 - nameX;
                string full = MeshMapLabel(kind);
                string label = PaintGui.TextWidth(full, PaintTheme.Label) <= space ? full : MeshMapGridLabel(kind);
                float labelWidth = PaintGui.TextWidth(label, PaintTheme.Label);
                PaintGui.Text(new Rect(nameX, row.y, space, row.height), PaintGui.Fit(label, space, PaintTheme.Label), PaintTheme.Label, selected ? Color.white : PaintTheme.Text);
                float stateWidth = PaintGui.TextWidth(stateName, PaintTheme.LabelDim);
                if (state != MeshMapState.Missing && labelWidth + 10 + stateWidth <= space)
                    PaintGui.Text(new Rect(dot.x - 4 - stateWidth, row.y, stateWidth, row.height), stateName, PaintTheme.LabelDim, selected ? Color.white : color);
                PaintGui.Tooltip(new Rect(nameX, row.y, row.xMax - nameX, row.height), full + " · " + stateName);
                host.Spot("bake.page." + kind, row);
                if (e.type == EventType.MouseDown && e.button == 0 && row.Contains(e.mousePosition) && !check.Contains(e.mousePosition)) { host.Page = (int)kind; e.Use(); }
            }
        }

        static void ListRowBackground(Rect row, bool selected)
        {
            bool hover = GUI.enabled && row.Contains(Event.current.mousePosition);
            if (selected) PaintGui.Rounded(row, PaintTheme.AccentDim, 4);
            else if (hover) PaintGui.Rounded(row, PaintTheme.ControlHover, 4);
        }

        /// <summary>一覧のしるし: 焼いていない（Missing）、最新、古い、確かめられない（モデルが無い）。</summary>
        (MeshMapState state, Color color, string name) MeshMapListState(MeshMapKind kind, MeshMapExpectation expected)
        {
            if (!meshMaps.TryGet(kind, out var map)) return (MeshMapState.Missing, PaintTheme.TextDisabled, L.Tr("Not baked yet"));
            var state = map.Provenance.Check(expected).State;
            return (state, state == MeshMapState.Current ? CurrentColor : state == MeshMapState.Stale ? PaintTheme.Warning : PaintTheme.TextDim, MeshMapStateName(state));
        }

        // ───────── 右の設定 ─────────

        void DrawMeshBakeCommon(UiRows rows, MeshBakeWindow host)
        {
            var s = meshBakeSettings;
            PaintGui.Text(rows.Row(22), L.Tr("Common Settings"), PaintTheme.LabelBold);
            NoteRow(rows, L.Tr("These apply to every map. Distances are relative to the model's bounding-box diagonal, so they do not depend on its size or units."));
            // 出力
            PaintGui.GroupLabel(rows.Row(16), L.TrIn("mesh map", "Output"));
            var bakeSets = CheckedBakeSets();
            PaintGui.ValueBox(rows.Row(), L.TrIn("mesh bake", "Size"), string.Join(", ", bakeSets.Select(t => t.Document.Width + " × " + t.Document.Height).Distinct()), BakeLabelWidth, null, L.Tr("Mesh maps are baked at the document size, for every mesh that uses this texture set's material"));
            PaintGui.ValueBox(rows.Row(), L.Tr("Texture Set"), textureSets.Count == 1 ? SetMaterialText(currentSet) : string.Join(", ", bakeSets.Select(t => t.Name)), BakeLabelWidth, null, null, true);
            s.Padding = PaintGui.KeepIntSlider(host.Spot("bake.padding", rows.Row()), L.TrIn("mesh map", "Padding"), s.Padding, 0, MeshBakeSettings.MaxPadding, " px", L.Tr("Texels the islands are extended into the empty space around them (never over another island)"));
            BakeChoice(host, rows.Row(), L.TrIn("mesh map", "Antialiasing"), s.Antialiasing, new[] { 1, 2, 3, 4 }, n => n == 1 ? L.Tr("None") : n + " × " + n, n => meshBakeSettings.Antialiasing = n,
                L.Tr("Subsamples per texel side (the bake takes n² times as long)"));
            // レイ（AO・ベントノーマル・厚みが共有する）
            PaintGui.GroupLabel(rows.Row(16), L.Tr("Rays (ambient occlusion, bent normal, thickness)"));
            BakeChoice(host, rows.Row(), L.TrIn("mesh map", "Occluders"), s.Occluders, (MeshOccluders[])Enum.GetValues(typeof(MeshOccluders)), OccludersName, v => meshBakeSettings.Occluders = v,
                L.Tr("Which faces block AO and thickness rays"));
            meshBakeUseGpu = PaintGui.FitToggle(host.Spot("bake.gpu", rows.Row()), L.Tr("Use the GPU for rays"), meshBakeUseGpu,
                L.Tr("Ambient occlusion, bent normal and thickness rays run in a compute shader (same formulas, float precision). Falls back to the CPU when the GPU cannot."));
            // 高ポリ
            PaintGui.GroupLabel(rows.Row(16), L.Tr("High Poly (optional)"));
            var high = highPolyModel;
            if (PaintGui.ObjectBox(rows.Row(), ref high, HighPolyPickerId, true, L.Tr("None (drop a high poly here)"), "view_in_ar",
                    L.Tr("A second model whose detail is projected onto this one (meshes are read only; nothing is instantiated or changed)"), L.Tr("Stop using the high poly")))
            { highPolyModel = high; CurrentHighPolyInput(); Repaint(); host.Repaint(); }
            bool usesHigh = highPolyModel != null;
            if (usesHigh) CurrentHighPolyInput(); // 選んであれば読む（同じものなら読み直さない）。数と知らせを出すため
            s.ReferenceFrontal = PaintGui.KeepSlider(host.Spot("bake.frontal", rows.Row()), L.TrIn("mesh map", "Frontal"), s.ReferenceFrontal, .0001, .2, "0.####", "",
                L.Tr("Max frontal distance: how far outside the low poly the high poly is searched (relative to the bounding-box diagonal)"), usesHigh);
            s.ReferenceRear = PaintGui.KeepSlider(rows.Row(), L.TrIn("mesh map", "Rear"), s.ReferenceRear, .0001, .2, "0.####", "",
                L.Tr("Max rear distance: how far inside the low poly the high poly is searched"), usesHigh);
            s.ReferenceAverageNormals = PaintGui.FitToggle(rows.Row(), L.Tr("Average normals"), s.ReferenceAverageNormals, L.Tr("Project along normals averaged over hard edges (a cage without gaps)"), usesHigh);
            s.ReferenceMatchByName = PaintGui.FitToggle(rows.Row(), L.Tr("Match by name"), s.ReferenceMatchByName, L.Tr("Project 'part_low' only onto 'part_high' (or 'part')"), usesHigh);
            if (usesHigh)
            {
                var reference = highPolyPreview != null ? highPolyPreview.Geometry : null;
                var row = rows.Row();
                PaintGui.ValueBox(new Rect(row.x, row.y, row.width - 28, row.height), L.Tr("Triangles"), reference != null ? reference.TriangleCount.ToString("N0", CultureInfo.InvariantCulture) : L.Tr("Not readable"), BakeLabelWidth,
                    reference != null ? (Color?)null : PaintTheme.Warning, L.Tr("Triangles read from the high poly"));
                if (PaintGui.IconButton(new Rect(row.xMax - 24, row.y, 24, row.height), "sync", L.Tr("Read the high poly's meshes again (after editing them)"), false, GUI.enabled, 16)) { DisposeHighPoly(); CurrentHighPolyInput(); }
                if (highPolyNote != null) NoteRow(rows, highPolyNote, NoteKind.Warning);
            }
            else NoteRow(rows, L.Tr("Without a high poly, the maps are baked from this model alone (tangent normal, height and opacity are then uniform)."), NoteKind.Info);
            // 最後のベイク
            if (lastMeshBakeReport != null) DrawMeshBakeReport(rows, lastMeshBakeReport);
        }

        /// <summary>最後に焼いた結果の記録（時間・レイの処理・テクセル・UV の重なり・高ポリに当たらなかった数・三角形と辺の診断）。</summary>
        void DrawMeshBakeReport(UiRows rows, MeshBakeReport report)
        {
            var c = CultureInfo.InvariantCulture;
            PaintGui.GroupLabel(rows.Row(16), L.Tr("Last Bake"));
            NoteRow(rows, L.Tr("Time: {0} s (prepare {1} · raster {2} · padding {3})", report.TotalSeconds.ToString("F2", c), report.PrepareSeconds.ToString("F2", c), report.RasterSeconds.ToString("F2", c), report.PaddingSeconds.ToString("F2", c)));
            NoteRow(rows, L.Tr("Rays: {0} on {1}", report.Rays.ToString("N0", c), report.RayBackend));
            NoteRow(rows, L.Tr("Texels: {0} baked · {1} padding · {2} empty", (report.CoveredTexels + report.OverlapTexels).ToString("N0", c), report.PaddedTexels.ToString("N0", c), report.EmptyTexels.ToString("N0", c)));
            if (report.OverlapTexels > 0) NoteRow(rows, L.Tr("{0} texels have overlapping UVs (the lower triangle index wins).", report.OverlapTexels.ToString("N0", c)), NoteKind.Warning);
            if (report.ReferenceTriangles > 0)
                NoteRow(rows, L.Tr("High poly: {0} triangles; {1} of {2} samples missed it and were baked from this model.", report.ReferenceTriangles.ToString("N0", c), report.MissedSamples.ToString("N0", c), report.ProjectedSamples.ToString("N0", c)),
                    report.MissedSamples > 0 ? NoteKind.Warning : NoteKind.Plain);
            NoteRow(rows, L.Tr("Triangles: {0} baked · {1} without UV area · {2} degenerate", report.ReceivingTriangles.ToString("N0", c), report.ZeroUvAreaTriangles.ToString("N0", c), report.DegenerateTriangles.ToString("N0", c)));
            NoteRow(rows, L.Tr("Edges: {0} open · {1} non-manifold · {2} with flipped winding", report.BoundaryEdges.ToString("N0", c), report.NonManifoldEdges.ToString("N0", c), report.InconsistentWindingEdges.ToString("N0", c)));
            foreach (var line in report.Diagnostics) NoteRow(rows, line, NoteKind.Info);
        }

        void DrawMeshBakeKind(UiRows rows, MeshBakeWindow host, MeshMapKind kind, MeshMapExpectation expected)
        {
            var s = meshBakeSettings;
            // 名前と「このマップを焼く」
            var head = rows.Row(22);
            bool on = s.Includes(kind), last = on && s.Maps.Length == 1;
            string bakeLabel = L.Tr("Bake this map");
            float toggleWidth = PaintGui.TextWidth(bakeLabel, PaintTheme.Label) + 30;
            PaintGui.Text(new Rect(head.x, head.y, head.width - toggleWidth - 8, head.height), MeshMapLabel(kind), PaintTheme.LabelBold);
            bool next = PaintGui.Toggle(host.Spot("bake.include", new Rect(head.xMax - toggleWidth, head.y, toggleWidth, head.height)), bakeLabel, on, last ? L.Tr("At least one map stays checked.") : null, !last);
            if (next != on) SetMeshBakeKind(kind, next);
            NoteRow(rows, MeshMapDescription(kind));
            // 焼いたマップの状態
            if (!meshMaps.TryGet(kind, out var map)) NoteRow(rows, L.Tr("Not baked yet"), NoteKind.Info);
            else
            {
                var check = map.Provenance.Check(expected);
                NoteRow(rows, L.Tr("Baked map: {0} ({1} × {2}, slot {3})", MeshMapStateName(check.State), map.Width, map.Height, SlotList(map.Provenance)), check.State == MeshMapState.Stale ? NoteKind.Warning : NoteKind.Info);
                if (check.State == MeshMapState.Stale) NoteRow(rows, L.Tr("{0} is stale and is not used: {1} Bake again to update it.", MeshMapLabel(kind), string.Join(" ", check.Reasons).Replace(";", "; ")), NoteKind.Warning);
                else if (check.State == MeshMapState.Unverified) NoteRow(rows, L.Tr("No model is loaded, so the maps cannot be checked against it."), NoteKind.Info);
            }
            rows.Space(2);
            switch (kind)
            {
                case MeshMapKind.AmbientOcclusion:
                case MeshMapKind.BentNormal:
                    PaintGui.GroupLabel(rows.Row(16), L.TrIn("mesh bake", "Rays"));
                    s.AoSamples = PaintGui.KeepIntSlider(host.Spot("bake.ao.rays", rows.Row()), L.TrIn("mesh map", "Rays"), s.AoSamples, 1, 512, "", L.Tr("Rays per texel (more is smoother and slower)"));
                    s.AoMaxDistance = PaintGui.KeepSlider(host.Spot("bake.ao.distance", rows.Row()), L.TrIn("mesh map", "Distance"), s.AoMaxDistance, .001, 1, "0.###", "", L.Tr("Max distance, relative to the model's bounding-box diagonal"));
                    s.AoSpreadDegrees = PaintGui.KeepSlider(rows.Row(), L.TrIn("mesh map", "Spread"), s.AoSpreadDegrees, 1, 180, "0", "°", L.Tr("180° = the whole hemisphere"));
                    s.AoIgnoreBackfaces = PaintGui.FitToggle(rows.Row(), L.Tr("Ignore back faces"), s.AoIgnoreBackfaces, L.Tr("Rays pass through faces seen from behind"));
                    if (kind == MeshMapKind.AmbientOcclusion)
                        BakeChoice(host, rows.Row(), L.TrIn("mesh map", "Falloff"), s.AoFalloff, (MeshOcclusionFalloff[])Enum.GetValues(typeof(MeshOcclusionFalloff)), FalloffName, v => meshBakeSettings.AoFalloff = v,
                            L.Tr("Linear: nearer occluders darken more"));
                    NoteRow(rows, L.Tr("The rays are shared by ambient occlusion and the bent normal; occluders and the GPU are in Common Settings."), NoteKind.Info);
                    break;
                case MeshMapKind.Thickness:
                    PaintGui.GroupLabel(rows.Row(16), L.TrIn("mesh bake", "Rays"));
                    s.ThicknessSamples = PaintGui.KeepIntSlider(host.Spot("bake.thickness.rays", rows.Row()), L.TrIn("mesh map", "Rays"), s.ThicknessSamples, 1, 512, "", L.Tr("Rays per texel (more is smoother and slower)"));
                    s.ThicknessMaxDistance = PaintGui.KeepSlider(rows.Row(), L.TrIn("mesh map", "Distance"), s.ThicknessMaxDistance, .001, 1, "0.###", "", L.Tr("Max distance, relative to the bounding-box diagonal; thicker parts read 1"));
                    s.ThicknessSpreadDegrees = PaintGui.KeepSlider(rows.Row(), L.TrIn("mesh map", "Spread"), s.ThicknessSpreadDegrees, 1, 180, "0", "°", L.Tr("180° = the whole hemisphere"));
                    NoteRow(rows, L.Tr("Occluders and the GPU are in Common Settings."), NoteKind.Info);
                    break;
                case MeshMapKind.Curvature:
                    s.CurvatureRadius = PaintGui.KeepSlider(host.Spot("bake.curvature.radius", rows.Row()), L.TrIn("mesh map", "Radius"), s.CurvatureRadius, MeshBakeSettings.MinCurvatureRadius, .2, "0.###", "",
                        L.Tr("Edges within this distance count (relative to the bounding-box diagonal). Larger = wider, softer edges"));
                    break;
                case MeshMapKind.Id:
                    BakeChoice(host, rows.Row(), L.Tr("Colors from"), s.IdSource, IdSourceChoices, IdSourceName, v => meshBakeSettings.IdSource = v,
                        L.Tr("What gets its own colour in the ID map"));
                    NoteRow(rows, IdSourceDescription(s.IdSource));
                    IdColorAssignmentRows(rows, host.Repaint);
                    if ((s.IdSource == MeshIdSource.MaterialSlot || s.IdSource == MeshIdSource.Mesh) && highPolyModel == null)
                        NoteRow(rows, L.Tr("Without a high poly, every texel of one texture set has the same slot and mesh, so this ID map is one colour. Use mesh parts, UV islands or vertex colours, or choose a high poly."), NoteKind.Warning);
                    break;
                default:
                    NoteRow(rows, L.Tr("This map has no settings of its own; Common Settings apply."), NoteKind.Plain);
                    break;
            }
            if ((kind == MeshMapKind.TangentNormal || kind == MeshMapKind.Height || kind == MeshMapKind.Opacity) && highPolyModel == null)
                NoteRow(rows, L.Tr("No high poly is chosen (Common Settings), so this map is uniform."), NoteKind.Warning);
        }

        /// <summary>種類の説明（右の設定の上）。</summary>
        static string MeshMapDescription(MeshMapKind kind)
        {
            switch (kind)
            {
                case MeshMapKind.WorldNormal: return L.Tr("The model's smooth (vertex) normals in model space: world axes, origin at the model root, stored as n × 0.5 + 0.5.");
                case MeshMapKind.Position: return L.Tr("Positions normalized to the model's bounding box (0–1 on each axis).");
                case MeshMapKind.AmbientOcclusion: return L.Tr("How open each texel is: 1 = nothing blocks it, 0 = blocked close by in every direction.");
                case MeshMapKind.Curvature: return L.Tr("0.5 is flat; brighter is convex (outer edges), darker is concave (inner corners).");
                case MeshMapKind.Thickness: return L.Tr("How far rays travel inward before leaving the other side, divided by the max distance: 0 = thin, 1 = thick.");
                case MeshMapKind.TangentNormal: return L.Tr("The high poly's normals in this model's tangent space (OpenGL, Y+). Flat where the high poly is missed.");
                case MeshMapKind.Height: return L.Tr("The signed distance from this model to the high poly along the projection (outside is brighter). 0.5 where it is missed.");
                case MeshMapKind.Id: return L.Tr("A flat colour for each part (material slot, mesh, connected mesh part or UV island) or each triangle's vertex colour. Select by these colours with Shift+W or the ID colour generator.");
                case MeshMapKind.BentNormal: return L.Tr("The average unblocked direction of the ambient occlusion rays (world space).");
                default: return L.Tr("1 where the high poly is hit, 0 where it is missed (1 wherever this model covers when there is no high poly).");
            }
        }

        /// <summary>ID の色の元の並び（ドロップダウン）。</summary>
        static readonly MeshIdSource[] IdSourceChoices = { MeshIdSource.MeshPart, MeshIdSource.UvIsland, MeshIdSource.MaterialSlot, MeshIdSource.MaterialAsset, MeshIdSource.Mesh, MeshIdSource.VertexColor };
        /// <summary>ID の色の元の説明（右の設定の、元の選択の下）。</summary>
        static string IdSourceDescription(MeshIdSource source)
        {
            switch (source)
            {
                case MeshIdSource.MaterialAsset: return L.Tr("Slots using the same material asset share an ID colour, including the high poly. Different assets keep different colours; names are not compared.");
                case MeshIdSource.MeshPart: return L.Tr("Each connected piece of the mesh (triangles sharing edges in 3D, across UV seams) gets its own colour, split the same way as the polygon fill's Mesh Part.");
                case MeshIdSource.UvIsland: return L.Tr("Each UV island gets its own colour, split the same way as the polygon fill's UV Island.");
                case MeshIdSource.Mesh: return L.Tr("Each mesh (renderer) gets its own colour; with a high poly, each of its meshes.");
                case MeshIdSource.VertexColor: return L.Tr("Each triangle takes the vertex colour most of its corners have (not blended, so every texel is a colour of the mesh). White when the mesh has no vertex colours.");
                default: return L.Tr("Each material slot (a renderer's submesh) gets its own colour; with a high poly, each of its slots.");
            }
        }

        /// <summary>名前と値の箱のドロップダウン（窓用。選んだら持ち主と窓を描き直す）。</summary>
        void BakeChoice<T>(MeshBakeWindow host, Rect r, string label, T value, T[] values, Func<T, string> name, Action<T> changed, string tooltip = null)
        {
            PaintGui.FitDropdown(r, label, name(value), at =>
            {
                var menu = new GenericMenu();
                foreach (var v in values) { var item = v; menu.AddItem(new GUIContent(name(item)), Equals(item, value), () => { changed(item); Repaint(); host.Repaint(); }); }
                menu.DropDown(at);
            }, tooltip, GUI.enabled, BakeLabelWidth);
        }

        // ───────── 下の帯 ─────────

        void DrawMeshBakeFooter(Rect foot, MeshBakeWindow host, string refusal)
        {
            PaintGui.Fill(foot, PaintTheme.PanelHeader); PaintGui.HLine(foot.x, foot.xMax, foot.y, PaintTheme.Border);
            var job = meshBakeJob;
            string bakeText = L.Tr("Bake Checked Maps"), cancelText = L.Tr("Cancel"), closeText = L.TrIn("mesh bake", "Close");
            float bakeWidth = Mathf.Max(PaintGui.TextWidth(bakeText, PaintTheme.Label), PaintGui.TextWidth(cancelText, PaintTheme.Label)) + 48;
            float closeWidth = Mathf.Max(90, PaintGui.TextWidth(closeText, PaintTheme.Label) + 32);
            var bake = new Rect(foot.xMax - 12 - bakeWidth, foot.y + (foot.height - 32) / 2, bakeWidth, 32);
            var close = new Rect(bake.x - 8 - closeWidth, bake.y, closeWidth, 32);
            var status = new Rect(foot.x + 12, foot.y + 8, close.x - 16 - foot.x - 12, foot.height - 14);
            if (job != null)
            {
                // 進み具合: 棒と「段階… nn% · 経過」
                var bar = new Rect(status.x, status.y + 4, status.width, 8);
                PaintGui.Rounded(bar, PaintTheme.ControlBg, 4);
                float f = Mathf.Clamp01((float)job.Fraction);
                if (f > 0) PaintGui.Rounded(new Rect(bar.x, bar.y, Mathf.Max(8, bar.width * f), bar.height), job.Canceling ? PaintTheme.TextDim : PaintTheme.Accent, 4);
                var elapsed = job.Clock.Elapsed;
                string text = MeshBakeSetText(job.Set, meshBakeFinished, meshBakeTotal) + MeshBakeProgressText(job) + " · " + ((int)elapsed.TotalMinutes) + ":" + elapsed.Seconds.ToString("00", CultureInfo.InvariantCulture);
                PaintGui.Text(new Rect(status.x, bar.yMax + 4, status.width, 20), PaintGui.Fit(text, status.width, PaintTheme.Label, false), PaintTheme.Label);
                if (PaintGui.Button(host.Spot("bake.cancel", bake), cancelText, false, !job.Canceling, L.Tr("Stop the bake; the previous maps stay as they are"), "close")) CancelMeshBake();
            }
            else
            {
                string text; Color color; string icon;
                if (refusal != null) { text = refusal; color = PaintTheme.Warning; icon = "warning"; }
                else if (meshBakeOutcome != null) { text = meshBakeOutcome; color = meshBakeOutcomeOk ? PaintTheme.TextDim : PaintTheme.Warning; icon = meshBakeOutcomeOk ? "check" : "info"; }
                else { text = L.Tr("Check the maps to bake on the left, and pick one to see its settings."); color = PaintTheme.TextDim; icon = "info"; }
                DrawMeshBakeStatus(status, text, icon, color);
                if (PaintGui.Button(host.Spot("bake.start", bake), bakeText, true, refusal == null && GUI.enabled, refusal ?? L.Tr("Bake the checked maps from the loaded model. The model, its materials and textures are not changed."), "local_fire_department"))
                    TryAction(() => StartMeshBake());
            }
            if (PaintGui.Button(host.Spot("bake.close", close), closeText, false, true, job != null ? L.Tr("The bake goes on; Texture Set Settings ▸ Mesh Maps shows its progress.") : null)) host.CloseSoon();
        }

        /// <summary>下の帯の知らせ（アイコンと、折り返した文。入らない分は切り、全文はツールチップに）。</summary>
        static void DrawMeshBakeStatus(Rect r, string text, string icon, Color color)
        {
            var lines = PaintGui.WrapLines(text, r.width - 22, PaintTheme.Wrap);
            float lineHeight = PaintTheme.Wrap.lineHeight > 0 ? PaintTheme.Wrap.lineHeight : 14;
            int fit = Mathf.Max(1, Mathf.FloorToInt(r.height / lineHeight));
            PaintGui.Icon(new Rect(r.x, r.y, 16, 16), icon, color, 14);
            for (int i = 0; i < Mathf.Min(fit, lines.Length); i++)
            {
                string line = i == fit - 1 && lines.Length > fit ? lines[i].TrimEnd() + "…" : lines[i];
                PaintGui.Text(new Rect(r.x + 22, r.y + i * lineHeight, r.width - 22, lineHeight), line, PaintTheme.Wrap, color);
            }
            PaintGui.Tooltip(r, text);
        }

        /// <summary>プロパティの欄の、ベイクの進み具合と取消の行（窓を閉じても見えるように）。</summary>
        void DrawMeshBakeProgressRow(UiRows rows)
        {
            var job = meshBakeJob; var row = rows.Row(26, 6);
            var bar = new Rect(row.x, row.y, row.width - 30, row.height);
            PaintGui.Rounded(bar, PaintTheme.ControlBg, 3);
            float f = Mathf.Clamp01((float)job.Fraction);
            if (f > 0) PaintGui.Rounded(new Rect(bar.x, bar.y, Mathf.Max(6, bar.width * f), bar.height), job.Canceling ? PaintTheme.ControlHover : PaintTheme.AccentDim, 3);
            string text = MeshBakeSetText(job.Set, meshBakeFinished, meshBakeTotal) + MeshBakeProgressText(job);
            PaintGui.Text(new Rect(bar.x + 7, bar.y, bar.width - 14, bar.height), PaintGui.Fit(text, bar.width - 14, PaintTheme.Label, false), PaintTheme.Label);
            PaintGui.Tooltip(bar, L.Tr("Baking mesh maps") + ": " + text);
            if (PaintGui.IconButton(Spot("meshmap.cancel", new Rect(row.xMax - 26, row.y, 26, row.height)), "close", L.Tr("Stop the bake; the previous maps stay as they are"), false, !job.Canceling, 16)) CancelMeshBake();
        }
    }
}
