using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace asset_tool
{
    public class AtlasRegion
    {
        public string Name = "";
        public int Index = -1;
        public int RotateDegrees; // 0, 90, 180 or 270 - the packer's applied rotation
        public int X, Y;
        public int Width, Height; // declared (unrotated) size

        public bool SwapsDimensions => RotateDegrees == 90 || RotateDegrees == 270;
        public string ExportName => Index < 0 ? Name : $"{Name}_{Index}";
    }

    public class AtlasPage
    {
        public string ImageFile = "";
        public List<AtlasRegion> Regions { get; } = new();
    }

    // Parser for the libgdx/Spine ".atlas" text format. Two real-world variants
    // exist and both are handled here:
    //   - legacy/classic:  xy: x,y / size: w,h / orig: w,h / offset: x,y / index: n
    //   - newer/compact:   bounds: x,y,w,h / offsets: offX,offY,origW,origH
    // Some exporters indent per-region attribute lines, some don't, so
    // classification is done by recognized key name + parse state (are we
    // inside a page's metadata block, or inside a region's attribute block),
    // never by whitespace.
    public static class AtlasParser
    {
        private static readonly HashSet<string> PageKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "size", "format", "filter", "repeat", "pma"
        };

        private static readonly HashSet<string> RegionKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "rotate", "xy", "size", "orig", "offset", "index", "bounds", "offsets", "split", "pad"
        };

        public static List<AtlasPage> Parse(string path)
        {
            var pages = new List<AtlasPage>();
            AtlasPage? page = null;
            AtlasRegion? region = null;

            foreach (var raw in File.ReadAllLines(path))
            {
                var trimmed = raw.Trim();

                if (trimmed.Length == 0)
                {
                    page = null;
                    region = null;
                    continue;
                }

                var colonIdx = trimmed.IndexOf(':');
                var key = colonIdx >= 0 ? trimmed[..colonIdx].Trim().ToLowerInvariant() : null;
                var value = colonIdx >= 0 ? trimmed[(colonIdx + 1)..].Trim() : null;

                if (page == null)
                {
                    // First line of a page block is always the image file name.
                    page = new AtlasPage { ImageFile = trimmed };
                    pages.Add(page);
                    region = null;
                    continue;
                }

                if (region == null)
                {
                    if (key != null && PageKeys.Contains(key))
                        continue; // page-level metadata, not needed for cropping

                    region = new AtlasRegion { Name = trimmed };
                    page.Regions.Add(region);
                    continue;
                }

                if (key != null && RegionKeys.Contains(key))
                {
                    ApplyRegionField(region, key, value ?? "");
                    continue;
                }

                // Not a recognized attribute line while inside a region -> it's the next region name.
                region = new AtlasRegion { Name = trimmed };
                page.Regions.Add(region);
            }

            return pages;
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
                    else if (int.TryParse(value, out var deg))
                        region.RotateDegrees = ((deg % 360) + 360) % 360; // normalize e.g. -90 -> 270
                    break;
                case "xy":
                {
                    var xy = SplitInts(value);
                    if (xy.Length >= 2) { region.X = xy[0]; region.Y = xy[1]; }
                    break;
                }
                case "size":
                {
                    var sz = SplitInts(value);
                    if (sz.Length >= 2) { region.Width = sz[0]; region.Height = sz[1]; }
                    break;
                }
                case "bounds":
                {
                    var b = SplitInts(value);
                    if (b.Length >= 4)
                    {
                        region.X = b[0];
                        region.Y = b[1];
                        region.Width = b[2];
                        region.Height = b[3];
                    }
                    break;
                }
                case "index":
                    if (int.TryParse(value, out var idx)) region.Index = idx;
                    break;
                // orig / offset / offsets / split / pad: not needed for a tight crop.
            }
        }

        private static int[] SplitInts(string value) =>
            value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)
                 .Select(int.Parse).ToArray();
    }
}
