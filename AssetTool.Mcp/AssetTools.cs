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
                 "_split_log.txt. A failing atlas is logged and skipped. Existing files in outputDir may be overwritten. " +
                 "If one page name matches several different textures (same name, different picture), every one is split: " +
                 "the first into the normal folder, the others into <name>_v2, <name>_v3 - check the log for which atlases this affects.")]
    public static async Task<object> SplitAtlasFolder(
        [Description("Folder to scan recursively for .atlas / .atlas.txt and PNG files.")] string inputDir,
        [Description("Output folder. Must differ from inputDir; may be inside it (output is never re-processed).")] string outputDir,
        [Description("File naming: 'keep_folders' (0103/air/fx -> 0103\\air\\fx.png, for Spine re-packing), " +
                     "'flatten' (0103_air_fx.png) or 'leaf_only' (fx.png). Default keep_folders.")] string naming = "keep_folders",
        [Description("Restore whitespace trimmed at packing time using orig/offset, so parts have original size.")] bool restoreOriginalSize = false,
        [Description("Remove pixels of neighbouring parts that leak into overlapping polygon-packed regions. Default true.")] bool removeOverlapLeaks = true,
        [Description("Copy PNGs that no atlas refers to (e.g. long-note textures) as-is. Default true.")] bool copyUnusedPngs = true,
        [Description("Drop the atlas's source folder from the output path: <outputDir>/<atlas name>/<region>.png, " +
                     "the layout Spine's image-folder import expects. Same-named atlases get _2, _3. Stray PNGs go to _unused_pngs. Default false.")] bool flattenFolders = false,
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
            FlattenFolders = flattenFolders,
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

// Wrappers around the AssetStudioMod CLI (https://github.com/aelurum/AssetStudio)
// so one request can go from Unity bundles to split parts.
[McpServerToolType]
public static class BundleTools
{
    private const string CliEnvVar = "ASSETSTUDIO_CLI";

    [McpServerTool(Name = "extract_unity_bundles", Destructive = false, Idempotent = true)]
    [Description("Extract assets (default: Texture2D PNGs and TextAssets such as .atlas/.skel) from Unity asset bundle " +
                 "files using the AssetStudioMod CLI. Accepts one bundle file or a folder of bundles. " +
                 "With groupBy='fileName' each bundle gets its own subfolder, so same-named assets from different bundles never collide.")]
    public static async Task<object> ExtractUnityBundles(
        [Description("A bundle file, or a folder containing bundles.")] string inputPath,
        [Description("Folder to extract into (created if missing).")] string outputDir,
        [Description("Comma-separated asset types for the CLI's -t option. Default 'tex2d,textAsset'.")] string assetTypes = "tex2d,textAsset",
        [Description("CLI -g option: none, type, container, containerFull, fileName, sceneHierarchy. Default 'fileName'.")] string groupBy = "fileName",
        [Description("Optional CLI --filter-by-name text, e.g. a character id.")] string? filterByName = null,
        [Description("Path to AssetStudioModCLI.exe. Defaults to the ASSETSTUDIO_CLI environment variable.")] string? cliPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
                return new { ok = false, error = $"입력이 없습니다: {inputPath}" };

            var cli = cliPath ?? Environment.GetEnvironmentVariable(CliEnvVar);
            if (string.IsNullOrWhiteSpace(cli) || !File.Exists(cli))
                return new { ok = false, error = $"AssetStudioModCLI.exe를 찾을 수 없습니다. cliPath를 주거나 환경변수 {CliEnvVar}를 설정하세요." };

            Directory.CreateDirectory(outputDir);
            var psi = new System.Diagnostics.ProcessStartInfo(cli)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in new[] { Path.GetFullPath(inputPath), "-t", assetTypes, "-g", groupBy,
                                      "-o", Path.GetFullPath(outputDir), "--log-level", "warning" })
                psi.ArgumentList.Add(a);
            if (!string.IsNullOrWhiteSpace(filterByName))
            {
                psi.ArgumentList.Add("--filter-by-name");
                psi.ArgumentList.Add(filterByName);
            }

            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = proc.StandardError.ReadToEndAsync(cancellationToken);
            try { await proc.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw; }

            var outText = await stdout;
            var errText = await stderr;
            var files = Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories)
                                 .GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
                                 .ToDictionary(g => g.Key.Length == 0 ? "(none)" : g.Key, g => g.Count());
            // The CLI prints \r-separated progress; keep only the last meaningful chunk.
            static string Tail(string s) { s = s.Trim(); return s.Length <= 600 ? s : s[^600..]; }
            return new
            {
                ok = proc.ExitCode == 0,
                exitCode = proc.ExitCode,
                outputDir = Path.GetFullPath(outputDir),
                fileCountsByExtension = files,
                stdoutTail = Tail(outText),
                stderrTail = Tail(errText)
            };
        }
        catch (OperationCanceledException)
        {
            return new { ok = false, error = "취소되었습니다." };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    private static readonly string[] BundleExtensions = { ".bundle", ".unity3d", ".assetbundle", ".ab" };
    private const string PartsFolder = "파츠";
    private const string SpritesFolder = "스프라이트";
    private const string RawFolder = "원본";

    [McpServerTool(Name = "extract_and_split", Destructive = false, Idempotent = true)]
    [Description("One-shot pipeline for Unity bundles: extract Texture2D + TextAsset (.atlas/.skel) and Sprites, then split every " +
                 "Spine atlas into per-part PNGs. layout='by_bundle' (default) writes one folder per bundle: " +
                 "<workDir>/<bundle>/파츠 (<atlas name>/<region>.png), /스프라이트 (loose sprites: long notes, hit points, backgrounds, skins...) " +
                 "and /원본 (mirrors 파츠: one folder per atlas holding its .atlas, page PNGs and the .skel files that use it; " +
                 "스켈_짝맞춤.csv lists how skeletons were matched). layout='classic' extracts the whole input into <workDir>/extracted and splits into <workDir>/parts.")]
    public static async Task<object> ExtractAndSplit(
        [Description("A bundle file, or a folder containing bundles.")] string inputPath,
        [Description("Working folder; per-bundle folders (or 'extracted' and 'parts' for layout='classic') are created inside it.")] string workDir,
        [Description("Optional CLI --filter-by-name text.")] string? filterByName = null,
        [Description("Naming for parts: keep_folders (default), flatten or leaf_only.")] string naming = "keep_folders",
        [Description("Path to AssetStudioModCLI.exe. Defaults to the ASSETSTUDIO_CLI environment variable.")] string? cliPath = null,
        [Description("classic layout only: lay parts out as <atlas name>/<region>.png without bundle/CAB folders. Default true.")] bool flattenFolders = true,
        [Description("'by_bundle' (default) or 'classic'.")] string layout = "by_bundle",
        [Description("by_bundle layout only: also extract Sprite assets into 스프라이트. Default true.")] bool includeSprites = true,
        CancellationToken cancellationToken = default)
    {
        if (layout is "classic")
        {
            var extractedDir = Path.Combine(workDir, "extracted");
            var partsDir = Path.Combine(workDir, "parts");

            var extraction = await ExtractUnityBundles(inputPath, extractedDir, filterByName: filterByName,
                                                       cliPath: cliPath, cancellationToken: cancellationToken);
            if (!IsOk(extraction))
                return new { ok = false, stage = "extract", extraction };

            var split = await AssetTools.SplitAtlasFolder(extractedDir, partsDir, naming, flattenFolders: flattenFolders, cancellationToken: cancellationToken);
            return new { ok = IsOk(split), stage = "split", extraction, split };
        }
        if (layout is not "by_bundle")
            return new { ok = false, error = "layout은 by_bundle 또는 classic이어야 합니다." };

        if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
            return new { ok = false, error = $"입력이 없습니다: {inputPath}" };

        var bundles = File.Exists(inputPath)
            ? new[] { Path.GetFullPath(inputPath) }
            : Directory.EnumerateFiles(inputPath, "*", SearchOption.AllDirectories)
                       .Where(f => BundleExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                       .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (bundles.Length == 0)
            return new { ok = false, error = $"번들 파일({string.Join(", ", BundleExtensions)})을 찾지 못했습니다: {inputPath}" };

        var results = new List<object>();
        var usedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool allOk = true;

        foreach (var bundle in bundles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // "gamescene_01_assets_all_<32 hex>.bundle" -> "gamescene_01"
            var label = System.Text.RegularExpressions.Regex.Replace(
                Path.GetFileNameWithoutExtension(bundle), "_assets_all_[0-9a-fA-F]{32}$", "");
            var unique = label;
            for (int n = 2; !usedLabels.Add(unique); n++) unique = $"{label}_{n}";

            var bundleDir = Path.Combine(workDir, unique);
            var rawDir = Path.Combine(bundleDir, RawFolder);
            var partsDir = Path.Combine(bundleDir, PartsFolder);
            var spritesDir = Path.Combine(bundleDir, SpritesFolder);

            var raw = await ExtractUnityBundles(bundle, rawDir, "tex2d,textAsset", "none", filterByName, cliPath, cancellationToken);
            if (!IsOk(raw))
            {
                allOk = false;
                results.Add(new { bundle = Path.GetFileName(bundle), folder = unique, ok = false, stage = "extract", detail = raw });
                continue;
            }

            object? split = null;
            if (Directory.EnumerateFiles(rawDir, "*.atlas*", SearchOption.AllDirectories).Any())
                split = await AssetTools.SplitAtlasFolder(rawDir, partsDir, naming, flattenFolders: true,
                                                          copyUnusedPngs: false, cancellationToken: cancellationToken);

            // Mirror the split output: one raw folder per atlas folder, skeletons placed inside.
            RawOrganizeResult? organized = null;
            if (split is not null && Directory.Exists(partsDir))
            {
                organized = RawOrganizer.Organize(rawDir, partsDir);
                RawOrganizer.WritePlacementCsv(Path.Combine(bundleDir, RawOrganizer.PlacementCsvName), organized.Skeletons);
            }

            int sprites = 0;
            if (includeSprites)
            {
                var sp = await ExtractUnityBundles(bundle, spritesDir, "sprite", "none", filterByName, cliPath, cancellationToken);
                if (!IsOk(sp)) allOk = false;
                sprites = CountFiles(sp, ".png");
            }

            if (split is not null && !IsOk(split)) allOk = false;
            results.Add(new
            {
                bundle = Path.GetFileName(bundle),
                folder = unique,
                ok = split is null || IsOk(split),
                atlases = Prop<int>(split, "atlases"),
                parts = Prop<int>(split, "parts"),
                sprites,
                rawFiles = Directory.EnumerateFiles(rawDir, "*", SearchOption.AllDirectories).Count(),
                rawFolders = organized?.Folders ?? 0,
                looseRawFiles = organized?.LooseFiles ?? 0,
                skeletonsWithoutAtlas = organized?.Skeletons.Count(k => k.Folder.Length == 0) ?? 0,
                warnings = Prop<int>(split, "warnings"),
                failures = Prop<int>(split, "failures"),
                splitLog = Prop<string>(split, "logPath")
            });
        }

        return new { ok = allOk, workDir = Path.GetFullPath(workDir), bundleCount = bundles.Length, bundles = results };
    }

    private static int CountFiles(object result, string extension) =>
        Prop<Dictionary<string, int>>(result, "fileCountsByExtension") is { } d && d.TryGetValue(extension, out var n) ? n : 0;

    private static T? Prop<T>(object? result, string name) =>
        result?.GetType().GetProperty(name)?.GetValue(result) is T v ? v : default;

    // Tool results are anonymous objects; read their "ok" flag reflectively.
    private static bool IsOk(object result) =>
        result.GetType().GetProperty("ok")?.GetValue(result) is true;
}
