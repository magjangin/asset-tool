using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace asset_tool.Mcp;

// Renders Spine skeletons to PNG through the Spine editor's command line, so a whole boss / note
// can be taken as one picture without opening the editor: contact sheet -> pick frames -> render.
[McpServerToolType]
public static class SpineTools
{
    private const string WorkFolder = "_spine";
    private const string FramesFolder = "frames";
    private const string SheetsFolder = "sheets";
    private const string PickedFolder = "picked";
    private const string RecipeFile = "recipe.json";

    [McpServerTool(Name = "spine_contact_sheet", Destructive = false, Idempotent = true)]
    [Description("Render every animation of a Spine 3.8 skeleton (.skel) to PNG frames with the Spine editor CLI (Spine.com, needs the " +
                 "licensed editor) and lay them out as contact sheet pictures, one row per animation, each frame labelled with its time. " +
                 "Look at the sheets, choose frames, then call spine_render_frames with 'animation@time'. " +
                 "Part PNGs come from the matching 파츠 folder (extract_and_split layout) unless partsDir is given. " +
                 "Writes <outputDir>\\sheets\\sheet_NN.png, <outputDir>\\frames\\ (every frame) and <outputDir>\\_spine\\ (editor project). " +
                 "Takes about 20-60 s because the editor starts for each step; the project is reused on later calls.")]
    public static async Task<object> SpineContactSheet(
        [Description("Path to the .skel file.")] string skelPath,
        [Description("Folder for sheets, frames and the editor project (created if missing).")] string outputDir,
        [Description("Folder holding the part PNGs, e.g. <bundle>\\파츠\\0101 (region names such as 'images/body' resolve below it). " +
                     "Default: <bundle>\\파츠\\<skel name> next to the 원본 folder.")] string? partsDir = null,
        [Description("Skeleton scale on import. Default 0.5: Muse Dash part PNGs are half the authored size, so bones and parts only line up at 0.5 " +
                     "(1 leaves the parts scattered).")] double importScale = 0.5,
        [Description("Frames per second to sample. Default 4 (a 1 s animation gives 4 frames). Raise it to see finer steps.")] double fps = 4,
        [Description("Comma-separated animation names to show on the sheets. Default: all. All animations are still exported to frames\\.")] string? animations = null,
        [Description("Most frames shown per animation; longer ones are thinned evenly (first and last are kept). Default 12.")] int maxColumns = 12,
        [Description("Height of one frame cell in pixels. Default 180.")] int cellHeight = 180,
        [Description("Animations per sheet picture. Default 10.")] int animationsPerSheet = 10,
        [Description("Path to Spine.com. Defaults to the SPINE_CLI environment variable, then C:\\Program Files\\Spine\\Spine.com.")] string? spineCliPath = null,
        [Description("Spine editor version to run (-u). Default 3.8.99.")] string spineVersion = "3.8.99",
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(skelPath))
                return new { ok = false, error = $"파일이 없습니다: {skelPath}" };
            if (fps <= 0 || maxColumns < 1 || cellHeight < 32 || animationsPerSheet < 1 || importScale <= 0)
                return new { ok = false, error = "fps, maxColumns, cellHeight, animationsPerSheet, importScale 값이 올바르지 않습니다." };
            var parts = ResolveParts(skelPath, partsDir, out var partsError);
            if (parts is null) return new { ok = false, error = partsError };

            var settings = new SpineSettings { CliPath = spineCliPath, Version = spineVersion, ImportScale = importScale };
            var work = Path.Combine(outputDir, WorkFolder);
            var framesDir = Path.Combine(outputDir, FramesFolder);
            var sheetsDir = Path.Combine(outputDir, SheetsFolder);

