using System;
using System.Collections.Generic;

namespace asset_tool
{
    public readonly record struct PixelRect(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
        public long Area => (long)Width * Height;

        public bool Contains(int px, int py) => px >= X && px < Right && py >= Y && py < Bottom;

        public bool Contains(PixelRect other) =>
            other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

        public bool Intersects(PixelRect other) =>
            X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

        public static PixelRect Packed(AtlasRegion r) => new(r.X, r.Y, r.PackedWidth, r.PackedHeight);
    }

    // Decides which region every visible pixel of an atlas page belongs to,
    // using only the atlas rectangles - never colors.
    //
    // Why: polygon-packed atlases (the Muse Dash character atlases are packed
    // this way) let region rectangles overlap, so a plain rectangle crop picks
    // up bits of whatever neighbor was tucked into its empty corners - or, for
    // a small part packed inside a big hair's rectangle, the whole small part.
    // The old color-based cutout guessed at those leaks from corner colors and
    // wiped out legitimate parts in the process (every note "shadow" - a dark
    // ellipse with transparent corners - vanished completely). Rectangles of a
    // normally packed atlas (most note/scene atlases) simply don't overlap, and
    // then nothing is touched at all - which is right, because nothing can leak.
    //
    // How, when rectangles do overlap. Packed images never share pixels, but
    // anti-aliasing can bridge two neighbors with a few nearly invisible pixels
    // (measured on the character atlases: 4-16 pixels, all below alpha 32), so
    // ownership is settled on SOLID pixels first and faint ones follow:
    //  A. Solid pixels covered by exactly one rectangle certainly belong to that
    //     region. From those seeds each region grows through touching
    //     (8-connected) solid pixels, staying inside its own rectangle - a part
    //     reaching into an overlap zone is claimed by the region it's attached to.
    //  B. Solid pixels nobody reached form islands lying entirely inside overlap
    //     zones (e.g. a collar packed inside a hair's rectangle). Each island goes
    //     to the smallest rectangle that fully contains it - the tighter fit is
    //     the likelier owner, and a region's pixels can't lie outside its rect.
    //  C. Faint pixels (anti-aliased edges) join whichever owned pixel they touch,
    //     again only inside that owner's rectangle.
    //  D. Faint pixels touching nothing owned are islands, handled like B.
    // Regions sharing an identical rectangle (packer aliases, e.g. two "smoke"
    // entries pointing at one image) share ownership.
    public sealed class AtlasOverlapResolver
    {
        private const byte SolidAlpha = 32;
        private const int Unassigned = -1;
        private const int Collecting = int.MaxValue;

        private readonly int _pageWidth;
        private readonly int[]? _owner; // per page pixel: owning group, -1 = transparent/uncovered
        private readonly List<PixelRect> _groupRects = new();
        private readonly Dictionary<AtlasRegion, int> _groupOf = new();

        public bool HasOverlaps { get; }

        // `regions` must already be validated to lie inside the page.
        public AtlasOverlapResolver(RgbaImage page, IReadOnlyList<AtlasRegion> regions)
        {
            _pageWidth = page.Width;

            var groupByRect = new Dictionary<PixelRect, int>();
            foreach (var region in regions)
            {
                var rect = PixelRect.Packed(region);
                if (!groupByRect.TryGetValue(rect, out var group))
                {
                    group = _groupRects.Count;
                    _groupRects.Add(rect);
                    groupByRect[rect] = group;
                }
                _groupOf[region] = group;
            }

            for (int i = 0; i < _groupRects.Count && !HasOverlaps; i++)
                for (int j = i + 1; j < _groupRects.Count; j++)
                    if (_groupRects[i].Intersects(_groupRects[j])) { HasOverlaps = true; break; }

            if (!HasOverlaps) return; // fast path: nothing can leak

            var px = page.Pixels;
            var cover = new byte[page.Width * page.Height];
            var owner = new int[page.Width * page.Height];

            // Coverage count, remembering the (last) covering group.
            for (int g = 0; g < _groupRects.Count; g++)
            {
                var r = _groupRects[g];
                for (int y = r.Y; y < r.Bottom; y++)
                {
                    int row = y * page.Width;
                    for (int x = r.X; x < r.Right; x++)
                    {
                        int idx = row + x;
                        if (cover[idx] < byte.MaxValue) cover[idx]++;
                        owner[idx] = g;
                    }
                }
            }

            // A. Seeds: solid pixels covered by exactly one rectangle; then grow.
            var queue = new Queue<int>();
            for (int idx = 0; idx < owner.Length; idx++)
            {
                if (cover[idx] == 1 && px[idx * 4 + 3] >= SolidAlpha)
                    queue.Enqueue(idx);
                else
                    owner[idx] = Unassigned;
            }
            Grow(queue, owner, px, page.Width, SolidAlpha);

            // B. Solid islands nobody reached.
            AssignIslands(owner, cover, px, page.Width, page.Height, SolidAlpha);

            // C. Faint pixels join the owned pixels they touch.
            for (int idx = 0; idx < owner.Length; idx++)
                if (owner[idx] >= 0) queue.Enqueue(idx);
            Grow(queue, owner, px, page.Width, 1);

            // D. Faint islands.
            AssignIslands(owner, cover, px, page.Width, page.Height, 1);

            _owner = owner;
        }

