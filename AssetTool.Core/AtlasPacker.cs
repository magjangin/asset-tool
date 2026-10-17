using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace asset_tool
{
    public sealed class PackOptions
    {
        // Largest page edge. Parts that don't fit go to a further page.
        public int MaxPageSize { get; init; } = 2048;

        // Empty pixels kept around every part (and along the page edge) so bilinear
        // filtering never bleeds a neighbour in.
        public int Padding { get; init; } = 2;

        // Like Spine's packer: cut transparent borders off and record the original
        // size + offset in the atlas, so the part still renders where it was drawn.
        public bool StripWhitespace { get; init; } = true;

        // Round page sizes up to a power of two (what most GPUs/engines prefer).
        public bool PowerOfTwo { get; init; } = true;
    }

    public sealed class PackInput
    {
        public required string Name { get; init; }   // region name, '/' separated, no extension
        public required RgbaImage Image { get; init; }
    }

    public sealed class PackedRegion
    {
        public required string Name { get; init; }
        public int X, Y;                  // top-left of the packed pixels on the page
        public int Width, Height;         // packed (stripped) size
        public int OrigWidth, OrigHeight; // size before stripping
        public int OffsetX, OffsetY;      // OffsetY counts from the BOTTOM edge (libgdx/Spine)
    }

    public sealed class PackedPage
    {
        public required string FileName { get; init; }
        public required RgbaImage Image { get; init; }
        public List<PackedRegion> Regions { get; } = new();
    }

    // Packs loose part images into atlas pages (MaxRects, best short side fit, no
    // rotation) and writes the libgdx/Spine ".atlas" text format AtlasParser reads.
    public static class AtlasPacker
    {
        private readonly record struct Rect(int X, int Y, int W, int H);

        public static List<PackedPage> Pack(IReadOnlyList<PackInput> inputs, string atlasName, PackOptions options)
        {
            if (inputs.Count == 0) throw new ArgumentException("패킹할 파츠가 없습니다.");
            if (options.Padding < 0) throw new ArgumentOutOfRangeException(nameof(options), "Padding은 0 이상이어야 합니다.");

            var items = new List<Item>();
            foreach (var input in inputs)
            {
                var (trimmed, left, top) = options.StripWhitespace ? Strip(input.Image) : (input.Image, 0, 0);
                items.Add(new Item(input, trimmed, left, top));
            }

            foreach (var item in items)
            {
                int need = Math.Max(item.Image.Width, item.Image.Height) + options.Padding * 2;
                if (need > options.MaxPageSize)
                    throw new InvalidOperationException(
                        $"'{item.Input.Name}'({item.Image.Width}x{item.Image.Height})이 페이지 최대 크기 {options.MaxPageSize}보다 큽니다. maxPageSize를 늘려 주세요.");
            }

            // Biggest first packs tighter.
            var pending = items.OrderByDescending(i => Math.Max(i.Image.Width, i.Image.Height))
                               .ThenByDescending(i => (long)i.Image.Width * i.Image.Height)
                               .ThenBy(i => i.Input.Name, StringComparer.Ordinal)
                               .ToList();

            var pages = new List<PackedPage>();
            while (pending.Count > 0)
            {
                var placed = PackOnePage(pending, options, out var leftover);
                pages.Add(BuildPage(placed, atlasName, pages.Count, options));
                pending = leftover;
            }
            return pages;
        }

        public static string WriteAtlasText(IReadOnlyList<PackedPage> pages)
        {
            var sb = new StringBuilder();
            foreach (var page in pages)
            {
                sb.Append('\n');
                sb.Append(page.FileName).Append('\n');
                sb.Append("size: ").Append(page.Image.Width).Append(',').Append(page.Image.Height).Append('\n');
                sb.Append("format: RGBA8888\n");
                sb.Append("filter: Linear,Linear\n");
                sb.Append("repeat: none\n");
                foreach (var r in page.Regions)
                {
                    sb.Append(r.Name).Append('\n');
                    sb.Append("  rotate: false\n");
                    sb.Append("  xy: ").Append(r.X).Append(", ").Append(r.Y).Append('\n');
                    sb.Append("  size: ").Append(r.Width).Append(", ").Append(r.Height).Append('\n');
                    sb.Append("  orig: ").Append(r.OrigWidth).Append(", ").Append(r.OrigHeight).Append('\n');
                    sb.Append("  offset: ").Append(r.OffsetX).Append(", ").Append(r.OffsetY).Append('\n');
                    sb.Append("  index: -1\n");
                }
            }
            return sb.ToString();
        }

        private sealed record Item(PackInput Input, RgbaImage Image, int Left, int Top)
        {
            public Rect? Placement;
        }

        // Bounding box of non-transparent pixels. A fully transparent image keeps 1x1.
        private static (RgbaImage image, int left, int top) Strip(RgbaImage src)
        {
            int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;
            for (int y = 0; y < src.Height; y++)
                for (int x = 0; x < src.Width; x++)
                    if (src.AlphaAt(x, y) != 0)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }

            if (maxX < 0) return (src.Crop(0, 0, 1, 1), 0, 0);
            if (minX == 0 && minY == 0 && maxX == src.Width - 1 && maxY == src.Height - 1) return (src, 0, 0);
            return (src.Crop(minX, minY, maxX - minX + 1, maxY - minY + 1), minX, minY);
        }

        // Greedy MaxRects (best short side fit) into one maxPageSize square.
        private static List<Item> PackOnePage(List<Item> pending, PackOptions o, out List<Item> leftover)
        {
            int pad = o.Padding;
            var free = new List<Rect> { new(pad, pad, o.MaxPageSize - pad, o.MaxPageSize - pad) };
            var placed = new List<Item>();
            leftover = new List<Item>();

            foreach (var item in pending)
            {
                int w = item.Image.Width + pad, h = item.Image.Height + pad;
                Rect? best = null;
                long bestShort = long.MaxValue, bestLong = long.MaxValue;

                foreach (var f in free)
                {
                    if (w > f.W || h > f.H) continue;
                    long leftW = f.W - w, leftH = f.H - h;
                    long shortSide = Math.Min(leftW, leftH), longSide = Math.Max(leftW, leftH);
                    if (shortSide < bestShort || (shortSide == bestShort && longSide < bestLong))
                    {
                        best = new Rect(f.X, f.Y, w, h);
                        bestShort = shortSide;
                        bestLong = longSide;
                    }
                }

                if (best is null) { leftover.Add(item); continue; }

                item.Placement = new Rect(best.Value.X, best.Value.Y, item.Image.Width, item.Image.Height);
                placed.Add(item);
                SplitFree(free, best.Value);
            }
            return placed;
        }

        private static void SplitFree(List<Rect> free, Rect used)
        {
            var next = new List<Rect>(free.Count + 4);
            foreach (var f in free)
            {
                if (!Intersects(f, used)) { next.Add(f); continue; }
                if (used.X > f.X)
                    next.Add(new Rect(f.X, f.Y, used.X - f.X, f.H));
                if (used.X + used.W < f.X + f.W)
                    next.Add(new Rect(used.X + used.W, f.Y, f.X + f.W - (used.X + used.W), f.H));
                if (used.Y > f.Y)
                    next.Add(new Rect(f.X, f.Y, f.W, used.Y - f.Y));
                if (used.Y + used.H < f.Y + f.H)
                    next.Add(new Rect(f.X, used.Y + used.H, f.W, f.Y + f.H - (used.Y + used.H)));
            }

            // Drop free rectangles fully inside another one.
            free.Clear();
            for (int i = 0; i < next.Count; i++)
            {
                bool contained = false;
                for (int j = 0; j < next.Count && !contained; j++)
                    if (i != j && Contains(next[j], next[i]) && (!Contains(next[i], next[j]) || j < i))
                        contained = true;
                if (!contained) free.Add(next[i]);
            }
        }

        private static bool Intersects(Rect a, Rect b) =>
            a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

        private static bool Contains(Rect outer, Rect inner) =>
            inner.X >= outer.X && inner.Y >= outer.Y &&
            inner.X + inner.W <= outer.X + outer.W && inner.Y + inner.H <= outer.Y + outer.H;

        private static PackedPage BuildPage(List<Item> placed, string atlasName, int pageIndex, PackOptions o)
        {
            int usedW = placed.Max(i => i.Placement!.Value.X + i.Placement.Value.W) + o.Padding;
            int usedH = placed.Max(i => i.Placement!.Value.Y + i.Placement.Value.H) + o.Padding;
            int w = o.PowerOfTwo ? NextPowerOfTwo(usedW) : usedW;
            int h = o.PowerOfTwo ? NextPowerOfTwo(usedH) : usedH;
            w = Math.Min(w, o.MaxPageSize);
            h = Math.Min(h, o.MaxPageSize);

            var image = new RgbaImage(w, h);
            var page = new PackedPage
            {
                FileName = pageIndex == 0 ? $"{atlasName}.png" : $"{atlasName}{pageIndex + 1}.png",
                Image = image
            };

            foreach (var item in placed.OrderBy(i => i.Input.Name, StringComparer.Ordinal))
            {
                var p = item.Placement!.Value;
                for (int row = 0; row < p.H; row++)
                    Buffer.BlockCopy(item.Image.Pixels, row * p.W * 4, image.Pixels, ((p.Y + row) * w + p.X) * 4, p.W * 4);

                int origW = item.Input.Image.Width, origH = item.Input.Image.Height;
                page.Regions.Add(new PackedRegion
                {
                    Name = item.Input.Name,
                    X = p.X, Y = p.Y, Width = p.W, Height = p.H,
                    OrigWidth = origW, OrigHeight = origH,
                    OffsetX = item.Left,
                    OffsetY = origH - item.Top - p.H
                });
            }
            return page;
        }

        private static int NextPowerOfTwo(int v)
        {
            int p = 1;
            while (p < v) p <<= 1;
            return p;
        }
    }
}
