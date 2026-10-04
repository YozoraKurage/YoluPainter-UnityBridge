using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>機能の印（挨拶の features の各ビット。スタンドアロンの yolu-protocol の <c>feature</c> と同じ割り当て）。</summary>
    internal static class LiveLinkFeatures
    {
        public const ulong MaterialValues = 1UL << 0;
        public const ulong Assets = 1UL << 1;
        public const ulong ProjectTransfer = 1UL << 2;
        public const ulong Animation = 1UL << 3;
        public const ulong Known = MaterialValues | Assets | ProjectTransfer | Animation;

        /// <summary>印の名前をビットの小さい順に並べる（名前を知らない印は「Newer features」にまとめる）。</summary>
        public static string Names(ulong mask)
        {
            var names = new List<string>();
            if ((mask & MaterialValues) != 0) names.Add(L.Tr("Material values"));
            if ((mask & Assets) != 0) names.Add(L.Tr("Assets"));
            if ((mask & ProjectTransfer) != 0) names.Add(L.Tr("Project transfer"));
            if ((mask & Animation) != 0) names.Add(L.Tr("Animation"));
            if ((mask & ~Known) != 0) names.Add(L.Tr("Newer features"));
            return string.Join(L.IsJapanese ? "・" : ", ", names);
        }
    }

    /// <summary>版の欄（ブリッジの C の関数が返す <c>major &lt;&lt; 32 | minor &lt;&lt; 16 | patch</c>。不明は <c>ulong.MaxValue</c>）。</summary>
    internal static class LiveLinkVersions
    {
        public const ulong NoneBits = ulong.MaxValue;

        public static Version Unpack(ulong packed) => packed == NoneBits ? null : new Version((int)((packed >> 32) & 0xffff), (int)((packed >> 16) & 0xffff), (int)(packed & 0xffff));

        public static ulong Pack(Version v) => v == null ? NoneBits : ((ulong)Math.Min(0xffff, v.Major) << 32) | ((ulong)Math.Min(0xffff, v.Minor) << 16) | (ulong)Math.Min(0xffff, Math.Max(0, v.Build));

        /// <summary>要る版の指定が無い（0.0.0）か。</summary>
        public static bool Unspecified(Version v) => v == null || (v.Major == 0 && v.Minor == 0 && v.Build <= 0);
    }

    /// <summary>
    /// ブリッジが返す、版のずれと機能の印の様子（<c>ylb_link_report</c>）。Unity のパッケージとスタンドアロンは別の製品で版の番号が別なので、版同士は比べず、
    /// 各側が「これ以上の相手」と宣言する版に足りているかで決める（ブリッジの中で決めた結果を読むだけ）。
    /// </summary>
    internal sealed class LiveLinkReport
    {
        internal enum Kind { None = 0, Connected = 1, Refused = 2 }

        public static readonly LiveLinkReport Empty = new LiveLinkReport();

        public Kind State;
        /// <summary>相手（スタンドアロン）のアプリの版。版を名乗らない古い相手は null。</summary>
        public Version PeerVersion;
        /// <summary>スタンドアロンを上げると解けるずれがある（求める版に足りない・版を名乗らない・自分の持つ機能が相手に無い）。</summary>
        public bool UpdateStandalone;
        /// <summary>スタンドアロンの求める版（無い・指定なしは null か 0.0.0）。</summary>
        public Version StandaloneTo;
        /// <summary>Unity のパッケージを上げると解けるずれがある（求める版に足りない・相手の持つ機能がこちらに無い）。</summary>
        public bool UpdatePackage;
        public Version PackageTo;
        public ulong OwnFeatures, PeerFeatures, CommonFeatures, MissingOnPeer, MissingHere;
        /// <summary>断られたとき（<see cref="Kind.Refused"/>）、上げるべき製品: 1 = Unity のパッケージ、2 = スタンドアロン。</summary>
        public int RefusedUpdate;
        public Version RefusedTo;
        public int UnityMinProtocol, UnityMaxProtocol, StandaloneMinProtocol, StandaloneMaxProtocol;
        public int Protocol;

        /// <summary>つながっていて、警告に値するずれがあるか。</summary>
        public bool Skewed => State == Kind.Connected && (UpdateStandalone || UpdatePackage);
        /// <summary>プロトコルの版の範囲が合わずに断られた。</summary>
        public bool Refused => State == Kind.Refused;

        internal static LiveLinkReport From(YlbLinkReport raw)
        {
            var r = new LiveLinkReport
            {
                State = raw.state == 1 ? Kind.Connected : raw.state == 2 ? Kind.Refused : Kind.None,
                Protocol = (int)raw.protocol,
                PeerVersion = LiveLinkVersions.Unpack(raw.peer_version),
                OwnFeatures = raw.own_features, PeerFeatures = raw.peer_features, CommonFeatures = raw.common_features,
                MissingOnPeer = raw.missing_on_peer, MissingHere = raw.missing_here,
                RefusedUpdate = raw.refused_update, RefusedTo = LiveLinkVersions.Unpack(raw.refused_to),
                UnityMinProtocol = (int)raw.unity_min_protocol, UnityMaxProtocol = (int)raw.unity_max_protocol,
                StandaloneMinProtocol = (int)raw.standalone_min_protocol, StandaloneMaxProtocol = (int)raw.standalone_max_protocol,
            };
            // update_peer: スタンドアロンを上げるべきなら求める版（不明でなければ上げるべき）。update_self: Unity のパッケージ
            r.StandaloneTo = LiveLinkVersions.Unpack(raw.update_peer);
            r.UpdateStandalone = raw.update_peer != LiveLinkVersions.NoneBits || raw.missing_on_peer != 0;
            r.PackageTo = LiveLinkVersions.Unpack(raw.update_self);
            r.UpdatePackage = raw.update_self != LiveLinkVersions.NoneBits || raw.missing_here != 0;
            return r;
        }
    }

    /// <summary>
    /// 版のずれの見せ方（窓の状態の行の印・ツールチップ・断られたときの理由）。文は状態と理由だけで、使い方は書かない。
    /// つないだまま警告するだけで、つなぐのを妨げない（プロトコルの版が重ならないときだけスタンドアロンが断る）。
    /// </summary>
    internal static class LiveLinkNotice
    {
        /// <summary>自分（Unity のパッケージ）の版。分からなければ null。</summary>
        public static string OwnVersion => PackagePaths.Version == "unknown" ? null : PackagePaths.Version;

        static string Unknown => L.Tr("unknown");

        /// <summary>「○○ を {0} 以上に上げる必要があります」（版の指定が無ければ「○○ を更新する必要があります」）。</summary>
        static string UpdateLine(bool package, Version to)
        {
            if (package) return LiveLinkVersions.Unspecified(to) ? L.Tr("The Unity package must be updated.") : L.Tr("The Unity package must be {0} or newer.", to);
            return LiveLinkVersions.Unspecified(to) ? L.Tr("The standalone must be updated.") : L.Tr("The standalone must be {0} or newer.", to);
        }

        /// <summary>つないだまま版がずれているときのツールチップ: 両方の版・どちらを上げればよいか・使えない機能の名前。ずれが無ければ null。</summary>
        public static string Tooltip(LiveLinkReport report, string ownVersion)
        {
            if (report == null || !report.Skewed) return null;
            var lines = new List<string>
            {
                L.Tr("Unity package {0} · standalone {1}", ownVersion ?? Unknown, report.PeerVersion != null ? report.PeerVersion.ToString() : Unknown),
            };
            if (report.UpdateStandalone) lines.Add(UpdateLine(false, report.StandaloneTo));
            if (report.UpdatePackage) lines.Add(UpdateLine(true, report.PackageTo));
            ulong apart = report.MissingOnPeer | report.MissingHere;
            if (apart != 0) lines.Add(L.Tr("Unavailable: {0}", LiveLinkFeatures.Names(apart)));
            return string.Join("\n", lines);
        }

        /// <summary>プロトコルの版の範囲が合わずに断られたときの理由（範囲と、どちらを何版以上に上げるか）。断られていなければ null。</summary>
        public static string Refusal(LiveLinkReport report)
        {
            if (report == null || !report.Refused) return null;
            string range = L.Tr("Protocol versions do not match (Unity {0}–{1}, standalone {2}–{3}).", report.UnityMinProtocol, report.UnityMaxProtocol, report.StandaloneMinProtocol, report.StandaloneMaxProtocol);
            if (report.RefusedUpdate != 1 && report.RefusedUpdate != 2) return range;
            return range + (L.IsJapanese ? "" : " ") + UpdateLine(report.RefusedUpdate == 1, report.RefusedTo);
        }

        /// <summary>窓の状態の行: 文字と、ずれがあるときの警告の印とツールチップ。</summary>
        public static GUIContent StateContent(string state, LiveLinkReport report, string ownVersion)
        {
            string tip = Tooltip(report, ownVersion);
            if (tip == null) return new GUIContent(state);
            return new GUIContent(state, EditorGUIUtility.IconContent("console.warnicon.sml").image, tip);
        }
    }
}
