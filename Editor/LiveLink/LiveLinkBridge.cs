using System;
using System.Text;
using UnityEditor;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>ブリッジの知らせ。</summary>
    internal enum LiveLinkEventKind { Connected = 1, Rejected = 2, Failed = 3, Closed = 4, SetAdded = 5, SetRemoved = 6, PeerError = 7, Note = 8 }

    internal readonly struct LiveLinkEvent
    {
        public readonly LiveLinkEventKind Kind; public readonly uint Set; public readonly int Code; public readonly string Text;
        public LiveLinkEvent(LiveLinkEventKind kind, uint set, int code, string text) { Kind = kind; Set = set; Code = code; Text = text; }
    }

    /// <summary>つながりの状態（ylb_status）。</summary>
    internal enum LiveLinkStatus { Unknown = -1, Connecting = 0, Connected = 1, Closed = 2, Failed = 3 }

    /// <summary>
    /// ブリッジの DLL（yolu_bridge。Plugins/LiveLink の Linux の .so と Windows の .dll、エディタだけ）の口。初めて使うときに DLL を読み、
    /// 最初に版（ylb_abi_version）を確かめる。合わなければほかの関数を呼ばない。Unity は一度読んだ DLL を手放さないので、ドメインの
    /// 読み直しの後は前のドメインのつながりを ylb_disconnect_all で切る（DLL を使ったドメインがあったときだけ。使わない人には DLL を読ませない）。
    /// </summary>
    internal static unsafe class LiveLinkBridge
    {
        /// <summary>この C# が知っているブリッジの版。</summary>
        public const uint ExpectedAbi = 4;
        const string LoadedKey = "Yozolab.YoluPainter.LiveLink.BridgeLoaded";
        static bool s_checked; static string s_problem; static uint s_abi, s_protocols;

        /// <summary>使えない理由（使えれば null）。初めて呼んだときに DLL を読む。</summary>
        public static string Problem { get { Check(); return s_problem; } }
        public static bool Available => Problem == null;
        public static uint AbiVersion { get { Check(); return s_abi; } }
        /// <summary>ブリッジの読めるプロトコルの版の範囲。</summary>
        public static (int Min, int Max) ProtocolVersions { get { Check(); return ((int)(s_protocols >> 16), (int)(s_protocols & 0xffff)); } }

        static void Check()
        {
            if (s_checked) return;
            s_checked = true;
            try
            {
                s_abi = LiveLinkNative.ylb_abi_version();
                SessionState.SetBool(LoadedKey, true);
                if (s_abi != ExpectedAbi) { s_problem = L.Tr("The Live Link library is version {0}, not {1}. Unity needs a restart.", s_abi, ExpectedAbi); return; }
                s_protocols = LiveLinkNative.ylb_protocol_versions();
            }
            catch (DllNotFoundException e) { s_problem = L.Tr("The Live Link library (yolu_bridge) could not be loaded: {0}", e.Message); }
            catch (EntryPointNotFoundException e) { s_problem = L.Tr("The Live Link library is missing a function ({0}). Unity needs a restart.", e.Message); }
            catch (BadImageFormatException e) { s_problem = L.Tr("The Live Link library (yolu_bridge) could not be loaded: {0}", e.Message); }
        }

        [InitializeOnLoadMethod]
        static void CleanUpPreviousDomain()
        {
            // DLL は前のドメインから読まれたまま。前のドメインのつながり（C# の側はもう無い）を切る
            if (!SessionState.GetBool(LoadedKey, false)) return;
            if (Available) LiveLinkNative.ylb_disconnect_all();
        }

        static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s ?? "");

        delegate int TextReader(byte* buffer, int capacity);
        static string ReadText(TextReader read)
        {
            var buf = new byte[256];
            int n;
            fixed (byte* p = buf) n = read(p, buf.Length);
            if (n < 0) return null;
            if (n > buf.Length) { buf = new byte[n]; fixed (byte* p = buf) n = read(p, buf.Length); }
            return Encoding.UTF8.GetString(buf, 0, Math.Min(n, buf.Length));
        }

        /// <summary>つなぎ始める（待たない）。0 なら名前が使えない。<paramref name="appVersion"/> は Unity のパッケージの版（「0.3.0」など。
        /// 読めない・空なら名乗らない）で、挨拶でスタンドアロンに伝わる。</summary>
        public static ulong Connect(string name, string agent, string appVersion = "")
        {
            if (!Available) return 0;
            byte[] n = Utf8(name), a = Utf8(agent), v = Utf8(appVersion);
            fixed (byte* pn = n) fixed (byte* pa = a) fixed (byte* pv = v) return LiveLinkNative.ylb_connect_with(pn, n.Length, pa, a.Length, pv, v.Length);
        }

        /// <summary>このつながりで使える機能の印（双方が出した印の共通部分。つながるまでは 0）。印の要る新しい命令は、立っているときだけ送る。</summary>
        public static ulong CommonFeatures(ulong handle) => handle == 0 || !Available ? 0 : LiveLinkNative.ylb_common_features(handle);

        /// <summary>相手（スタンドアロン）のアプリの版。つながっていない・版を名乗らない古い相手は null。</summary>
        public static Version PeerAppVersion(ulong handle) => handle == 0 || !Available ? null : LiveLinkVersions.Unpack(LiveLinkNative.ylb_peer_app_version(handle));

        /// <summary>版のずれと機能の印の様子（まだ無ければ <see cref="LiveLinkReport.State"/> が None）。</summary>
        public static LiveLinkReport Report(ulong handle)
        {
            if (handle == 0 || !Available) return LiveLinkReport.Empty;
            YlbLinkReport raw;
            int state = LiveLinkNative.ylb_link_report(handle, &raw);
            return state < 0 ? LiveLinkReport.Empty : LiveLinkReport.From(raw);
        }

        public static void Disconnect(ulong handle) { if (handle != 0 && Available) LiveLinkNative.ylb_disconnect(handle); }

        public static LiveLinkStatus Status(ulong handle) => !Available ? LiveLinkStatus.Unknown : (LiveLinkStatus)Math.Max(-1, LiveLinkNative.ylb_status(handle));

        public static string StatusText(ulong handle) => ReadText((p, cap) => LiveLinkNative.ylb_status_text(handle, p, cap));

        public static ulong Serial(ulong handle) => LiveLinkNative.ylb_serial(handle);

        /// <summary>知らせを 1 つ取り出す。</summary>
        public static bool NextEvent(ulong handle, out LiveLinkEvent e)
        {
            var buf = new byte[1024]; YlbEvent raw; int r;
            fixed (byte* p = buf) r = LiveLinkNative.ylb_next_event(handle, &raw, p, buf.Length);
            if (r != 1) { e = default; return false; }
            e = new LiveLinkEvent((LiveLinkEventKind)raw.kind, raw.set, raw.code, Encoding.UTF8.GetString(buf, 0, Math.Max(0, Math.Min(raw.text_len, buf.Length))));
            return true;
        }

        public static int SetCount(ulong handle) => LiveLinkNative.ylb_set_count(handle);

        public static bool SetInfo(ulong handle, int index, out YlbSetInfo info)
        {
            YlbSetInfo raw;
            bool ok = LiveLinkNative.ylb_set_info(handle, index, &raw) == 0;
            info = raw;
            return ok;
        }

        public static string SetName(ulong handle, uint set) => ReadText((p, cap) => LiveLinkNative.ylb_set_name(handle, set, p, cap));

        // ───────── モデル・ポーズの組み立て ─────────

        public static int ModelBegin(ulong handle, string name) { var b = Utf8(name); fixed (byte* p = b) return LiveLinkNative.ylb_model_begin(handle, p, b.Length); }

        public static int ModelMaterial(ulong handle, bool unassigned, string name, string guid, long fileId, string shader)
        {
            byte[] n = Utf8(name), g = Utf8(guid), s = Utf8(shader);
            fixed (byte* pn = n) fixed (byte* pg = g) fixed (byte* ps = s)
                return LiveLinkNative.ylb_model_material(handle, unassigned ? 1 : 0, pn, n.Length, pg, g.Length, fileId, ps, s.Length);
        }

        public static int ModelMaterialTexture(ulong handle, int material, string property, int width, int height)
        {
            var b = Utf8(property);
            fixed (byte* p = b) return LiveLinkNative.ylb_model_material_texture(handle, material, p, b.Length, (uint)Math.Max(0, width), (uint)Math.Max(0, height));
        }

        public static int ModelMaterialRoute(ulong handle, int material, int channel, string property)
        {
            var b = Utf8(property);
            fixed (byte* p = b) return LiveLinkNative.ylb_model_material_route(handle, material, channel, p, b.Length);
        }

        public static int ModelMesh(ulong handle, string key, string name, bool skinned, float[] positions, float[] normals, float[] uv0, int vertexCount)
        {
            byte[] k = Utf8(key), n = Utf8(name);
            fixed (byte* pk = k) fixed (byte* pn = n) fixed (float* pp = positions) fixed (float* pnr = normals) fixed (float* pu = uv0)
                return LiveLinkNative.ylb_model_mesh(handle, pk, k.Length, pn, n.Length, skinned ? 1 : 0, pp, normals != null && normals.Length > 0 ? pnr : null, uv0 != null && uv0.Length > 0 ? pu : null, vertexCount);
        }

        public static int ModelSubmesh(ulong handle, int mesh, int material, int[] indices)
        {
            fixed (int* p = indices) return LiveLinkNative.ylb_model_submesh(handle, mesh, material, p, indices?.Length ?? 0);
        }

        // マテリアルの更新（モデルを送り直さずに、シェーダー・テクスチャのプロパティ・流し込み先だけを変える）

        public static int MaterialsBegin(ulong handle) => LiveLinkNative.ylb_materials_begin(handle);

        public static int MaterialsMaterial(ulong handle, bool unassigned, string name, string guid, long fileId, string shader)
        {
            byte[] n = Utf8(name), g = Utf8(guid), s = Utf8(shader);
            fixed (byte* pn = n) fixed (byte* pg = g) fixed (byte* ps = s)
                return LiveLinkNative.ylb_materials_material(handle, unassigned ? 1 : 0, pn, n.Length, pg, g.Length, fileId, ps, s.Length);
        }

        public static int MaterialsTexture(ulong handle, int material, string property, int width, int height)
        {
            var b = Utf8(property);
            fixed (byte* p = b) return LiveLinkNative.ylb_materials_texture(handle, material, p, b.Length, (uint)Math.Max(0, width), (uint)Math.Max(0, height));
        }

        public static int MaterialsRoute(ulong handle, int material, int channel, string property)
        {
            var b = Utf8(property);
            fixed (byte* p = b) return LiveLinkNative.ylb_materials_route(handle, material, channel, p, b.Length);
        }

        public static int MaterialsSend(ulong handle) => LiveLinkNative.ylb_materials_send(handle);

        public static int ModelSend(ulong handle) => LiveLinkNative.ylb_model_send(handle);
        public static int ModelClose(ulong handle) => LiveLinkNative.ylb_model_close(handle);
        public static int PoseBegin(ulong handle) => LiveLinkNative.ylb_pose_begin(handle);

        public static int PoseMesh(ulong handle, int mesh, float[] positions, float[] normals, int vertexCount)
        {
            fixed (float* pp = positions) fixed (float* pn = normals)
                return LiveLinkNative.ylb_pose_mesh(handle, mesh, pp, normals != null && normals.Length > 0 ? pn : null, vertexCount);
        }

        public static int PoseSend(ulong handle) => LiveLinkNative.ylb_pose_send(handle);

        // ───────── マテリアルの値（機能の印 MaterialValues がスタンドアロンにもあるときだけ送る） ─────────

        /// <summary>機能の印: マテリアルの値（lilToon のプロパティの値と描いていないスロットの絵）。</summary>
        public const ulong FeatureMaterialValues = 1UL << 0;

        /// <summary>値の組み立てを始める。<paramref name="lilToon"/> が偽なら「値なし」（前に送った値を捨てさせる）。</summary>
        public static int ValuesBegin(ulong handle, int material, bool lilToon, string shader, string source)
        {
            byte[] s = Utf8(shader), o = Utf8(source);
            fixed (byte* ps = s) fixed (byte* po = o) return LiveLinkNative.ylb_values_begin(handle, material, lilToon ? 1 : 0, ps, s.Length, po, o.Length);
        }

        public static int ValuesFloat(ulong handle, string name, float value) { var b = Utf8(name); fixed (byte* p = b) return LiveLinkNative.ylb_values_float(handle, p, b.Length, value); }
        public static int ValuesInt(ulong handle, string name, int value) { var b = Utf8(name); fixed (byte* p = b) return LiveLinkNative.ylb_values_int(handle, p, b.Length, value); }
        public static int ValuesColor(ulong handle, string name, UnityEngine.Color c) { var b = Utf8(name); fixed (byte* p = b) return LiveLinkNative.ylb_values_color(handle, p, b.Length, c.r, c.g, c.b, c.a); }
        public static int ValuesVector(ulong handle, string name, UnityEngine.Vector4 v) { var b = Utf8(name); fixed (byte* p = b) return LiveLinkNative.ylb_values_vector(handle, p, b.Length, v.x, v.y, v.z, v.w); }
        public static int ValuesKeyword(ulong handle, string name) { var b = Utf8(name); fixed (byte* p = b) return LiveLinkNative.ylb_values_keyword(handle, p, b.Length); }

        /// <summary>描いていないスロットの様子（<see cref="LiveLinkSlotState"/>）と元のテクスチャの大きさ。</summary>
        public static int ValuesSlot(ulong handle, string name, LiveLinkSlotState state, int width, int height)
        {
            var b = Utf8(name);
            fixed (byte* p = b) return LiveLinkNative.ylb_values_slot(handle, p, b.Length, (int)state, (uint)Math.Max(0, width), (uint)Math.Max(0, height));
        }

        /// <summary>組み立てた値を送る。1 = 送った、0 = スタンドアロンに印が無いので送らない、負は失敗。</summary>
        public static int ValuesSend(ulong handle) => LiveLinkNative.ylb_values_send(handle);

        /// <summary>描いていないスロットの絵を送る（RGBA8、行は下から）。1 = 送った、0 = 印が無いので送らない、負は失敗。</summary>
        public static int TextureSend(ulong handle, int material, string slot, int width, int height, bool srgb, byte[] pixels)
        {
            var b = Utf8(slot);
            fixed (byte* p = b) fixed (byte* px = pixels)
                return LiveLinkNative.ylb_texture_send(handle, material, p, b.Length, (uint)width, (uint)height, srgb ? 1 : 0, px, pixels?.Length ?? 0);
        }
    }

    /// <summary>描いていないスロットの絵の様子（ylb_values_slot）。</summary>
    internal enum LiveLinkSlotState { Empty = 0, Follows = 1, Unchanged = 2, OverBudget = 3, Unreadable = 4 }

    /// <summary>
    /// ブリッジの中の自己診断のスタンドアロン（同じプロセスで本物のソケットと共有メモリを通し、マテリアルごとの色の市松を返す）。
    /// スタンドアロン無しで Live Link を確かめる（窓の「診断」と試験）。
    /// </summary>
    internal static unsafe class LiveLinkTestServer
    {
        /// <summary>待ち受けを始める（0 は失敗）。モデルが来ると、マテリアルごとに size × size のセットを返す。</summary>
        public static ulong Start(string name, int size, int tileSize)
        {
            if (!LiveLinkBridge.Available) return 0;
            var b = Encoding.UTF8.GetBytes(name);
            fixed (byte* p = b) return LiveLinkNative.ylb_test_server_start(p, b.Length, size, tileSize);
        }

        public static void Stop(ulong server) { if (server != 0 && LiveLinkBridge.Available) LiveLinkNative.ylb_test_server_stop(server); }

        /// <summary>模様の色（マテリアルの番号とタイルの座標から）。</summary>
        public static UnityEngine.Color32 Pattern(uint material, uint tileX, uint tileY)
        {
            uint v = LiveLinkNative.ylb_test_server_pattern(material, tileX, tileY);
            return new UnityEngine.Color32((byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24));
        }

        /// <summary>マテリアルの番号のセットの、タイル [x0, x1) × [y0, y1) を塗って知らせる。塗ったタイルの数を返す。</summary>
        public static int Paint(ulong server, uint material, PaintChannel channel, uint x0, uint y0, uint x1, uint y1, UnityEngine.Color32 color)
            => LiveLinkNative.ylb_test_server_paint(server, material, (int)channel, x0, y0, x1, y1, color.r | (uint)color.g << 8 | (uint)color.b << 16 | (uint)color.a << 24);

        /// <summary>自己診断のスタンドアロンの名乗りを決める（次につなぐものから効く）。<paramref name="appVersion"/> が null なら版を名乗らない古いスタンドアロンの役、
        /// <paramref name="minPeer"/> は求める Unity のパッケージの版（null は要求なし）、<paramref name="features"/> は出す機能の印。</summary>
        public static bool Configure(ulong server, Version appVersion, Version minPeer, ulong features)
            => LiveLinkNative.ylb_test_server_configure(server, LiveLinkVersions.Pack(appVersion), LiveLinkVersions.Pack(minPeer ?? new Version(0, 0, 0)), features) == 0;

        /// <summary>読めるプロトコルの版の範囲を決める（次につなぐものから効く。つなぐ側の範囲と重ならなければ、版の範囲の断りを返す）。</summary>
        public static bool SetProtocol(ulong server, int min, int max) => LiveLinkNative.ylb_test_server_set_protocol(server, (uint)Math.Max(0, min), (uint)Math.Max(0, max)) == 0;

        /// <summary>鍵のファイルを別の鍵に差し替える（つなぎ直すと鍵の断りを受ける）。</summary>
        public static bool ReplaceKey(ulong server) => LiveLinkNative.ylb_test_server_replace_key(server) == 0;

        public static YlbTestServerStats Stats(ulong server)
        {
            YlbTestServerStats stats;
            LiveLinkNative.ylb_test_server_stats(server, &stats);
            return stats;
        }

        /// <summary>最後に受けた、マテリアルの番号 <paramref name="material"/> の値のプロパティ。型（0 Float・1 Int・2 Color・3 Vector）を返し、無ければ負。</summary>
        public static int Value(ulong server, int material, string name, out UnityEngine.Vector4 value)
        {
            var b = Encoding.UTF8.GetBytes(name);
            float* v = stackalloc float[4];
            int r;
            fixed (byte* p = b) r = LiveLinkNative.ylb_test_server_value(server, (uint)material, p, b.Length, v);
            value = new UnityEngine.Vector4(v[0], v[1], v[2], v[3]);
            return r;
        }

        /// <summary>最後に受けた値の、スロットの様子（無ければ負）。</summary>
        public static int Slot(ulong server, int material, string name)
        {
            var b = Encoding.UTF8.GetBytes(name);
            fixed (byte* p = b) return LiveLinkNative.ylb_test_server_slot(server, (uint)material, p, b.Length, 0);
        }

        /// <summary>最後に受けた値に、キーワードがあるか。</summary>
        public static bool HasKeyword(ulong server, int material, string name)
        {
            var b = Encoding.UTF8.GetBytes(name);
            fixed (byte* p = b) return LiveLinkNative.ylb_test_server_slot(server, (uint)material, p, b.Length, 1) == 1;
        }

        /// <summary>最後に受けた、スロットの絵の様子（無ければ false）。</summary>
        public static bool Texture(ulong server, int material, string slot, out YlbTestServerTexture texture)
        {
            var b = Encoding.UTF8.GetBytes(slot);
            YlbTestServerTexture t;
            int r;
            fixed (byte* p = b) r = LiveLinkNative.ylb_test_server_texture(server, (uint)material, p, b.Length, &t);
            texture = t;
            return r == 0;
        }
    }
}
