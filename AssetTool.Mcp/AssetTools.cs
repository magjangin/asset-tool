using System.ComponentModel;
using ModelContextProtocol.Server;

namespace asset_tool.Mcp;

// MCP tools wrapping the splitter core. Every tool returns plain data (never
// throws for bad input) so the calling model can read the error and retry.
[McpServerToolType]
public static class AssetTools
{
    [McpServerTool(Name = "list_atlas_regions", ReadOnly = true)]
    [Description("Parse a Spine/libgdx .atlas (or .atlas.txt) file and list its pages and regions " +
                 "without writing anything. Use it to inspect an atlas before splitting.")]
    public static object ListAtlasRegions(
        [Description("Path to the .atlas or .atlas.txt file.")] string atlasPath,
        [Description("Max regions listed per page (default 100); counts are always complete.")] int maxRegionsPerPage = 100)
    {
        try
        {
            if (!File.Exists(atlasPath))
                return new { ok = false, error = $"파일이 없습니다: {atlasPath}" };

            var pages = AtlasParser.Parse(atlasPath);
            return new
            {
                ok = true,
                pageCount = pages.Count,
                pages = pages.Select(p => new
                {
                    imageFile = p.ImageFile,
                    width = p.Width,
                    height = p.Height,
                    regionCount = p.Regions.Count,
                    regions = p.Regions.Take(Math.Max(0, maxRegionsPerPage)).Select(r => new
                    {
                        name = r.ExportName,
                        x = r.X, y = r.Y,
                        width = r.Width, height = r.Height,
                        rotateDegrees = r.RotateDegrees,
                        strippedWhitespace = r.HasStrippedWhitespace
                    })
                })
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    [McpServerTool(Name = "split_atlas_folder", Destructive = false, Idempotent = true)]
    [Description("Split every Spine .atlas under a folder into per-part PNGs. Atlas page PNGs are matched " +
                 "automatically (e.g. TextAsset\\x paired with Texture2D\\x from an AssetStudio export). " +
                 "Output goes to <outputDir>\\<atlas folder relative to input>\\<atlas name>\\<region>.png and a " +
                 "_split_log.txt. A failing atlas is logged and skipped. Existing files in outputDir may be overwritten.")]
    public static async Task<object> SplitAtlasFolder(
        [Description("Folder to scan recursively for .atlas / .atlas.txt and PNG files.")] string inputDir,
        [Description("Output folder. Must differ from inputDir; may be inside it (output is never re-processed).")] string outputDir,
        [Description("File naming: 'keep_folders' (0103/air/fx -> 0103\\air\\fx.png, for Spine re-packing), " +
                     "'flatten' (0103_air_fx.png) or 'leaf_only' (fx.png). Default keep_folders.")] string naming = "keep_folders",
        [Description("Restore whitespace trimmed at packing time using orig/offset, so parts have original size.")] bool restoreOriginalSize = false,
        [Description("Remove pixels of neighbouring parts that leak into overlapping polygon-packed regions. Default true.")] bool removeOverlapLeaks = true,
        [Description("Copy PNGs that no atlas refers to (e.g. long-note textures) as-is. Default true.")] bool copyUnusedPngs = true,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(inputDir))
            return new { ok = false, error = $"입력 폴더가 없습니다: {inputDir}" };

        NamingMode mode;
        switch (naming.Trim().ToLowerInvariant())
        {
            case "keep_folders": mode = NamingMode.KeepFolders; break;
            case "flatten": mode = NamingMode.Flatten; break;
            case "leaf_only": mode = NamingMode.LeafOnly; break;
            default:
                return new { ok = false, error = "naming은 keep_folders, flatten, leaf_only 중 하나여야 합니다." };
        }

        var options = new BatchOptions
        {
            Naming = mode,
            CopyUnusedPngs = copyUnusedPngs,
            Extract = new ExtractOptions
            {
                RemoveOverlapLeaks = removeOverlapLeaks,
                RestoreOriginalSize = restoreOriginalSize
            }
        };

        try
        {
            var summary = await Task.Run(
                () => BatchProcessor.Run(inputDir, outputDir, options, progress: null, cancellationToken),
                cancellationToken);

            // Warnings and failures only; the full log is on disk.
            var problems = File.ReadAllLines(summary.LogPath)
                               .Where(l => l.Contains('✗') || l.TrimStart().StartsWith("!"))
                               .Take(50)
                               .ToArray();
            return new
            {
                ok = summary.Failures == 0,
                atlases = summary.Atlases,
                pages = summary.Pages,
                parts = summary.Parts,
                overlapCleanedRegions = summary.CleanedRegions,
                copiedPngs = summary.CopiedPngs,
                warnings = summary.Warnings,
                failures = summary.Failures,
                outputDir = Path.GetFullPath(outputDir),
                logPath = summary.LogPath,
                problems
            };
        }
        catch (OperationCanceledException)
        {
            return new { ok = false, error = "취소되었습니다. 일부 파일이 이미 출력됐을 수 있습니다." };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    [McpServerTool(Name = "split_png_by_alpha", Destructive = false, Idempotent = true)]
    [Description("Split one PNG into parts by finding connected blobs of non-transparent pixels. " +
                 "Use only when there is no .atlas for the image. Writes part_XX_x<X>_y<Y>.png files into outputDir.")]
    public static object SplitPngByAlpha(
        [Description("Path to the PNG file.")] string pngPath,
        [Description("Folder to write the part PNGs into (created if missing).")] string outputDir,
        [Description("Pixels with alpha below this count as transparent (0-255). Default 10.")] int alphaThreshold = 10,
        [Description("Blobs smaller than this many pixels are ignored. Default 4.")] int minPixelCount = 4)
    {
        try
        {
            if (!File.Exists(pngPath))
                return new { ok = false, error = $"파일이 없습니다: {pngPath}" };
            if (alphaThreshold is < 0 or > 255)
                return new { ok = false, error = "alphaThreshold는 0~255여야 합니다." };

            var source = RgbaImage.Load(pngPath);
            var parts = PartSplitter.Split(source, (byte)alphaThreshold, minPixelCount);

            Directory.CreateDirectory(outputDir);
            var files = new List<string>();
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                var path = Path.Combine(outputDir, $"part_{i + 1:D2}_x{p.X}_y{p.Y}.png");
                File.WriteAllBytes(path, p.Image.EncodePng());
                files.Add(path);
            }
            return new { ok = true, partCount = parts.Count, outputDir = Path.GetFullPath(outputDir), files };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }
}