            return await Task.Run<object>(() =>
            {
                var project = SpineRenderer.Prepare(skelPath, parts, work, settings, cancellationToken);
                var frames = SpineRenderer.ExportAll(project, framesDir, fps, maxBounds: true, scalePercent: 50, cancellationToken);

                var all = frames.GroupBy(f => f.Animation).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
                var wanted = SplitNames(animations);
                var shown = wanted.Count == 0 ? all : all.Where(g => wanted.Contains(g.Key)).ToList();
                if (shown.Count == 0)
                    return new { ok = false, error = $"해당 애니메이션이 없습니다. 있는 것: {string.Join(", ", all.Select(g => g.Key))}" };

                // The export canvas covers every animation, so crop each row to what that animation paints.
                var lists = shown.ToDictionary(g => g.Key, g => Thin(g.OrderBy(f => f.Index).ToList(), maxColumns));
                var crops = shown.ToDictionary(g => g.Key, g => UnionBounds(lists[g.Key].Select(f => f.Path)));
                double aspect = Math.Clamp(crops.Values.Where(c => c.Height > 0).Select(c => (double)c.Width / c.Height).DefaultIfEmpty(0.7).Max(), 0.5, 2.0);
                int cellWidth = (int)Math.Round(cellHeight * aspect);

                var rows = new List<SheetRow>();
                foreach (var g in shown)
                {
                    var chosen = lists[g.Key];
                    var crop = crops[g.Key];
                    int total = g.Count();
                    // Sheet text is ASCII: Skia's default font has no Hangul glyphs.
                    string thinned = chosen.Count < total ? $" (showing {chosen.Count})" : "";
                    rows.Add(new SheetRow(
                        g.Key,
                        $"{total} frames {total / fps:0.##}s {crop.Width}x{crop.Height}{thinned}",
                        chosen.Select(f => new SheetCell($"{f.Index / fps:0.00}s #{f.Index}", f.Path, crop)).ToList()));
                }

                Directory.CreateDirectory(sheetsDir);
                foreach (var old in Directory.EnumerateFiles(sheetsDir, "sheet_*.png")) File.Delete(old);
                var sheets = new List<string>();
                for (int i = 0; i * animationsPerSheet < rows.Count; i++)
                {
                    var png = ContactSheet.Render(rows.Skip(i * animationsPerSheet).Take(animationsPerSheet).ToList(), cellWidth, cellHeight);
                    var path = Path.Combine(sheetsDir, $"sheet_{i + 1:D2}.png");
                    File.WriteAllBytes(path, png);
                    sheets.Add(Path.GetFullPath(path));
                }

                return new
                {
                    ok = true,
                    skeleton = project.Name,
                    partsDir = project.PartsDir,
                    importScale,
                    sampleFps = fps,
                    animationCount = all.Count,
                    animations = all.Select(g => new
                    {
                        name = g.Key,
                        frames = g.Count(),
                        seconds = Math.Round(g.Count() / fps, 2),
                        shownOnSheets = shown.Contains(g)
                    }),
                    sheets,
                    framesDir = Path.GetFullPath(framesDir),
                    projectPath = project.ProjectPath,
                    warnings = project.Warnings.Take(20),
                    next = "시트를 보고 고른 프레임을 spine_render_frames의 frames에 'animation@time'(초)으로 넘기세요. 예: standby@0.50"
                };
            }, cancellationToken);
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