        // Multi-source BFS: every queued pixel spreads its owner to unassigned
        // 8-neighbors with at least `minAlpha`, inside the owner's rectangle.
        private void Grow(Queue<int> queue, int[] owner, byte[] px, int w, byte minAlpha)
        {
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int g = owner[idx];
                var r = _groupRects[g];
                int cx = idx % w, cy = idx / w;

                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = cy + dy;
                    if (ny < r.Y || ny >= r.Bottom) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx;
                        if ((dx == 0 && dy == 0) || nx < r.X || nx >= r.Right) continue;
                        int nidx = ny * w + nx;
                        if (owner[nidx] != Unassigned || px[nidx * 4 + 3] < minAlpha) continue;
                        owner[nidx] = g;
                        queue.Enqueue(nidx);
                    }
                }
            }
        }

        // Groups still-unassigned covered pixels (alpha >= minAlpha) into
        // 8-connected islands and gives each to its tightest containing rectangle.
        private void AssignIslands(int[] owner, byte[] cover, byte[] px, int w, int h, byte minAlpha)
        {
            var queue = new Queue<int>();
            var island = new List<int>();

            bool Candidate(int idx) => owner[idx] == Unassigned && cover[idx] != 0 && px[idx * 4 + 3] >= minAlpha;

            for (int start = 0; start < owner.Length; start++)
            {
                if (!Candidate(start)) continue;

                island.Clear();
                owner[start] = Collecting;
                queue.Enqueue(start);
                int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    island.Add(idx);
                    int cx = idx % w, cy = idx / w;
                    minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx);
                    minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = cy + dy;
                        if (ny < 0 || ny >= h) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx;
                            if ((dx == 0 && dy == 0) || nx < 0 || nx >= w) continue;
                            int nidx = ny * w + nx;
                            if (!Candidate(nidx)) continue;
                            owner[nidx] = Collecting;
                            queue.Enqueue(nidx);
                        }
                    }
                }

                int winner = PickIslandOwner(new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1), island, w);
                foreach (var idx in island)
                    owner[idx] = winner;
            }
        }

        private int PickIslandOwner(PixelRect bounds, List<int> island, int pageWidth)
        {
            int best = -1;
            for (int g = 0; g < _groupRects.Count; g++)
            {
                if (_groupRects[g].Contains(bounds) &&
                    (best < 0 || _groupRects[g].Area < _groupRects[best].Area))
                    best = g;
            }
            if (best >= 0) return best;

            // Island straddles several rectangles without fitting in any one:
            // fall back to whichever rectangle covers most of its pixels.
            int bestCount = -1;
            for (int g = 0; g < _groupRects.Count; g++)
            {
                if (!_groupRects[g].Intersects(bounds)) continue;
                int count = 0;
                foreach (var idx in island)
                    if (_groupRects[g].Contains(idx % pageWidth, idx / pageWidth)) count++;
                if (count > bestCount) { bestCount = count; best = g; }
            }
            return best;
        }

        // `crop` is the region's packed (still unrotated) rectangle cut from the
        // page. Makes every visible pixel belonging to another region
        // transparent; returns how many were removed.
        public int RemoveForeignPixels(AtlasRegion region, RgbaImage crop)
        {
            if (_owner is null || !_groupOf.TryGetValue(region, out var group)) return 0;

            var rect = _groupRects[group];
            int removed = 0;
            for (int y = 0; y < crop.Height; y++)
            {
                int pageRow = (rect.Y + y) * _pageWidth + rect.X;
                for (int x = 0; x < crop.Width; x++)
                {
                    if (crop.AlphaAt(x, y) == 0) continue;
                    if (_owner[pageRow + x] == group) continue;
                    crop.ClearPixel(x, y);
                    removed++;
                }
            }
            return removed;
        }
    }
}
