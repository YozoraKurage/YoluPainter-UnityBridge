using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Editor
{
    public sealed partial class TexturePaintWindow
    {
        string recoveredProjectState;
        bool retainingRecoveryRoot;
        double lastRecoveryStorageCheck;
        RecoveryWriter recoveryWriter;
        string lastRecoveryRequest;
        bool recoveryFailed;
        Task<long> recoveryStorageTask;
        Action<string> recoveryFaultInjection;
        internal Action<string> RecoveryFaultInjection
        {
            get => recoveryFaultInjection;
            set { recoveryFaultInjection = value; if (recoveryWriter != null) recoveryWriter.FaultInjection = value; }
        }
        internal double LastRecoveryCaptureMilliseconds { get; private set; }
        internal double LastRecoveryWorkMilliseconds { get; private set; }
        internal double LastRecoveryNativeMilliseconds { get; private set; }
        internal GenerationTimings LastRecoveryTimings { get; private set; }
        internal long LastRecoveryWrittenBytes { get; private set; }
        internal int LastRecoveryReusedFiles { get; private set; }
        string RecoveryRequestKey() => setsRevision + "\n" + RecoveryState() + "\n" + string.Join(",", textureSets.Select(s => s.Id + ":" + s.Document.Revision));
        /// <summary>主スレッドでは写しだけを取り、書き出し・ハッシュ検証・ディスクの処理は書き手へ渡す。</summary>
        bool SaveRecovery()
        {
            PollRecovery();
            if (document == null || currentSet == null || stroke != null) return true;
            SyncCurrentSet();
            if ((recoveryWriter == null || recoveryWriter.IsIdle) && RecoveryIsCurrent()) return true;
            string key = RecoveryRequestKey();
            if (recoveryWriter != null && !recoveryWriter.IsIdle && key == lastRecoveryRequest) return true;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            lastRecovery = EditorApplication.timeSinceStartup; // 失敗時も次の通常間隔で再試行する。
            try
            {
                var request = new RecoveryWriter.Request
                {
                    Sets = textureSets.Select(s => new RecoveryWriter.Set { Id = s.Id, Document = s.Document.CaptureSnapshot() }).ToArray(),
                    Project = ProjectInfo(), Resources = ResourceIndex.Capture(resources), Info = RecoveryInfoBytes(),
                    Writer = YlpContent.Writer, CreatedBy = projectCreatedBy, State = RecoveryState(), SetsRevision = setsRevision,
                    StorageRoot = RecoveryCatalog.BaseRoot, Keep = PainterSettings.RecoveryGenerationsToKeep
                };
                if (recoveryWriter == null) recoveryWriter = new RecoveryWriter(recoveryRoot, recoveryToken) { FaultInjection = RecoveryFaultInjection };
                recoveryWriter.Submit(request); lastRecoveryRequest = key; return true;
            }
            catch (Exception ex) { ShowRecoveryFailure(ex); return false; }
            finally { LastRecoveryCaptureMilliseconds = timer.Elapsed.TotalMilliseconds; }
        }
        void PollRecovery()
        {
            RecoveryWriter.Result result;
            while (recoveryWriter != null && (result = recoveryWriter.TakeResult()) != null)
            {
                if (result.Error != null) { lastRecoveryRequest = null; ShowRecoveryFailure(result.Error); continue; }
                recoveryToken = result.Saved.Token;
                LastRecoveryWorkMilliseconds = result.WorkMilliseconds; LastRecoveryNativeMilliseconds = result.NativeMilliseconds; LastRecoveryTimings = result.Timings;
                LastRecoveryWrittenBytes = result.Saved.WrittenContentBytes; LastRecoveryReusedFiles = result.Saved.ReusedContentFiles;
                foreach (var saved in result.Request.Sets)
                {
                    var set = textureSets.FirstOrDefault(s => s.Id == saved.Id);
                    if (set != null) set.RecoveredRevision = saved.Document.Revision;
                }
                recoveredSetsRevision = result.Request.SetsRevision; recoveredProjectState = result.Request.State;
                if (recoveryFailed) { recoveryFailed = false; message = L.Tr("Recovery checkpoint saved. Automatic recovery is working again."); Repaint(); }
                var notice = RecoveryCatalog.StorageNotice(result.StorageBytes); if (notice != null) { message = notice; Repaint(); }
                lastRecoveryStorageCheck = EditorApplication.timeSinceStartup;
            }
            if (recoveryStorageTask != null && recoveryStorageTask.IsCompleted)
            {
                if (recoveryStorageTask.Status == TaskStatus.RanToCompletion)
                { var notice = RecoveryCatalog.StorageNotice(recoveryStorageTask.Result); if (notice != null) { message = notice; Repaint(); } }
                else { _ = recoveryStorageTask.Exception; }
                recoveryStorageTask = null;
            }
        }
        void ShowRecoveryFailure(Exception error)
        {
            recoveryFailed = true; message = L.Tr("Recovery checkpoint failed: {0}", error.Message); Repaint();
        }
        internal bool FlushRecovery()
        {
            recoveryWriter?.Wait(); PollRecovery(); return RecoveryIsCurrent();
        }
        bool SaveRecoveryAndWait()
        {
            if (!SaveRecovery()) return false;
            if (FlushRecovery()) return true;
            // 同じ写しの保存中に終了要求が来て、その書き込みが失敗した場合も、最新を一度だけ再試行する。
            return SaveRecovery() && FlushRecovery();
        }
        void SaveRecoveryBeforeLifecycleChange()
        {
            if (!SaveRecoveryAndWait()) Debug.LogWarning(message);
        }
        string RecoveryState() => (projectPath ?? "") + "\n" + (projectToken ?? "") + "\n" + ProjectUnchanged();
        byte[] RecoveryInfoBytes() => Encoding.UTF8.GetBytes(JsonUtility.ToJson(new RecoveryCatalog.Info
        {
            title = projectPath != null ? Path.GetFileName(projectPath) : string.Join(", ", textureSets.Select(s => s.Name)),
            projectPath = projectPath, projectToken = projectToken, unchanged = ProjectUnchanged()
        }));
        void RestoreRecovery(string root)
        {
            recoveryWriter?.Wait(); PollRecovery();
            var snapshot = GenerationStore.Load(root);
            var files = new Dictionary<string, byte[]>(snapshot.Files, StringComparer.Ordinal); files.Remove(RecoveryCatalog.InfoName);
            var recovered = YlpFormat.Open(files);
            var sets = ReadTextureSets(recovered); var recoveredResources = ResourceIndex.Load(recovered.Files, recovered.Resources);
            RecoveryCatalog.Info info = null;
            if (snapshot.Files.TryGetValue(RecoveryCatalog.InfoName, out var bytes))
            {
                if (bytes.Length > 64 * 1024) throw new InvalidDataException("Recovery information exceeds its 64 KiB budget.");
                info = JsonUtility.FromJson<RecoveryCatalog.Info>(Encoding.UTF8.GetString(bytes));
            }
            // 全部の正本とリソースを読めてから今のプロジェクトを置き換える。
            retainingRecoveryRoot = string.Equals(root, recoveryRoot, PainterSettings.PathComparison);
            try { ReplaceProject(sets, sets.First(s => s.Id == recovered.Project.CurrentSet)); }
            finally { retainingRecoveryRoot = false; }
            AdoptResources(recoveredResources);
            recoveryRoot = root; recoveryToken = snapshot.Token;
            ResetSetsBaseline(false); projectCreatedBy = recovered.Info.CreatedBy; openedFormat = recovered.Info.Format;
            projectPath = null; projectToken = null; externalConflict = false;
            if (info != null)
            {
                projectPath = info.projectPath; projectToken = info.projectToken;
                if (info.unchanged)
                {
                    foreach (var set in sets)
                        if (string.IsNullOrEmpty(projectPath)) set.PristineRevision = set.Document.Revision;
                        else set.SavedRevision = set.Document.Revision;
                    if (!string.IsNullOrEmpty(projectPath)) savedSetsRevision = setsRevision;
                }
            }
            var notes = new List<string>();
            foreach (var set in sets) RestoreSavedSelection(set, recovered.SetFiles(set.Id), notes);
            foreach (var set in sets) set.RecoveredRevision = set.Document.Revision;
            recoveredSetsRevision = setsRevision; recoveredProjectState = RecoveryState();
            var missingImages = MissingFillImageNote(); if (missingImages != null) notes.Add(missingImages);
            message = L.Tr("Recovered native source from the last durable checkpoint. Unsaved edits after that checkpoint may be missing.") + (notes.Count > 0 ? " " + string.Join(" ", notes) : "");
        }
        internal bool OpenRecoveryAt(string root)
        {
            if (!RecoveryCatalog.IsRecoveryRoot(root) || RecoveryCatalog.IsOpen(root, this)) return false;
            if (stroke != null || !ConfirmDiscard()) return false;
            try { RestoreRecovery(root); BindDocument(); CheckRecoveryStorage(); Repaint(); return true; }
            catch (Exception ex) { message = L.Tr("Recovery was not loaded: {0}", ex.Message); return false; }
        }
        internal void ShowRecovery() => RecoveryBrowser.Open(this);
        bool CanRemoveRecovery() => ProjectUnchanged() && (string.IsNullOrEmpty(projectPath) || !YlpStore.HasExternalChange(projectPath, projectToken));
        /// <summary>別のプロジェクトへ移るとき、捨てると確認した未保存の作業も別の checkpoint として残す。</summary>
        void DetachRecoveryForProjectChange()
        {
            if (currentSet == null || retainingRecoveryRoot) return;
            if (!SaveRecoveryAndWait()) throw new IOException(message);
            recoveryWriter = null; lastRecoveryRequest = null;
            if (CanRemoveRecovery())
                try { RecoveryCatalog.Delete(recoveryRoot, this); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            recoveryRoot = RecoveryCatalog.NewRoot(); recoveryToken = null; recoveredProjectState = null;
        }
        void CleanupRecoveryOnClose()
        {
            FlushRecovery();
            if (!CanRemoveRecovery() || RecoveryCatalog.IsOpen(recoveryRoot, this)) return;
            try { RecoveryCatalog.Delete(recoveryRoot, this); }
            catch (Exception ex) { Debug.LogWarning(L.Tr("The saved window's recovery folder could not be removed: {0}", ex.Message)); }
        }
        void CheckRecoveryStorage()
        {
            lastRecoveryStorageCheck = EditorApplication.timeSinceStartup;
            if (recoveryStorageTask != null) return;
            string root = RecoveryCatalog.BaseRoot;
            recoveryStorageTask = Task.Run(() => RecoveryCatalog.DirectoryBytes(root));
        }
    }
}
