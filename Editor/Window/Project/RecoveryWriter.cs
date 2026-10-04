using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>窓ごとに一つの書き手。実行中一件と最新の待機一件だけを持ち、Unity の API は呼ばない。</summary>
    internal sealed class RecoveryWriter
    {
        internal sealed class Set
        {
            internal Guid Id;
            internal PaintDocument Document;
        }
        internal sealed class Request
        {
            internal Set[] Sets;
            internal YlpProjectInfo Project;
            internal ResourceIndex.Snapshot Resources;
            internal byte[] Info;
            internal YlpWriterInfo Writer, CreatedBy;
            internal string State, StorageRoot;
            internal long SetsRevision;
            internal int Keep;
        }
        internal sealed class Result
        {
            internal Request Request;
            internal GenerationSnapshot Saved;
            internal Exception Error;
            internal long StorageBytes;
            internal double NativeMilliseconds, WorkMilliseconds;
            internal GenerationTimings Timings = new GenerationTimings();
        }
        readonly object gate = new object();
        readonly Queue<Result> results = new Queue<Result>();
        readonly string root;
        string token;
        Request pending;
        Task task;
        bool running;
        // 決定的な回帰試験用。所有スレッドで、最初の Submit より前に設定する。
        internal Action<string> FaultInjection;
        internal RecoveryWriter(string root, string token) { this.root = root; this.token = token; }
        internal bool IsIdle { get { lock (gate) return !running; } }
        internal void Submit(Request request)
        {
            lock (gate)
            {
                pending = request;
                if (running) return;
                running = true; task = Task.Run(WriteLoop);
            }
        }
        internal void Wait()
        {
            Task writing; lock (gate) writing = task;
            writing?.GetAwaiter().GetResult();
        }
        internal Result TakeResult() { lock (gate) return results.Count > 0 ? results.Dequeue() : null; }
        void WriteLoop()
        {
            while (true)
            {
                Request request;
                lock (gate)
                {
                    request = pending; pending = null;
                    if (request == null) { running = false; return; }
                }
                var result = new Result { Request = request, StorageBytes = -1 };
                var timer = System.Diagnostics.Stopwatch.StartNew();
                bool pointerCommitted = false;
                try
                {
                    FaultInjection?.Invoke("snapshot");
                    long nativeStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [RecoveryCatalog.InfoName] = request.Info };
                    foreach (var set in request.Sets)
                    {
                        files.Add(YlpFormat.SetEntry(set.Id, YlpArchive.NativeName), DocumentBinary.Write(set.Document));
                        if (set.Document.Selection != null) files.Add(YlpFormat.SetEntry(set.Id, SelectionBinary.EntryName), SelectionBinary.Write(set.Document.Selection));
                    }
                    result.NativeMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - nativeStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    files.Add(YlpFormat.ProjectName, YlpFormat.WriteProject(request.Project));
                    ResourceIndex.AddTo(files, request.Resources);
                    YlpFormat.Stamp(files, request.Writer, request.CreatedBy);
                    result.Saved = GenerationStore.Commit(root, files, token, stage =>
                    {
                        if (stage == "after-pointer") pointerCommitted = true;
                        FaultInjection?.Invoke(stage);
                    }, request.Keep, shareContents: true, timings: result.Timings);
                    token = result.Saved.Token; result.Saved.Files.Clear(); // 大きなバイト列を主スレッドへ持ち越さない。
                }
                catch (Exception ex)
                {
                    result.Error = ex;
                    // 自分が current を確定した後で通知を失った場合だけ、検証して次回の印を合わせる。
                    if (pointerCommitted)
                        try { token = GenerationStore.Load(root).Token; } catch (Exception) { }
                }
                try { result.StorageBytes = RecoveryCatalog.DirectoryBytes(request.StorageRoot); }
                catch (Exception) { }
                result.WorkMilliseconds = timer.Elapsed.TotalMilliseconds;
                lock (gate) results.Enqueue(result);
            }
        }
    }
}
