using System;
using System.Collections.Generic;

namespace asset_tool
{
    public static class PartSplitter
    {
        public class Part
        {
            public int X;
            public int Y;
            public RgbaImage Image = null!;
        }

        // Finds connected blobs of non-transparent pixels (8-connectivity) and
        // crops each one out, masking away any other blob's pixels that fall
        // inside the same bounding box.
        public static List<Part> Split(RgbaImage source, byte alphaThreshold = 10, int minPixelCount = 4)
        {
            int w = source.Width;
            int h = source.Height;
            var px = source.Pixels;

            var visited = new bool[w * h];
            var results = new List<Part>();
            var stack = new Stack<int>();
            var pixels = new List<int>();

            for (int start = 0; start < w * h; start++)
            {
                if (visited[start] || px[start * 4 + 3] < alphaThreshold)
                    continue;

                pixels.Clear();
                int minX = start % w, maxX = minX, minY = start / w, maxY = minY;

                visited[start] = true;
                stack.Push(start);

                while (stack.Count > 0)
                {
                    int idx = stack.Pop();
                    pixels.Add(idx);
                    int cx = idx % w, cy = idx / w;
                    if (cx < minX) minX = cx;
                    if (cx > maxX) maxX = cx;
                    if (cy < minY) minY = cy;
                    if (cy > maxY) maxY = cy;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = cy + dy;
                        if (ny < 0 || ny >= h) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx;
                            if ((dx == 0 && dy == 0) || nx < 0 || nx >= w) continue;
                            int nidx = ny * w + nx;
                            if (visited[nidx] || px[nidx * 4 + 3] < alphaThreshold) continue;
                            visited[nidx] = true;
                            stack.Push(nidx);
                        }
                    }
                }

                if (pixels.Count < minPixelCount) continue;

                int pw = maxX - minX + 1;
                int ph = maxY - minY + 1;
                var cropped = new RgbaImage(pw, ph);

                foreach (var idx in pixels)
                {
                    int x = idx % w - minX, y = idx / w - minY;
                    Buffer.BlockCopy(px, idx * 4, cropped.Pixels, (y * pw + x) * 4, 4);
                }

                results.Add(new Part { X = minX, Y = minY, Image = cropped });
            }

            results.Sort((a, b) => a.Y != b.Y ? a.Y - b.Y : a.X - b.X);
            return results;
        }
    }
}