    [McpServerTool(Name = "spine_render_frames", Destructive = false, Idempotent = true)]
    [Description("Render chosen frames of a Spine 3.8 skeleton to final PNGs and save the choice as a recipe. " +
                 "Give frames as 'animation@time' (seconds from animation start, e.g. 'standby@0.50'), optionally '=name' for the file name " +
                 "('standby@0.50=boss_cover'). Output: <outputDir>\\picked\\<name>.png and <outputDir>\\recipe.json. " +
                 "Pass recipePath to render a saved recipe again (e.g. at another exportScalePercent); any other argument you give overrides the recipe. " +
                 "Frames are cut on a 1/renderFps time grid. All frames of one animation share a canvas size (maxBounds), like the editor's export.")]
    public static async Task<object> SpineRenderFrames(
        [Description("Folder for the rendered PNGs (picked\\), recipe.json and the editor project (_spine\\). Reuse the contact-sheet folder to skip the setup.")] string outputDir,
        [Description("Path to the .skel file. Not needed when recipePath is given.")] string? skelPath = null,
        [Description("Frames to render: 'animation@time' or 'animation@time=name' (time in seconds).")] string[]? frames = null,
        [Description("Saved recipe.json to start from.")] string? recipePath = null,
        [Description("Folder holding the part PNGs. Default: from the recipe, else <bundle>\\파츠\\<skel name>.")] string? partsDir = null,
        [Description("Skeleton scale on import. Default 0.5 (see spine_contact_sheet).")] double? importScale = null,
        [Description("Output size in percent of the imported (0.5x) skeleton. Default 100.")] int? exportScalePercent = null,
        [Description("Time grid for picking frames, frames per second. Default 60.")] double? renderFps = null,
        [Description("Use the largest bounds over the animation as the canvas for every frame. Default true.")] bool? maxBounds = null,
        [Description("Path to Spine.com. Defaults to the SPINE_CLI environment variable, then C:\\Program Files\\Spine\\Spine.com.")] string? spineCliPath = null,
        [Description("Spine editor version to run (-u). Default: from the recipe, else 3.8.99.")] string? spineVersion = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recipe = new SpineRecipe();
            if (!string.IsNullOrWhiteSpace(recipePath))
            {
                if (!File.Exists(recipePath))
                    return new { ok = false, error = $"레시피가 없습니다: {recipePath}" };
                recipe = SpineRecipe.Load(recipePath);
            }
            if (!string.IsNullOrWhiteSpace(skelPath)) recipe.Skel = skelPath;
            if (!string.IsNullOrWhiteSpace(partsDir)) recipe.PartsDir = partsDir;
            if (importScale is { } sc) recipe.ImportScale = sc;
            if (exportScalePercent is { } ep) recipe.ExportScalePercent = ep;
            if (renderFps is { } rf) recipe.RenderFps = rf;
            if (maxBounds is { } mb) recipe.MaxBounds = mb;
            if (!string.IsNullOrWhiteSpace(spineVersion)) recipe.SpineVersion = spineVersion;

            if (frames is { Length: > 0 })
            {
                recipe.Frames = new List<SpineRecipeFrame>();
                foreach (var spec in frames)
                {
                    var f = ParsePick(spec, out var pickError);
                    if (f is null) return new { ok = false, error = pickError };
                    recipe.Frames.Add(f);
                }
            }

            if (string.IsNullOrWhiteSpace(recipe.Skel) || !File.Exists(recipe.Skel))
                return new { ok = false, error = $"스켈레톤 파일이 없습니다: {recipe.Skel}" };
            if (recipe.Frames.Count == 0)
                return new { ok = false, error = "frames가 비었습니다. 'animation@time' 형식으로 하나 이상 주세요." };
            if (recipe.RenderFps <= 0 || recipe.ExportScalePercent <= 0 || recipe.ImportScale <= 0)
                return new { ok = false, error = "renderFps, exportScalePercent, importScale는 0보다 커야 합니다." };

            if (string.IsNullOrWhiteSpace(recipe.PartsDir))
            {
                var guessed = ResolveParts(recipe.Skel, null, out var partsError);
                if (guessed is null) return new { ok = false, error = partsError };
                recipe.PartsDir = guessed;
            }
            if (!Directory.Exists(recipe.PartsDir))
                return new { ok = false, error = $"파츠 폴더가 없습니다: {recipe.PartsDir}" };

            var names = UniqueNames(recipe.Frames, Path.GetFileNameWithoutExtension(recipe.Skel));
            var settings = new SpineSettings { CliPath = spineCliPath, Version = recipe.SpineVersion, ImportScale = recipe.ImportScale };
            var work = Path.Combine(outputDir, WorkFolder);
            var pickedDir = Path.Combine(outputDir, PickedFolder);

            return await Task.Run<object>(() =>
            {
                var project = SpineRenderer.Prepare(recipe.Skel, recipe.PartsDir, work, settings, cancellationToken);
                Directory.CreateDirectory(pickedDir);

                var results = new List<object>();
                int animIndex = 0;
                foreach (var group in recipe.Frames.Select((f, i) => (f, name: names[i])).GroupBy(x => x.f.Animation))
                {
                    var tmp = Path.Combine(work, "render", $"a{animIndex++}");
                    if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
                    var rendered = SpineRenderer.ExportAnimation(project, group.Key, tmp, "f", recipe.RenderFps,
                                                                 recipe.MaxBounds, recipe.ExportScalePercent, cancellationToken);
                    foreach (var (f, name) in group)
                    {
                        int wanted = (int)Math.Round(f.Time * recipe.RenderFps);
                        int index = Math.Clamp(wanted, 0, rendered[^1].Index);
                        var source = rendered.First(r => r.Index == index).Path;
                        var dest = Path.Combine(pickedDir, name + ".png");
                        File.Copy(source, dest, overwrite: true);
                        var size = ContactSheet.ImageSize(dest);
                        results.Add(new
                        {
                            name,
                            animation = f.Animation,
                            requestedTime = f.Time,
                            renderedTime = Math.Round(index / recipe.RenderFps, 3),
                            clamped = index != wanted,
                            size = $"{size.Width}x{size.Height}",
                            path = Path.GetFullPath(dest)
                        });
                    }
                    Directory.Delete(tmp, recursive: true);
                }

                for (int i = 0; i < recipe.Frames.Count; i++) recipe.Frames[i].Name = names[i];
                var recipeOut = Path.Combine(outputDir, RecipeFile);
                recipe.Save(recipeOut);

                return new
                {
                    ok = true,
                    count = results.Count,
                    frames = results,
                    recipePath = Path.GetFullPath(recipeOut),
                    warnings = project.Warnings.Take(20)
                };
            }, cancellationToken);
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

    private static string? ResolveParts(string skelPath, string? partsDir, out string error)
    {
        error = "";
        if (!string.IsNullOrWhiteSpace(partsDir))
        {
            if (Directory.Exists(partsDir)) return Path.GetFullPath(partsDir);
            error = $"파츠 폴더가 없습니다: {partsDir}";
            return null;
        }
        var guessed = SpineRenderer.GuessPartsDir(skelPath);
        if (guessed is not null) return guessed;
        error = "파츠 폴더를 찾지 못했습니다. <번들>\\파츠\\<스켈 이름> 구조가 아니면 partsDir를 지정하세요.";
        return null;
    }

    private static SkiaSharp.SKRectI UnionBounds(IEnumerable<string> paths)
    {
        SkiaSharp.SKRectI? union = null;
        foreach (var p in paths)
        {
            if (ContactSheet.OpaqueBounds(p) is not { } b) continue;
            union = union is { } u ? SkiaSharp.SKRectI.Union(u, b) : b;
        }
        if (union is { } result) return result;
        var size = ContactSheet.ImageSize(paths.First());
        return new SkiaSharp.SKRectI(0, 0, Math.Max(1, size.Width), Math.Max(1, size.Height));
    }

    private static HashSet<string> SplitNames(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.Ordinal);

    // Evenly spaced frames, always including the first and last.
    private static List<SpineFrame> Thin(List<SpineFrame> list, int max)
    {
        if (list.Count <= max) return list;
        if (max == 1) return new List<SpineFrame> { list[0] };
        return Enumerable.Range(0, max)
                         .Select(i => list[(int)Math.Round(i * (list.Count - 1.0) / (max - 1))])
                         .DistinctBy(f => f.Index).ToList();
    }

    // "standby@0.5" or "standby@0.5=cover"
    private static SpineRecipeFrame? ParsePick(string spec, out string error)
    {
        error = "";
        string? name = null;
        var s = spec.Trim();
        int eq = s.LastIndexOf('=');
        if (eq >= 0) { name = s[(eq + 1)..].Trim(); s = s[..eq]; }
        int at = s.LastIndexOf('@');
        if (at <= 0 || !double.TryParse(s[(at + 1)..].Trim().TrimEnd('s'), NumberStyles.Float, CultureInfo.InvariantCulture, out var time) || time < 0)
        {
            error = $"프레임 형식이 올바르지 않습니다: '{spec}'. 'animation@time' 또는 'animation@time=name' (time은 0 이상의 초)";
            return null;
        }
        return new SpineRecipeFrame { Animation = s[..at].Trim(), Time = time, Name = name ?? "" };
    }

    private static List<string> UniqueNames(List<SpineRecipeFrame> frames, string skeleton)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var f in frames)
        {
            var raw = string.IsNullOrWhiteSpace(f.Name)
                ? $"{skeleton}_{f.Animation}_{f.Time.ToString("0.00", CultureInfo.InvariantCulture)}s"
                : f.Name;
            var clean = Regex.Replace(raw, "[" + Regex.Escape(new string(Path.GetInvalidFileNameChars())) + "]", "_").Trim('.', ' ');
            if (clean.Length == 0) clean = "frame";
            var unique = clean;
            for (int n = 2; !used.Add(unique); n++) unique = $"{clean}_{n}";
            result.Add(unique);
        }
        return result;
    }
}
