using System;
using System.Collections.Generic;
using System.Linq;

namespace asset_tool
{
    // Optional, color-based cleanup kept from the first version. The default
    // leak removal is now AtlasOverlapResolver, which works from the atlas
    // rectangles and can't damage a correct part; this is only a fallback for
    // when the PNG doesn't actually match its atlas (re-packed custom skins,
    // a different game version, ...), so the rectangles can't be trusted.
    //
    // RemoveBorderBackground: a leak tucked into a corner of the crop shows up
    // as an opaque corner pixel, so opaque corner colors make good background
    // samples. We flood-fill inward from the border, removing only pixels that
    // match one of those samples (within tolerance) - this stops naturally at
    // the actual part's own (differently colored) pixels.
    //
    // Only OPAQUE corners are sampled. A transparent corner means nothing leaked
    // there; sampling it anyway made "transparent black" a background color and
    // erased every dark part touching the border (note shadows, dark outlines,
    // black hair - measured: 100% of the pixels gone).
    //
    // Safety net: bail out if the removal would leave essentially nothing
    // behind (protects a solid-colored part whose own color fills the box and
    // matches its corners). But when a big chunk of a DIFFERENT color survives -
    // the real part is still there after erasing a neighbor that dominated the
    // crop - we go ahead and remove, even if that neighbor was most of the box.
    public static class BackgroundCutout
    {
        // Bail only if less than this fraction of the visible pixels would remain.
        private const double MinRemainingFraction = 0.08;

        // A corner must be at least this opaque to count as a leak sample.
        private const byte CornerAlphaThreshold = 128;

        private static readonly int[] NeighborDx = { -1, 1, 0, 0 };
        private static readonly int[] NeighborDy = { 0, 0, -1, 1 };

        public static int RemoveBorderBackground(RgbaImage image, int tolerance = 100)
        {
            int w = image.Width, h = image.Height;
            var px = image.Pixels;

            var refColors = new List<(int r, int g, int b)>();
            foreach (var (x, y) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) })
            {
                int i = (y * w + x) * 4;
                if (px[i + 3] >= CornerAlphaThreshold)
                    refColors.Add((px[i], px[i + 1], px[i + 2]));
            }
            if (refColors.Count == 0) return 0; // every corner is see-through: nothing leaked in

            long thresholdSq = ThresholdSquared(tolerance);

            bool MatchesBackground(int i)
            {
                foreach (var (r, g, b) in refColors)
                {
                    int dr = px[i] - r, dg = px[i + 1] - g, db = px[i + 2] - b;
                    if (dr * dr + dg * dg + db * db <= thresholdSq) return true;
                }
                return false;
            }

            var visited = new bool[w * h];
            var toRemove = new List<int>();
            var queue = new Queue<int>();

            void Visit(int x, int y)
            {
                int idx = y * w + x;
                if (visited[idx]) return;
                visited[idx] = true;
                if (px[idx * 4 + 3] == 0 || !MatchesBackground(idx * 4)) return;
                toRemove.Add(idx);
                queue.Enqueue(idx);
            }

            for (int x = 0; x < w; x++) { Visit(x, 0); Visit(x, h - 1); }
            for (int y = 0; y < h; y++) { Visit(0, y); Visit(w - 1, y); }

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int cx = idx % w, cy = idx / w;
                if (cx > 0) Visit(cx - 1, cy);
                if (cx < w - 1) Visit(cx + 1, cy);
                if (cy > 0) Visit(cx, cy - 1);
                if (cy < h - 1) Visit(cx, cy + 1);
            }

            // Count ALL visible pixels, not just the ones the flood happened to
            // visit - otherwise unreached transparent space counts as "opaque"
            // and the safety net almost never triggers.
            int visibleTotal = image.CountVisiblePixels();
            int remaining = visibleTotal - toRemove.Count;
            if (visibleTotal == 0 || remaining < visibleTotal * MinRemainingFraction)
                return 0; // would erase essentially the whole part; leave it untouched

            foreach (var idx in toRemove)
                Array.Clear(px, idx * 4, 4);
            return toRemove.Count;
        }

        // Catches a leak that's fully enclosed by the part itself (like an eye
        // poking through inside a hair crop) - something RemoveBorderBackground
        // can never reach since it only grows from the border. Groups the
        // visible pixels into color-connected islands (chaining
        // neighbor-to-neighbor, so gradients/shading stay in one island), then
        // keeps the largest island AND every island of comparable size, erasing
        // only the ones much smaller than the biggest.
        //
        // Keeping comparable-sized islands is what lets a legit two-piece part
        // survive - e.g. an "eyes" region with a left and a right eye.
        //
        // Caution: islands are split by COLOR, not just by transparency, so any
        // small, sharply contrasting detail inside a part is its own island and
        // gets erased even though it touches the main shape - a pupil inside an
        // eye, an eye highlight, a skull mark on a note body (measured: a 6x6
        // dark pupil inside a skin-colored part was removed completely). Only
        // use it on parts you know contain a stray enclosed leak.
        public static int KeepDominantIslands(RgbaImage image, int tolerance = 60, double keepFraction = 0.2)
        {
            int w = image.Width, h = image.Height;
            var px = image.Pixels;
            long thresholdSq = ThresholdSquared(tolerance);

            bool ColorsClose(int a, int b)
            {
                int dr = px[a] - px[b], dg = px[a + 1] - px[b + 1], db = px[a + 2] - px[b + 2];
                return dr * dr + dg * dg + db * db <= thresholdSq;
            }

            var visited = new bool[w * h];
            var islands = new List<List<int>>();
            var queue = new Queue<int>();

            for (int start = 0; start < w * h; start++)
            {
                if (visited[start]) continue;
                visited[start] = true;
                if (px[start * 4 + 3] == 0) continue;

                var island = new List<int> { start };
                queue.Enqueue(start);

                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    int cx = idx % w, cy = idx / w;

                    for (int n = 0; n < 4; n++)
                    {
                        int nx = cx + NeighborDx[n], ny = cy + NeighborDy[n];
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                        int nidx = ny * w + nx;
                        if (visited[nidx]) continue;
                        if (px[nidx * 4 + 3] == 0) { visited[nidx] = true; continue; }
                        if (!ColorsClose(nidx * 4, idx * 4)) continue;
                        visited[nidx] = true;
                        island.Add(nidx);
                        queue.Enqueue(nidx);
                    }
                }

                islands.Add(island);
            }

            if (islands.Count <= 1) return 0; // nothing to separate

            int largestSize = islands.Max(i => i.Count);
            double keepThreshold = largestSize * keepFraction;
            int removed = 0;

            foreach (var island in islands)
            {
                if (island.Count >= keepThreshold) continue; // comparable in size, keep it
                foreach (var idx in island)
                    Array.Clear(px, idx * 4, 4);
                removed += island.Count;
            }
            return removed;
        }

        // Tolerance is a per-channel distance (0-255); clamping also prevents the
        // int overflow a huge typed value used to cause.
        private static long ThresholdSquared(int tolerance)
        {
            long t = Math.Clamp(tolerance, 0, 255);
            return t * t * 3;
        }
    }
}
