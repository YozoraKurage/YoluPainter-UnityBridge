using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core.MeshMaps;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// mesh map のベイクの AO・ベントノーマル・厚みのレイを計算シェーダー（Shaders/MeshBake.compute）で処理する。BVH は CPU で作ったものを
    /// そのまま上げ、式は CPU の基準（MeshBaker）と同じで、精度だけが float になる。使えない GPU（計算シェーダーが無い、シェーダーが
    /// コンパイルできない、メモリの予算を超える）では Prepare が理由を返し、CPU で焼く。途中で失敗したら例外を投げ、MeshBaker が CPU で
    /// 焼き直す。Unity の GPU の呼び出しは主スレッドでしかできないので、別のスレッドからの呼び出しは主スレッドの <see cref="Pump"/> に
    /// 回して待つ（ウィンドウは進捗バーを出す合間に呼ぶ）。高ポリへの投影（サンプルあたり 1 本）は CPU のまま。
    /// </summary>
    internal sealed class GpuMeshBakeRayTracer : IMeshBakeRayTracer
    {
        public static string ShaderPath => PackagePaths.Asset("Shaders/MeshBake.compute");
        /// <summary>1 回の Dispatch で処理するサンプルの数の上限（バッファの大きさ）。</summary>
        public const int DispatchJobs = 65536;
        /// <summary>
        /// 1 回の Dispatch と読み戻しの時間の目標。Windows の GPU のタイムアウト（TDR、既定 2 秒。WSL の /dev/dxg を通すとコンテナごと
        /// 落ちうる）から十分に離すため、サンプルの数を時間で決める: 最初は <see cref="FirstDispatchJobs"/> から始め、測った時間が目標の
        /// 半分より短ければ倍に、目標を超えれば半分にする。1 サンプルのレイの数（AO + 厚み）と形の込み具合で 1 サンプルの重さは
        /// 100 倍以上違うので、決め打ちの数より時間で合わせる方が安全。分け方は結果に影響しない（サンプルどうしは独立）。
        /// </summary>
        public const double TargetDispatchMilliseconds = 50;
        public const int FirstDispatchJobs = 256, MinDispatchJobs = 64;
        /// <summary>1 回の Dispatch のレイの数の上限（時間を測る前の最初の 1 回でも重すぎないように）。</summary>
        public const long MaxRaysPerDispatch = 4L * 1024 * 1024;

        [StructLayout(LayoutKind.Sequential)] struct GpuJob { public Vector3 O; public int Ignore; public Vector3 N; public int Scene; public Vector3 G; public float Shift, Rc, Rs; public Vector2 Pad; }
        [StructLayout(LayoutKind.Sequential)] struct GpuResult { public float Ao; public Vector3 Bent; public float Thickness; public int Rays; public Vector2 Pad; }
        [StructLayout(LayoutKind.Sequential)] struct GpuNode { public Vector3 Min; public int First; public Vector3 Max; public int Count; }
        [StructLayout(LayoutKind.Sequential)] struct GpuTri { public Vector3 A; public int Original; public Vector3 E1; public float Epsilon; public Vector3 E2; public float Pad; }

        readonly long maxBytes;
        readonly Thread mainThread = Thread.CurrentThread;
        readonly Queue<(Action action, ManualResetEventSlim done, Exception[] error)> pending = new Queue<(Action, ManualResetEventSlim, Exception[])>();
        ComputeShader shader; int kernel = -1;
        ComputeBuffer nodes, tris, aoDirs, thDirs, jobs, results;
        GpuJob[] jobData; GpuResult[] resultData;
        int chunk = FirstDispatchJobs, raysPerJob = 1;
        public long UploadedBytes { get; private set; }
        /// <summary>これまでで最も長かった 1 回の Dispatch と読み戻しの時間（ミリ秒）と、Dispatch の回数。</summary>
        public double MaxDispatchMilliseconds { get; private set; }
        public int Dispatches { get; private set; }

        readonly string name;
        /// <param name="maxBytes">GPU に置く BVH・方向・仕事・結果の合計の上限。</param>
        /// <remarks>主スレッドで作る（名前に使うデバイスの情報は主スレッドでしか読めない。ベイクは別のスレッドから Name を読む）。</remarks>
        public GpuMeshBakeRayTracer(long maxBytes)
        {
            this.maxBytes = maxBytes;
            name = "GPU (" + SystemInfo.graphicsDeviceName + ", " + SystemInfo.graphicsDeviceType + ")";
        }

        public string Name => name;

        /// <summary>これがあると GPU のベイクを使えないと答える（CPU で焼く）。開発環境のテストの台に .devcontainer の common.sh が設定する。</summary>
        public const string OffEnvironmentVariable = "YOLUPAINTER_GPU_BAKE_OFF";
        /// <summary>この環境で使えない理由（使えれば null）。主スレッドで呼ぶ。</summary>
        public static string Unavailable()
        {
            // 開発環境のテストの台だけ: GPU のベイクの compute で GPU のデバイスが消えて Unity が落ちる件（2026-10-03）の原因が分かるまで止める
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OffEnvironmentVariable))) return "GPU baking is turned off in this environment (" + OffEnvironmentVariable + ")";
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return "no graphics device";
            if (!SystemInfo.supportsComputeShaders) return "this graphics device has no compute shaders";
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            if (cs == null) return "the package's MeshBake.compute could not be loaded";
            foreach (var message in ShaderUtil.GetComputeShaderMessages(cs))
                if (message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error) return "MeshBake.compute has compile errors (" + message.message + ")";
            if (!cs.HasKernel("Trace")) return "MeshBake.compute has no Trace kernel";
            return null;
        }

        /// <summary>別のスレッドから来た GPU の仕事を、主スレッドでここで行う。</summary>
        public void Pump()
        {
            while (true)
            {
                (Action action, ManualResetEventSlim done, Exception[] error) item;
                lock (pending) { if (pending.Count == 0) return; item = pending.Dequeue(); }
                try { item.action(); } catch (Exception ex) { item.error[0] = ex; }
                item.done.Set();
            }
        }
        void OnMain(Action action)
        {
            if (Thread.CurrentThread == mainThread) { action(); return; }
            var error = new Exception[1];
            using (var done = new ManualResetEventSlim(false))
            {
                lock (pending) pending.Enqueue((action, done, error));
                done.Wait();
            }
            if (error[0] != null) throw new InvalidOperationException(error[0].Message, error[0]);
        }

        public string Prepare(MeshBakeRayScene scene)
        {
            string why = null;
            OnMain(() => why = PrepareOnMain(scene));
            return why;
        }

        string PrepareOnMain(MeshBakeRayScene scene)
        {
            string unavailable = Unavailable(); if (unavailable != null) return unavailable;
            if (scene.SceneCount < 1 || scene.SceneCount > 2) return "unexpected scene count";
            int nodeTotal = 0, triTotal = 0;
            for (int i = 0; i < scene.SceneCount; i++) { nodeTotal += scene.NodeFirst[i].Length; triTotal += scene.TriangleOriginal[i].Length; }
            long bytes = (long)Math.Max(1, nodeTotal) * 32 + (long)Math.Max(1, triTotal) * 48 + (scene.AoU.Length + scene.ThicknessU.Length + 2) * 16L
                + (long)DispatchJobs * (Marshal.SizeOf<GpuJob>() + Marshal.SizeOf<GpuResult>());
            if (bytes > maxBytes) return "it needs " + (bytes >> 20) + " MiB of GPU memory, over the " + (maxBytes >> 20) + " MiB GPU cache budget (Project Settings > YoluPainter)";
            shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath); kernel = shader.FindKernel("Trace");
            var nodeData = new GpuNode[Math.Max(1, nodeTotal)]; var triData = new GpuTri[Math.Max(1, triTotal)];
            int nodeBase = 0, triBase = 0; var nodeBases = new int[2]; var triBases = new int[2]; var nodeCounts = new int[2];
            for (int s = 0; s < scene.SceneCount; s++)
            {
                var b = scene.NodeBounds[s]; var first = scene.NodeFirst[s]; var count = scene.NodeCount[s]; var t = scene.Triangles[s]; var original = scene.TriangleOriginal[s];
                nodeBases[s] = nodeBase; triBases[s] = triBase; nodeCounts[s] = first.Length;
                for (int i = 0; i < first.Length; i++)
                    nodeData[nodeBase + i] = new GpuNode { Min = new Vector3(b[i * 6], b[i * 6 + 1], b[i * 6 + 2]), First = first[i], Max = new Vector3(b[i * 6 + 3], b[i * 6 + 4], b[i * 6 + 5]), Count = count[i] };
                for (int i = 0; i < original.Length; i++)
                    triData[triBase + i] = new GpuTri { A = new Vector3(t[i * 10], t[i * 10 + 1], t[i * 10 + 2]), Original = original[i], E1 = new Vector3(t[i * 10 + 3], t[i * 10 + 4], t[i * 10 + 5]), Epsilon = t[i * 10 + 9], E2 = new Vector3(t[i * 10 + 6], t[i * 10 + 7], t[i * 10 + 8]) };
                nodeBase += first.Length; triBase += original.Length;
            }
            try
            {
                nodes = new ComputeBuffer(nodeData.Length, Marshal.SizeOf<GpuNode>()); nodes.SetData(nodeData);
                tris = new ComputeBuffer(triData.Length, Marshal.SizeOf<GpuTri>()); tris.SetData(triData);
                aoDirs = Directions(scene.AoU, scene.AoCos, scene.AoSin); thDirs = Directions(scene.ThicknessU, scene.ThicknessCos, scene.ThicknessSin);
                jobs = new ComputeBuffer(DispatchJobs, Marshal.SizeOf<GpuJob>()); results = new ComputeBuffer(DispatchJobs, Marshal.SizeOf<GpuResult>());
            }
            catch (Exception ex) { ReleaseOnMain(); return "GPU buffers could not be created (" + ex.Message + ")"; }
            jobData = new GpuJob[DispatchJobs]; resultData = new GpuResult[DispatchJobs];
            UploadedBytes = bytes; chunk = FirstDispatchJobs;
            raysPerJob = Math.Max(1, (scene.WantAo ? scene.AoU.Length : 0) + (scene.WantThickness ? scene.ThicknessU.Length : 0));
            shader.SetBuffer(kernel, "_Nodes", nodes); shader.SetBuffer(kernel, "_Tris", tris); shader.SetBuffer(kernel, "_AoDirs", aoDirs); shader.SetBuffer(kernel, "_ThDirs", thDirs);
            shader.SetBuffer(kernel, "_Jobs", jobs); shader.SetBuffer(kernel, "_Results", results);
            shader.SetInt("_NodeBase0", nodeBases[0]); shader.SetInt("_NodeBase1", nodeBases[1]); shader.SetInt("_TriBase0", triBases[0]); shader.SetInt("_TriBase1", triBases[1]);
            shader.SetInt("_NodeCount0", nodeCounts[0]); shader.SetInt("_NodeCount1", nodeCounts[1]);
            shader.SetInt("_AoCount", scene.AoU.Length); shader.SetInt("_ThCount", scene.ThicknessU.Length);
            shader.SetFloat("_AoCos2", (float)scene.AoCos2); shader.SetFloat("_ThCos2", (float)scene.ThicknessCos2); shader.SetFloat("_AoMax", (float)scene.AoMax); shader.SetFloat("_ThMax", (float)scene.ThicknessMax);
            shader.SetFloat("_RayOffset", (float)scene.RayOffset); shader.SetFloat("_Grazing", (float)scene.GrazingLimit);
            shader.SetInt("_AoAnyHit", scene.AoAnyHit ? 1 : 0); shader.SetInt("_AoIgnoreBack", scene.AoIgnoreBackfaces ? 1 : 0);
            shader.SetInt("_WantAo", scene.WantAo ? 1 : 0); shader.SetInt("_WantThickness", scene.WantThickness ? 1 : 0);
            return null;
        }
        static ComputeBuffer Directions(double[] u, double[] cos, double[] sin)
        {
            var data = new Vector4[Math.Max(1, u.Length)];
            for (int i = 0; i < u.Length; i++) data[i] = new Vector4((float)u[i], (float)cos[i], (float)sin[i], 0);
            var buffer = new ComputeBuffer(data.Length, 16); buffer.SetData(data); return buffer;
        }

        public void Trace(MeshBakeRayJob[] input, int count, MeshBakeRayResult[] output) => OnMain(() => TraceOnMain(input, count, output));

        void TraceOnMain(MeshBakeRayJob[] input, int count, MeshBakeRayResult[] output)
        {
            if (shader == null || jobs == null) throw new InvalidOperationException("The GPU ray tracer was not prepared.");
            var clock = new System.Diagnostics.Stopwatch();
            int limit = (int)Math.Max(MinDispatchJobs, Math.Min(DispatchJobs, MaxRaysPerDispatch / raysPerJob));
            for (int start = 0; start < count;)
            {
                int n = Math.Min(Math.Min(chunk, limit), count - start);
                using var gate = GpuHeavyWorkGate.Enter(); // 開発環境のテストの台だけ: 台をまたいで GPU の重い仕事を 1 つずつ（待つ時間は測らない）
                clock.Restart();
                for (int i = 0; i < n; i++)
                {
                    ref var j = ref input[start + i];
                    jobData[i] = new GpuJob { O = new Vector3((float)j.Ox, (float)j.Oy, (float)j.Oz), Ignore = j.Ignore, N = new Vector3((float)j.Nx, (float)j.Ny, (float)j.Nz), Scene = j.Scene,
                        G = new Vector3((float)j.Gx, (float)j.Gy, (float)j.Gz), Shift = (float)j.Shift, Rc = (float)j.Rc, Rs = (float)j.Rs };
                }
                jobs.SetData(jobData, 0, 0, n);
                shader.SetInt("_JobCount", n); shader.SetInt("_JobOffset", 0);
                shader.Dispatch(kernel, (n + 63) / 64, 1, 1);
                results.GetData(resultData, 0, 0, n); // 待つ（同期の読み戻し。ベイクは描画の経路ではない）
                double milliseconds = clock.Elapsed.TotalMilliseconds;
                Dispatches++; MaxDispatchMilliseconds = Math.Max(MaxDispatchMilliseconds, milliseconds);
                for (int i = 0; i < n; i++)
                {
                    var r = resultData[i];
                    if (float.IsNaN(r.Ao) || float.IsNaN(r.Thickness) || float.IsNaN(r.Bent.x)) throw new InvalidOperationException("the GPU returned NaN");
                    output[start + i] = new MeshBakeRayResult { Ao = r.Ao, BentX = r.Bent.x, BentY = r.Bent.y, BentZ = r.Bent.z, Thickness = r.Thickness, Rays = r.Rays };
                }
                start += n;
                // 次の 1 回の大きさを時間で合わせる（満杯で回したときだけ増やす）
                if (milliseconds > TargetDispatchMilliseconds) chunk = Math.Max(MinDispatchJobs, chunk / 2);
                else if (milliseconds < TargetDispatchMilliseconds / 2 && n == chunk) chunk = Math.Min(DispatchJobs, chunk * 2);
            }
        }

        public void Release() => OnMain(ReleaseOnMain);
        void ReleaseOnMain()
        {
            nodes?.Release(); tris?.Release(); aoDirs?.Release(); thDirs?.Release(); jobs?.Release(); results?.Release();
            nodes = tris = aoDirs = thDirs = jobs = results = null; jobData = null; resultData = null; UploadedBytes = 0;
        }
    }
}
