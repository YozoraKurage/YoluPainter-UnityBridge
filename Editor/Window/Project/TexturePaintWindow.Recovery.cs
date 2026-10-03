using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
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
        internal long LastRecoveryWrittenBytes { get; private set; }
        internal int LastRecoveryReusedFiles { get; private set; }
        string RecoveryState() => (projectPath ?? "") + "\n" + (projectToken ?? "") + "\n" + ProjectUnchanged();
        byte[] RecoveryInfoBytes() => Encoding.UTF8.GetBytes(JsonUtility.ToJson(new RecoveryCatalog.Info
        {
            title = projectPath != null ? Path.GetFileName(projectPath) : string.Join(", ", textureSets.Select(s => s.Name)),
            projectPath = projectPath, projectToken = projectToken, unchanged = ProjectUnchanged()
        }));
        void RestoreRecovery(string root)
        {
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
            if (CanRemoveRecovery())
                try { RecoveryCatalog.Delete(recoveryRoot, this); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            recoveryRoot = RecoveryCatalog.NewRoot(); recoveryToken = null; recoveredProjectState = null;
        }
        void CleanupRecoveryOnClose()
        {
            if (!CanRemoveRecovery() || RecoveryCatalog.IsOpen(recoveryRoot, this)) return;
            try { RecoveryCatalog.Delete(recoveryRoot, this); }
            catch (Exception ex) { Debug.LogWarning(L.Tr("The saved window's recovery folder could not be removed: {0}", ex.Message)); }
        }
        void CheckRecoveryStorage()
        {
            lastRecoveryStorageCheck = EditorApplication.timeSinceStartup;
            try { var notice = RecoveryCatalog.StorageNotice(RecoveryCatalog.DirectoryBytes(RecoveryCatalog.BaseRoot)); if (notice != null) message = notice; }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
