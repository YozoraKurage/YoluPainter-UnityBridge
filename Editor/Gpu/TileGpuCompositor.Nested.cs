using System;
using System.Collections.Generic;
using System.Text;
using Yozolab.YoluPainter.Core;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <remarks>
    /// <para>入れ子のグループの写し（GPU と CPU の経路で同じ規則）。最上段の「下の写し」と同じ考えを、グループの中身の並びにも当てる:
    /// ブロックごとにグループ 1 つにつき 1 つ、「中身の先頭 Index 項目まで合成した、グループの内側の結果」を残す。分離のグループ（クリッピングの
    /// あるグループも）は透明から合成した中身なので、グループの外（下の層・位置）が変わっても有効。通過のグループは下の結果の上に重ねた中身
    /// なので、下の結果がそのまま（グループの位置と、その下の項目、さらに外側の通過グループの下）のときだけ有効。</para>
    /// <para>前回の署名の木と今の木を比べて、最上段から「最初に違う項目」がグループなら中へ降り、中身の最初に違う項目を探す（違う道）。写しは
    /// この道に沿って、浅い段から取る（予算が足りなければ深い段は取らない。写しが無ければ今までどおりグループの始めから合成する）。署名の木が
    /// 前回と同じ所は、写しの中身も同じ（署名は CpuCompositor の計画とブロックの書き換え番号から作る）。クリッピングされたグループの中身は
    /// 写しを持たない（そのクリッピングの基の項目ごと、今までどおり合成する）。</para>
    /// </remarks>
    internal sealed partial class TileGpuCompositor
    {
        /// <summary>ブロックの署名の木: 計画の項目の署名（グループは中身を含む。最上段の比べ方は今までと同じ文字列）と、グループの中身の項目。</summary>
        internal sealed class SigNode
        {
            public string Sig; public Guid Id;
            /// <summary>グループ（クリッピングの基の項目としてのグループ。中身は Children）。</summary>
            public bool Group;
            /// <summary>中身を透明から合成するグループ（通過でない、またはクリッピングのある通過）。</summary>
            public bool Isolated;
            public SigNode[] Children;
        }

        SigNode[] BuildSigs(IReadOnlyList<CpuCompositor.StackEntry> plan, PaintChannel channel, int bx, int by)
        {
            var nodes = new SigNode[plan.Count];
            for (int i = 0; i < plan.Count; i++) nodes[i] = BuildSig(plan[i], channel, bx, by);
            return nodes;
        }
        SigNode BuildSig(CpuCompositor.StackEntry entry, PaintChannel channel, int bx, int by)
        {
            SigNode[] children = null;
            if (entry.Base.IsGroup)
            {
                children = new SigNode[entry.Children.Count];
                for (int i = 0; i < children.Length; i++) children[i] = BuildSig(entry.Children[i], channel, bx, by);
            }
            var sb = new StringBuilder(96);
            AppendSignature(sb, entry, channel, bx, by, children);
            return new SigNode { Sig = sb.ToString(), Id = entry.Base.Id, Group = entry.Base.IsGroup, Isolated = !entry.PassesThrough, Children = children };
        }
        static int FirstDifference(SigNode[] before, SigNode[] now)
        {
            int n = Math.Min(before.Length, now.Length);
            for (int i = 0; i < n; i++) if (!string.Equals(before[i].Sig, now[i].Sig, StringComparison.Ordinal)) return i;
            return n;
        }

        /// <summary>前回と今の署名の木の違い（ブロック 1 つ）。</summary>
        internal sealed class SigDiff
        {
            /// <summary>最上段で最初に違う項目（同じなら短いほうの数）。</summary>
            public int RootFirst;
            /// <summary>今の計画のグループごと（クリッピングの基の項目のグループだけ）。</summary>
            public readonly Dictionary<Guid, GroupDiff> Groups = new Dictionary<Guid, GroupDiff>();
            /// <summary>違う道: 最上段の最初に違う項目がグループなら、そのグループと中身の最初に違う項目、それもグループなら…の並び。</summary>
            public readonly List<(Guid Group, int Index)> Path = new List<(Guid, int)>();
        }
        internal sealed class GroupDiff
        {
            public SigNode Node;
            /// <summary>今の計画の中の位置（最上段の添え字、中身の添え字、…）。</summary>
            public int[] Address;
            /// <summary>中身で最初に違う項目（前回と比べられないとき −1: 前回に無い、または分離と通過が入れ替わった）。</summary>
            public int First = -1;
            /// <summary>中身を始める所が前回と同じ: 分離のグループは透明なのでいつも、通過のグループは同じ位置で、その下が同じとき。</summary>
            public bool StartUnchanged;
        }

        internal static SigDiff Diff(SigNode[] before, SigNode[] now)
        {
            var diff = new SigDiff { RootFirst = FirstDifference(before, now) };
            var old = new Dictionary<Guid, (SigNode node, Guid parent, int index)>();
            void Index(SigNode[] level, Guid parent)
            {
                for (int i = 0; i < level.Length; i++)
                    if (level[i].Group) { old[level[i].Id] = (level[i], parent, i); Index(level[i].Children, level[i].Id); }
            }
            Index(before, Guid.Empty);
            void Visit(SigNode[] level, Guid parent, int[] prefix, int levelFirst, bool levelStart)
            {
                for (int i = 0; i < level.Length; i++)
                {
                    var g = level[i]; if (!g.Group) continue;
                    var address = new int[prefix.Length + 1]; Array.Copy(prefix, address, prefix.Length); address[prefix.Length] = i;
                    var d = new GroupDiff { Node = g, Address = address };
                    if (old.TryGetValue(g.Id, out var o) && o.node.Isolated == g.Isolated)
                    {
                        d.First = FirstDifference(o.node.Children, g.Children);
                        // 下の結果が同じ: 親の段の始めが同じで、親の中身でこの位置より下が前回と同じ、前回も同じ親の同じ位置
                        d.StartUnchanged = g.Isolated || o.parent == parent && o.index == i && levelStart && levelFirst >= 0 && i <= levelFirst;
                    }
                    diff.Groups[g.Id] = d;
                    Visit(g.Children, g.Id, address, d.First, d.First >= 0 && d.StartUnchanged);
                }
            }
            Visit(now, Guid.Empty, new int[0], diff.RootFirst, true);
            var nodes = now; int first = diff.RootFirst;
            while (first < nodes.Length && nodes[first].Group && diff.Groups[nodes[first].Id].First >= 0)
            {
                var d = diff.Groups[nodes[first].Id];
                diff.Path.Add((d.Node.Id, d.First));
                nodes = d.Node.Children; first = d.First;
            }
            return diff;
        }

        /// <summary>グループの写し（ブロック 1 つ、グループ 1 つ）: 中身の先頭 Index 項目まで合成した内側の結果。GPU の経路は Texture、
        /// CPU の経路は Pixels（ブロックの画素を詰めた行）。</summary>
        internal sealed class GroupCopy
        {
            public Guid Group; public bool Isolated; public int Index; public int Depth;
            public RenderTexture Texture; public byte[] Pixels;
        }
        /// <summary>写しが今も正しいか: グループが今もあり、中身の先頭 Index 項目と始める所（通過なら下の結果）が前回と同じ。</summary>
        static bool StillValid(GroupCopy copy, SigDiff diff)
        {
            return diff != null && diff.Groups.TryGetValue(copy.Group, out var d) && d.First >= 0 && copy.Index <= d.First && d.StartUnchanged && d.Node.Isolated == copy.Isolated;
        }

        /// <summary>1 つのブロックの合成で使う入れ子の写し: 使える写し（グループの ID ごと）と、取る写し（グループの ID → 中身の添え字）。</summary>
        sealed class NestPlan
        {
            public readonly Dictionary<Guid, GroupCopy> Resume = new Dictionary<Guid, GroupCopy>();
            public readonly Dictionary<Guid, int> Capture = new Dictionary<Guid, int>();
            /// <summary>この合成で取った写し。</summary>
            public readonly Dictionary<Guid, GroupCopy> Taken = new Dictionary<Guid, GroupCopy>();
            /// <summary>先回りで取る写し（違う道に無い分離のグループの中身の全部。浅い順）: 空いた予算だけで取り、ほかの写しを捨てない。</summary>
            public readonly List<Guid> Spare = new List<Guid>();
            public SigDiff Diff;
        }
        /// <summary>前回の写しのうち今も正しいものを残し（ほかは捨てる）、違う道に沿って取る写しを決める（添え字 0 は始めと同じなので取らない。
        /// 同じ所に正しい写しがあれば取り直さない）。diff が null（前回と比べられない）なら全部捨てる。</summary>
        /// <param name="start">最上段で合成を始める項目（それより下のグループは合成しない）。</param>
        NestPlan PlanNested(Dictionary<Guid, GroupCopy> copies, SigDiff diff, bool capture, int start)
        {
            List<Guid> stale = null;
            foreach (var pair in copies) if (!StillValid(pair.Value, diff)) (stale ?? (stale = new List<Guid>())).Add(pair.Key);
            if (stale != null) foreach (var id in stale) { ReleaseGroupCopy(copies[id]); copies.Remove(id); }
            if (diff == null) return null;
            var plan = new NestPlan { Diff = diff };
            foreach (var pair in copies) plan.Resume.Add(pair.Key, pair.Value);
            if (!capture) return plan;
            foreach (var (group, index) in diff.Path)
                if (index > 0 && !(copies.TryGetValue(group, out var c) && c.Index == index)) plan.Capture.Add(group, index);
            // 先回り: 違う道に無い分離のグループを今回始めから合成するなら、中身の全部の写しを取っておく（分離のグループの中身は下の層に依らないので、
            // グループの外（下の層、グループ自身の不透明度・マスク・合成モード）が変わっても、中身を合成し直さずに済む）
            // （違う道のグループは中身が変わっているので除く）
            var onPath = new HashSet<Guid>(); foreach (var (group, _) in diff.Path) onPath.Add(group);
            var groups = new List<GroupDiff>(diff.Groups.Values);
            groups.Sort((a, b) => a.Address.Length.CompareTo(b.Address.Length));
            foreach (var d in groups)
            {
                var id = d.Node.Id;
                if (!d.Node.Isolated || d.Node.Children.Length == 0 || copies.ContainsKey(id) || onPath.Contains(id) || !Reached(d.Address, start, plan)) continue;
                plan.Capture.Add(id, d.Node.Children.Length); plan.Spare.Add(id);
            }
            return plan;
        }
        /// <summary>先回りの写しは、空いた予算のうち全体の半分までで取る（ほかの写しを捨てない。残りは違う道の写しと GPU に残すブロックのため）。</summary>
        bool SpareRoom(long bytes) => ResidentBytes + bytes <= ResidentBudgetBytes / 2;
        /// <summary>このグループの中身を始めから合成するはずか: 最上段の start 以上で、中身を写しから始める外側のグループの写しより上にある。</summary>
        static bool Reached(int[] address, int start, NestPlan plan)
        {
            if (address[0] < start) return false;
            foreach (var c in plan.Resume.Values)
            {
                var outer = plan.Diff.Groups[c.Group].Address;
                if (outer.Length >= address.Length) continue;
                bool prefix = true; for (int i = 0; i < outer.Length && prefix; i++) prefix = outer[i] == address[i];
                if (prefix && address[outer.Length] < c.Index) return false;
            }
            return true;
        }
        void ReleaseGroupCopy(GroupCopy copy)
        {
            if (copy.Texture != null) { Release(copy.Texture); ResidentBytes -= BlockBytes; copy.Texture = null; }
            if (copy.Pixels != null) { ResidentBytes -= copy.Pixels.Length; copy.Pixels = null; }
        }
        void ReleaseGroupCopies(Dictionary<Guid, GroupCopy> copies)
        {
            foreach (var c in copies.Values) ReleaseGroupCopy(c);
            copies.Clear();
        }
        /// <summary>写しを捨てて場所を作るとき、そのブロックの写しのうち最初に捨てるもの（深い段から）。</summary>
        static GroupCopy DeepestCopy(Dictionary<Guid, GroupCopy> copies)
        {
            GroupCopy deepest = null;
            foreach (var c in copies.Values) if (deepest == null || c.Depth > deepest.Depth) deepest = c;
            return deepest;
        }

        // 直近の Update の入れ子の写しの内訳（診断・テスト・計測用）
        /// <summary>グループの中身を写しから始めた回数（ブロック × グループ）。</summary>
        internal int LastNestedReuseCount { get; private set; }
        /// <summary>グループの中で写しを取った回数（ブロック × グループ）。</summary>
        internal int LastNestedCaptureCount { get; private set; }
        /// <summary>今残しているグループの写しの数（全部のブロック）。</summary>
        internal int NestedCopyCount
        {
            get { int n = 0; foreach (var b in blocks.Values) n += b.Groups.Count; foreach (var b in cpuBlocks.Values) n += b.Groups.Count; return n; }
        }
    }
}
