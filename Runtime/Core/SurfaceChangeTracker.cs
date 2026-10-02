using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>
    /// SparseTileSurface の書き換えを、面ごと・タイルごとの通し番号で数える。合成器が GPU に置いた写し（アップロードしたタイル、
    /// 下の合成結果）がまだ正しいかを、画素を読まずに確かめるためのもの。
    /// 最初に <see cref="For"/> を呼んだ時点から数え始める（面の TileChanged 通知に自分を足す。文書の変更記録はそのまま呼ばれる）。
    /// それより前の書き換えは数えないので、使う側は写しを作る前に For を呼ぶ。通知が後から付け替えられていたら付け直し、
    /// <see cref="Generation"/> を進める（その間の書き換えは分からないので、使う側はすべて変わったとみなす）。
    /// </summary>
    public sealed class SurfaceChangeTracker
    {
        static readonly ConditionalWeakTable<SparseTileSurface, SurfaceChangeTracker> trackers = new ConditionalWeakTable<SparseTileSurface, SurfaceChangeTracker>();
        static long nextId;

        readonly Dictionary<TileCoord, long> tileRevisions = new Dictionary<TileCoord, long>();
        Action<TileCoord> hook;

        /// <summary>面ごとに一意な番号（同じ面には同じ番号）。</summary>
        public long Id { get; private set; }
        /// <summary>通知の付け直しの回数。変わったら、それ以前の番号との比較は意味を持たない。</summary>
        public long Generation { get; private set; }
        /// <summary>この面のどこかのタイルが変わるたびに増える。</summary>
        public long Revision { get; private set; }

        SurfaceChangeTracker(long id) { Id = id; }

        /// <summary>面の数え手を返す（無ければ作って通知に足す）。</summary>
        public static SurfaceChangeTracker For(SparseTileSurface surface)
        {
            if (surface == null) throw new ArgumentNullException(nameof(surface));
            if (!trackers.TryGetValue(surface, out var tracker))
            {
                tracker = new SurfaceChangeTracker(++nextId);
                tracker.Attach(surface);
                trackers.Add(surface, tracker);
            }
            else if (!ReferenceEquals(surface.TileChanged, tracker.hook))
            {
                // 誰かが通知を付け替えた。その間の書き換えは数えられていないので、世代を進めて付け直す
                tracker.Generation++;
                tracker.Attach(surface);
            }
            return tracker;
        }

        void Attach(SparseTileSurface surface)
        {
            var inner = surface.TileChanged;
            hook = coord => { Revision++; tileRevisions[coord] = Revision; inner?.Invoke(coord); };
            surface.TileChanged = hook;
        }

        /// <summary>タイルが最後に変わったときの <see cref="Revision"/>（数え始めてから変わっていなければ 0）。</summary>
        public long TileRevision(TileCoord coord) { return tileRevisions.TryGetValue(coord, out var r) ? r : 0; }

        /// <summary>タイルの範囲 [x0, x1) × [y0, y1) のうち、最後に変わったタイルの <see cref="Revision"/>。</summary>
        public long MaxTileRevision(int x0, int y0, int x1, int y1)
        {
            long max = 0;
            if (tileRevisions.Count == 0) return 0;
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
                if (tileRevisions.TryGetValue(new TileCoord(x, y), out var r) && r > max) max = r;
            return max;
        }
    }
}
