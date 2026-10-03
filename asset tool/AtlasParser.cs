using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace asset_tool
{
    public class AtlasRegion
    {
        public string Name = "";
        public int Index = -1;
        public int RotateDegrees; // 0, 90, 180 or 270 - the packer's applied rotation
        public int X, Y;
        public int Width, Height; // declared (unrotated) size

        // Whitespace stripping: the original image size and where the packed
        // pixels sat inside it (libgdx/Spine convention: offsetY is measured
        // from the BOTTOM edge). 0 x 0 orig means "same as Width/Height".
        public int OrigWidth, OrigHeight;
        public int OffsetX, OffsetY;

        public bool SwapsDimensions => RotateDegrees == 90 || RotateDegrees == 270;

        // Size of the rectangle as it actually sits on the page texture.
        public int PackedWidth => SwapsDimensions ? Height : Width;
        public int PackedHeight => SwapsDimensions ? Width : Height;

        public bool HasStrippedWhitespace =>
            OrigWidth > 0 && OrigHeight > 0 &&
            (OrigWidth != Width || OrigHeight != Height || OffsetX != 0 || OffsetY != 0);

        public string ExportName => Index < 0 ? Name : $"{Name}_{Index}";
    }

    public class AtlasPage
    {
        public string ImageFile = "";
        public int Width, Height; // declared page size, 0 if the atlas doesn't say
        public bool PremultipliedAlpha;
        public List<AtlasRegion> Regions { get; } = new();
    }

    // Parser for the libgdx/Spine ".atlas" text format. Two real-world variants
    // exist and both are handled here:
    //   - legacy/classic:  xy: x,y / size: w,h / orig: w,h / offset: x,y / index: n
    //   - newer/compact:   bounds: x,y,w,h / offsets: offX,offY,origW,origH
    // Line classification follows libgdx's own reader rather than indentation
    // (some exporters indent attribute lines, some don't): a blank line ends a
    // page, the first line of a page is its image file, and after that any
    // line containing ':' is an attribute of the current page/region while a
    // line without one starts a new region. Unknown attributes (custom
    // key/values some exporters add) are therefore ignored instead of being
    // mistaken for region names.
    public static class AtlasParser
    {
        public static List<AtlasPage> Parse(string path) => ParseLines(File.ReadAllLines(path));

        public static List<AtlasPage> ParseLines(IEnumerable<string> lines)
        {
            var pages = new List<AtlasPage>();
            AtlasPage? page = null;
            AtlasRegion? region = null;

            foreach (var raw in lines)
            {
                var line = raw.Trim();

                if (line.Length == 0)
                {
                    page = null;
                    region = null;
                    continue;
                }

                if (page == null)
                {
                    page = new AtlasPage { ImageFile = line };
                    pages.Add(page);
                    region = null;
                    continue;
                }

                int colonIdx = line.IndexOf(':');
                if (colonIdx < 0)
                {
                    region = new AtlasRegion { Name = line };
                    page.Regions.Add(region);
                    continue;
                }

                var key = line[..colonIdx].Trim().ToLowerInvariant();
                var value = line[(colonIdx + 1)..].Trim();

                if (region == null)
                    ApplyPageField(page, key, value);
                else
                    ApplyRegionField(region, key, value);
            }

            return pages;
        }

        private static void ApplyPageField(AtlasPage page, string key, string value)
        {
            switch (key)
            {
                case "size":
                {
                    var sz = ParseInts(value);
                    if (sz.Count >= 2) { page.Width = sz[0]; page.Height = sz[1]; }
                    break;
                }
                case "pma":
                    page.PremultipliedAlpha = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
                // format / filter / repeat / custom keys: not needed for cropping.
            }
        }

        private static void ApplyRegionField(AtlasRegion region, string key, string value)
        {
            switch (key)
            {
                case "rotate":
                    if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
                        region.RotateDegrees = 90;
                    else if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
                        region.RotateDegrees = 0;
                    else if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var deg))
                        region.RotateDegrees = ((deg % 360) + 360) % 360; // normalize e.g. -90 -> 270
                    break;
                case "xy":
                {
                    var xy = ParseInts(value);
                    if (xy.Count >= 2) { region.X = xy[0]; region.Y = xy[1]; }
                    break;
                }
                case "size":
                {
                    var sz = ParseInts(value);
                    if (sz.Count >= 2) { region.Width = sz[0]; region.Height = sz[1]; }
                    break;
                }
                case "bounds":
                {
                    var b = ParseInts(value);
                    if (b.Count >= 4)
                    {
                        region.X = b[0];
                        region.Y = b[1];
                        region.Width = b[2];
                        region.Height = b[3];
                    }
                    break;
                }
                case "orig":
                {
                    var o = ParseInts(value);
                    if (o.Count >= 2) { region.OrigWidth = o[0]; region.OrigHeight = o[1]; }
                    break;
                }
                case "offset":
                {
                    var o = ParseInts(value);
                    if (o.Count >= 2) { region.OffsetX = o[0]; region.OffsetY = o[1]; }
                    break;
                }
                case "offsets":
                {
                    var o = ParseInts(value);
                    if (o.Count >= 4)
                    {
                        region.OffsetX = o[0];
                        region.OffsetY = o[1];
                        region.OrigWidth = o[2];
                        region.OrigHeight = o[3];
                    }
                    break;
                }
                case "index":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                        region.Index = idx;
                    break;
                // split / pad / custom keys: not needed for a crop.
            }
        }

        // Tolerant: a malformed value makes the whole field come back empty (so
        // it's ignored) instead of throwing and taking the file down, or
        // silently shifting the remaining numbers into the wrong slots.
        // Fractional values are rounded.
        private static List<int> ParseInts(string value)
        {
            var result = new List<int>(4);
            foreach (var part in value.Split(','))
            {
                if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return new List<int>();
                result.Add((int)Math.Round(d));
            }
            return result;
        }
    }
}
