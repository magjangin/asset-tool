using System.Collections.Generic;
using SkiaSharp;

namespace asset_tool
{
    public static class PartSplitter
    {
        public class Part
        {
            public int X;
            public int Y;
            public int Width;
            public int Height;
            public SKBitmap Image = null!;
        }

        // Finds connected blobs of non-transparent pixels (8-connectivity) and
        // crops each one out, masking away any other blob's pixels that fall
        // inside the same bounding box.
        public static List<Part> Split(SKBitmap source, byte alphaThreshold = 10, int minPixelCount = 4)
        {
            int w = source.Width;
            int h = source.Height;

            var alpha = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    alpha[y * w + x] = source.GetPixel(x, y).Alpha;

            var visited = new bool[w * h];
            var results = new List<Part>();
            var stack = new Stack<(int x, int y)>();

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    if (visited[idx] || alpha[idx] < alphaThreshold)
                        continue;

                    var pixels = new List<(int x, int y)>();
                    int minX = x, maxX = x, minY = y, maxY = y;

                    visited[idx] = true;
                    stack.Push((x, y));

                    while (stack.Count > 0)
                    {
                        var (cx, cy) = stack.Pop();
                        pixels.Add((cx, cy));
                        if (cx < minX) minX = cx;
                        if (cx > maxX) maxX = cx;
                        if (cy < minY) minY = cy;
                        if (cy > maxY) maxY = cy;

                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = cx + dx, ny = cy + dy;
                                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                                int nidx = ny * w + nx;
                                if (visited[nidx] || alpha[nidx] < alphaThreshold) continue;
                                visited[nidx] = true;
                                stack.Push((nx, ny));
                            }
                        }
                    }

                    if (pixels.Count < minPixelCount) continue;

                    int pw = maxX - minX + 1;
                    int ph = maxY - minY + 1;
                    var cropped = new SKBitmap(pw, ph, source.ColorType, source.AlphaType);

                    foreach (var (px, py) in pixels)
                        cropped.SetPixel(px - minX, py - minY, source.GetPixel(px, py));

                    results.Add(new Part { X = minX, Y = minY, Width = pw, Height = ph, Image = cropped });
                }
            }

            results.Sort((a, b) => a.Y != b.Y ? a.Y - b.Y : a.X - b.X);
            return results;
        }
    }
}
