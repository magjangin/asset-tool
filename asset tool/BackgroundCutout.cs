using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace asset_tool
{
    // Cleans up atlas-rectangle crops whose bounding box leaks a bit of a
    // neighboring part or packing background at the edges. A tight bounding
    // box around an irregular shape (like hair) almost always leaves its own
    // four corners uncovered, so the corner colors make good background
    // samples. We flood-fill inward from the border, removing only pixels
    // that match one of those corner colors (within tolerance) - this stops
    // naturally as soon as it reaches the actual part's own (differently
    // colored) pixels, even if they touch the middle of an edge.
    //
    // Safety net: bail out only if the removal would leave essentially nothing
    // behind. That protects a solid-colored head/torso whose own color fills the
    // box and matches its corners (removing it all would gut a correct part).
    // But when a big chunk of a DIFFERENT color survives the flood - i.e. the
    // real part is still there after erasing a neighbor that dominated the crop -
    // we go ahead and remove, even if that neighbor was most of the box. (This is
    // the "hair_1 came out as a different part" case: the neighbor filled the
    // crop, so an over-eager "removed too much -> bail" net used to keep it.)
    //
    // Limitation: this can't remove a leak that's fully enclosed by the part
    // itself (not connected to the border) - see KeepDominantIslands for that case.
    public static class BackgroundCutout
    {
        // Bail only if less than this fraction of the opaque pixels would remain.
        private const double MinRemainingFraction = 0.08;

        public static void RemoveBorderBackground(SKBitmap bitmap, int tolerance = 100)
        {
            int w = bitmap.Width, h = bitmap.Height;
            if (w == 0 || h == 0) return;

            var refColors = new[]
            {
                bitmap.GetPixel(0, 0),
                bitmap.GetPixel(w - 1, 0),
                bitmap.GetPixel(0, h - 1),
                bitmap.GetPixel(w - 1, h - 1)
            };

            int thresholdSq = tolerance * tolerance * 3;

            bool MatchesBackground(SKColor c)
            {
                foreach (var r in refColors)
                {
                    int dr = c.Red - r.Red, dg = c.Green - r.Green, db = c.Blue - r.Blue;
                    if (dr * dr + dg * dg + db * db <= thresholdSq) return true;
                }
                return false;
            }

            var visited = new bool[w, h];
            var toRemove = new List<(int x, int y)>();
            var queue = new Queue<(int x, int y)>();
            int alreadyTransparent = 0;

            void Seed(int x, int y)
            {
                if (x < 0 || x >= w || y < 0 || y >= h || visited[x, y]) return;
                visited[x, y] = true;
                var c = bitmap.GetPixel(x, y);
                if (c.Alpha == 0) { alreadyTransparent++; return; }
                if (MatchesBackground(c)) { toRemove.Add((x, y)); queue.Enqueue((x, y)); }
            }

            for (int x = 0; x < w; x++) { Seed(x, 0); Seed(x, h - 1); }
            for (int y = 0; y < h; y++) { Seed(0, y); Seed(w - 1, y); }

            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();

                foreach (var (nx, ny) in new[] { (cx - 1, cy), (cx + 1, cy), (cx, cy - 1), (cx, cy + 1) })
                {
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h || visited[nx, ny]) continue;
                    visited[nx, ny] = true;
                    var c = bitmap.GetPixel(nx, ny);
                    if (c.Alpha == 0) { alreadyTransparent++; continue; }
                    if (MatchesBackground(c)) { toRemove.Add((nx, ny)); queue.Enqueue((nx, ny)); }
                }
            }

            int opaqueTotal = w * h - alreadyTransparent;
            int remaining = opaqueTotal - toRemove.Count;
            if (opaqueTotal == 0 || remaining < opaqueTotal * MinRemainingFraction)
                return; // would erase essentially the whole part; leave it untouched

            foreach (var (x, y) in toRemove)
                bitmap.SetPixel(x, y, SKColors.Transparent);
        }

        // Catches a leak that's fully enclosed by the part itself (like an eye
        // poking through inside a hair crop) - something RemoveBorderBackground
        // can never reach since it only ever grows from the border. Groups the
        // remaining opaque pixels into color-connected islands (chaining
        // neighbor-to-neighbor, so gradients/shading stay in one island), then
        // keeps the largest island AND every island of comparable size, erasing
        // only the ones that are much smaller than the biggest.
        //
        // Keeping comparable-sized islands is what makes a legit two-piece part
        // survive - e.g. an "eyes" region holding a left and a right eye: the two
        // eyes are separated by transparent space (two islands) but are roughly
        // the same size, so both are kept. A stray eye buried inside a big hair
        // crop is far smaller than the hair, so it still gets removed.
        //
        // Trade-off: a genuinely small, disconnected detail of the real part (a
        // tiny clip/highlight not touching the main shape) that falls below
        // keepFraction would also be removed. Toggle the feature off if that
        // ever happens.
        public static void KeepDominantIslands(SKBitmap bitmap, int tolerance = 60, double keepFraction = 0.2)
        {
            int w = bitmap.Width, h = bitmap.Height;
            if (w == 0 || h == 0) return;

            int thresholdSq = tolerance * tolerance * 3;
            bool ColorsClose(SKColor a, SKColor b)
            {
                int dr = a.Red - b.Red, dg = a.Green - b.Green, db = a.Blue - b.Blue;
                return dr * dr + dg * dg + db * db <= thresholdSq;
            }

            var visited = new bool[w, h];
            var islands = new List<List<(int x, int y)>>();

            for (int sy = 0; sy < h; sy++)
            {
                for (int sx = 0; sx < w; sx++)
                {
                    if (visited[sx, sy]) continue;
                    visited[sx, sy] = true;
                    if (bitmap.GetPixel(sx, sy).Alpha == 0) continue;

                    var island = new List<(int x, int y)> { (sx, sy) };
                    var queue = new Queue<(int x, int y)>();
                    queue.Enqueue((sx, sy));

                    while (queue.Count > 0)
                    {
                        var (cx, cy) = queue.Dequeue();
                        var baseColor = bitmap.GetPixel(cx, cy);

                        foreach (var (nx, ny) in new[] { (cx - 1, cy), (cx + 1, cy), (cx, cy - 1), (cx, cy + 1) })
                        {
                            if (nx < 0 || nx >= w || ny < 0 || ny >= h || visited[nx, ny]) continue;
                            var nc = bitmap.GetPixel(nx, ny);
                            if (nc.Alpha == 0) { visited[nx, ny] = true; continue; }
                            if (!ColorsClose(nc, baseColor)) continue;
                            visited[nx, ny] = true;
                            island.Add((nx, ny));
                            queue.Enqueue((nx, ny));
                        }
                    }

                    islands.Add(island);
                }
            }

            if (islands.Count <= 1) return; // nothing to separate

            int largestSize = islands.Max(i => i.Count);
            double keepThreshold = largestSize * keepFraction;

            foreach (var island in islands)
            {
                if (island.Count >= keepThreshold) continue; // comparable in size, keep it
                foreach (var (x, y) in island)
                    bitmap.SetPixel(x, y, SKColors.Transparent);
            }
        }
    }
}
