using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Yozolab.YoluPainter.Core.MeshMaps
{
    /// <summary>低ポリのメッシュの塊ごとの手動 ID 色。指紋は UV・スロット・レンダラーと塊の対応を含み、形だけの変更は許す。
    /// 値は 0xRRGGBB。色の重複は同じ色で選ぶために許す。元のアセットには書かない。</summary>
    public sealed class IdColorAssignments : IEquatable<IdColorAssignments>
    {
        public const int MaxParts = 4096;
        public static readonly IdColorAssignments Empty = new IdColorAssignments("", new Dictionary<int, int>());
        public string Binding { get; }
        public IReadOnlyDictionary<int, int> Colors { get; }
        public string Key { get; }
        public IdColorAssignments(string binding, IReadOnlyDictionary<int, int> colors)
        {
            if (colors == null) throw new ArgumentNullException(nameof(colors));
            if (colors.Count > MaxParts) throw new ArgumentException("Too many manual ID colours.", nameof(colors));
            if (colors.Count == 0) binding = "";
            if (binding == null || (colors.Count > 0 && (binding.Length != 64 || binding.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))))
                throw new ArgumentException("Manual ID colours need a model binding hash.", nameof(binding));
            var copy = new SortedDictionary<int, int>();
            foreach (var item in colors)
            {
                if (item.Key < 0 || item.Key >= MeshBakeInput.MaxTriangles || item.Value < 0 || item.Value > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(colors));
                copy.Add(item.Key, item.Value);
            }
            Binding = binding; Colors = new ReadOnlyDictionary<int, int>(copy);
            Key = colors.Count == 0 ? "" : Hash(binding + ";" + string.Join(";", copy.Select(x => x.Key.ToString(System.Globalization.CultureInfo.InvariantCulture) + "=" + x.Value.ToString("X6"))));
        }
        public IdColorAssignments WithColor(string binding, int part, int? rgb)
        {
            if (Colors.Count > 0 && binding != Binding) throw new ArgumentException("Manual ID colours belong to another model. Reset them before assigning this model.");
            var copy = new Dictionary<int, int>(Colors);
            if (rgb.HasValue) copy[part] = rgb.Value; else copy.Remove(part);
            return new IdColorAssignments(binding, copy);
        }
        public bool Equals(IdColorAssignments other) => other != null && Key == other.Key;
        public override bool Equals(object obj) => obj is IdColorAssignments other && Equals(other);
        public override int GetHashCode() => Key.GetHashCode();
        internal static string Hash(string text)
        {
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")));
        }
    }

    /// <summary>手動色の対象になる低ポリの塊と、対応の指紋（同じ入力の間は呼び手が保持する）。</summary>
    public sealed class IdPartIndex
    {
        public MeshBakeInput Input { get; }
        public IReadOnlyList<int> Parts { get; }
        public int Count { get; }
        public string Binding { get; }
        public IdPartIndex(MeshBakeInput input)
        {
            Input = input ?? throw new ArgumentNullException(nameof(input));
            var parts = MeshRegions.MeshParts(input.Corners, input.Slots, out int count); Count = count;
            Parts = Array.AsReadOnly(parts);
            using (var sha = SHA256.Create())
            {
                var head = Encoding.ASCII.GetBytes(input.TopologyHash); sha.TransformBlock(head, 0, head.Length, null, 0);
                var bytes = new byte[parts.Length * 4]; Buffer.BlockCopy(parts, 0, bytes, 0, bytes.Length);
                sha.TransformFinalBlock(bytes, 0, bytes.Length);
                Binding = string.Concat(sha.Hash.Select(b => b.ToString("x2")));
            }
        }
        public void Validate(IdColorAssignments colors)
        {
            if (colors.Colors.Count == 0) return;
            if (colors.Binding != Binding || colors.Colors.Keys.Any(p => p >= Count))
                throw new MeshBakeRefusedException("Manual ID colours belong to another model or mesh partition. Reset them before baking ID.");
        }
    }
}
