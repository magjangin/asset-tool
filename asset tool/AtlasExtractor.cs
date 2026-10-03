using System.Collections.Generic;

namespace asset_tool
{
    public sealed class ExtractOptions
    {
        // Atlas-geometry leak removal (see AtlasOverlapResolver). Safe default:
        // it never touches regions whose rectangle doesn't overlap another one.
        public bool RemoveOverlapLeaks { get; init; } = true;

        // Legacy color heuristics (see BackgroundCutout) - off by default.
        public bool ColorCutout { get; init; }
        public bool RemoveSmallIslands { get; init; }
        public int Tolerance { get; init; } = 100;

        // Pad stripped whitespace back using orig/offset, so the PNG has the
        // same size the artist exported (what Spine expects when re-packing).
        public bool RestoreOriginalSize { get; init; }
    }

    public sealed class ExtractedPart
    {
        public required AtlasRegion Region { get; init; }
        public required RgbaImage Image { get; init; }
        public int OverlapPixelsRemoved { get; init; }
        public int ColorPixelsRemoved { get; init; }
    }

    public sealed class PageExtraction
    {
        public List<ExtractedPart> Parts { get; } = new();
        public List<string> SkippedRegions { get; } = new(); // out of the page bounds / empty
        public bool PageHasOverlaps { get; init; }
    }

    public static class AtlasExtractor
    {
        public static PageExtraction ExtractPage(RgbaImage page, AtlasPage atlasPage, ExtractOptions options)
        {
            var valid = new List<AtlasRegion>();
            var skipped = new List<string>();

            foreach (var region in atlasPage.Regions)
            {
                int pw = region.PackedWidth, ph = region.PackedHeight;
                if (pw <= 0 || ph <= 0 || region.X < 0 || region.Y < 0 ||
                    region.X + pw > page.Width || region.Y + ph > page.Height)
                    skipped.Add(region.ExportName);
                else
                    valid.Add(region);
            }

            var resolver = options.RemoveOverlapLeaks ? new AtlasOverlapResolver(page, valid) : null;
            var result = new PageExtraction { PageHasOverlaps = resolver?.HasOverlaps ?? false };
            result.SkippedRegions.AddRange(skipped);

            foreach (var region in valid)
            {
                var crop = page.Crop(region.X, region.Y, region.PackedWidth, region.PackedHeight);

                // Leak removal works in page coordinates, so it runs before rotating.
                int overlapRemoved = resolver?.RemoveForeignPixels(region, crop) ?? 0;

                var image = region.RotateDegrees != 0 ? crop.RotateClockwise(region.RotateDegrees) : crop;

                int colorRemoved = 0;
                if (options.ColorCutout)
                {
                    colorRemoved += BackgroundCutout.RemoveBorderBackground(image, options.Tolerance);
                    if (options.RemoveSmallIslands)
                        colorRemoved += BackgroundCutout.KeepDominantIslands(image, options.Tolerance);
                }

                if (options.RestoreOriginalSize && region.HasStrippedWhitespace)
                {
                    // offsetY counts from the bottom edge of the original image.
                    int top = region.OrigHeight - region.OffsetY - image.Height;
                    image = image.PlacedOnCanvas(region.OrigWidth, region.OrigHeight, region.OffsetX, top);
                }

                result.Parts.Add(new ExtractedPart
                {
                    Region = region,
                    Image = image,
                    OverlapPixelsRemoved = overlapRemoved,
                    ColorPixelsRemoved = colorRemoved
                });
            }

            return result;
        }
    }
}
