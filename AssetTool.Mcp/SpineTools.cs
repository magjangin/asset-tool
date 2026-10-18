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
    // About 50 skeletons overflowed an 8 GB editor heap; 18 per project stays far below it.
    private const int MaxSkeletonsPerProject = 18;

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

    [McpServerTool(Name = "spine_render_batch", Destructive = false, Idempotent = true)]
    [Description("Render one frame (default: standby at 0.0 s) of many Spine 3.8 skeletons at once to transparent-background PNGs, cropped to what is painted. " +
                 "Built for the Muse Dash scene monsters: point rootDir at the folder holding the 장면NN folders (or at one 장면NN folder), choose scenes ('1-10,12') " +
                 "and a skeleton name regex (default: notes 05-08 and 10-17, each as _air and _road, e.g. 0113_air = blue, 0113_road = pink). " +
                 "Skeletons sharing a parts folder (the scene's sNN_atlas) are imported into one editor project, so a scene takes about 2-3 minutes instead of 1 minute per skeleton. " +
                 "Output: <outputDir>\\<scene folder>\\<skeleton>.png. Skeletons without the animation, or without a parts folder, are listed, not rendered.")]
    public static async Task<object> SpineRenderBatch(
        [Description("Folder containing the 장면NN folders (each with 원본 and 파츠), or a single 장면NN folder.")] string rootDir,
        [Description("Folder for the PNGs (<scene>\\<skeleton>.png) and the editor projects (_spine\\).")] string outputDir,
        [Description("Scene numbers to do: 'all' (default) or e.g. '1-10,12'. Matches the number at the end of the scene folder name.")] string scenes = "all",
        [Description("Regex on the skeleton file name without extension. Default '^\\d{2}(0[5-8]|1[0-7])_(air|road)$'.")] string skeletonPattern = @"^\d{2}(0[5-8]|1[0-7])_(air|road)$",
        [Description("Animation to take the frame from. Default 'standby'.")] string animation = "standby",
        [Description("Time in seconds inside the animation. Default 0 (first frame).")] double time = 0,
        [Description("Skeleton scale on import. Default 0.5 (see spine_contact_sheet).")] double importScale = 0.5,
        [Description("Crop each PNG to its painted area. Default true. When false every PNG has the scene-wide canvas.")] bool cropToContent = true,
        [Description("Transparent pixels kept around the crop. Default 2.")] int padding = 2,
        [Description("Sampling rate used when time is above 0 or pick is 'largest'. Default 10.")] double fps = 10,
        [Description("'time' (default): the frame at 'time' of 'animation'. 'largest': for every skeleton the sampled frame with the most painted pixels among the " +
                     "candidateAnimations (else among the last frames of all animations except out_*), reported in chosenFrames. Use it when the standby loop starts squashed " +
                     "(e.g. notes 06-08) or a note has no standby (e.g. 15) or the character is hidden at time 0.")] string pick = "time",
        [Description("Regex on animation names considered by pick='largest'. Default '^(stand|standby\\d*)$'.")] string candidateAnimations = @"^(stand|standby\d*)$",
        [Description("'all' (default): a skeleton with skins besides 'default' is rendered once per skin as <skeleton>__<skin>.png " +
                     "(Muse Dash keeps some body parts only in skins such as origin/xiemei/Miku/RinLen; without this a note like 0914 shows only its shadow). " +
                     "The plain <skeleton>.png is written only when its default skin is complete. 'default': skeletons as they are, skins untouched.")] string skins = "all",
        [Description("Path to Spine.com. Defaults to the SPINE_CLI environment variable, then C:\\Program Files\\Spine\\Spine.com.")] string? spineCliPath = null,
        [Description("Spine editor version to run (-u). Default 3.8.99.")] string spineVersion = "3.8.99",
        CancellationToken cancellationToken = default)
    {
        try
        {
            bool includeSkins;
            switch (skins.Trim().ToLowerInvariant())
            {
                case "all": includeSkins = true; break;
                case "default": includeSkins = false; break;
                default: return new { ok = false, error = "skins는 all 또는 default여야 합니다." };
            }
            bool pickLargest;
            switch (pick.Trim().ToLowerInvariant())
            {
                case "time": pickLargest = false; break;
                case "largest": pickLargest = true; break;
                default: return new { ok = false, error = "pick은 time 또는 largest여야 합니다." };
            }
            Regex candidatePattern;
            try { candidatePattern = new Regex(candidateAnimations, RegexOptions.CultureInvariant); }
            catch (ArgumentException ex) { return new { ok = false, error = $"candidateAnimations 정규식 오류: {ex.Message}" }; }
            if (!Directory.Exists(rootDir))
                return new { ok = false, error = $"폴더가 없습니다: {rootDir}" };
            if (time < 0 || fps <= 0 || importScale <= 0 || padding < 0)
                return new { ok = false, error = "time, fps, importScale, padding 값이 올바르지 않습니다." };
            Regex pattern;
            try { pattern = new Regex(skeletonPattern, RegexOptions.CultureInvariant); }
            catch (ArgumentException ex) { return new { ok = false, error = $"skeletonPattern 정규식 오류: {ex.Message}" }; }
            var wanted = ParseSceneNumbers(scenes, out var scenesError);
            if (scenesError is not null) return new { ok = false, error = scenesError };

            var sceneDirs = Directory.Exists(Path.Combine(rootDir, "원본"))
                ? new List<string> { Path.GetFullPath(rootDir) }
                : Directory.EnumerateDirectories(rootDir).Where(d => Directory.Exists(Path.Combine(d, "원본")))
                           .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            sceneDirs = sceneDirs.Where(d => wanted is null || (TrailingNumber(Path.GetFileName(d)) is { } n && wanted.Contains(n))).ToList();
            if (sceneDirs.Count == 0)
                return new { ok = false, error = "해당하는 장면 폴더가 없습니다 (<장면 폴더>\\원본 구조여야 합니다)." };

            var settings = new SpineSettings { CliPath = spineCliPath, Version = spineVersion, ImportScale = importScale };
            // Frame 0 is all that is needed at time 0, so sample coarsely and keep the export small.
            double sampleFps = time == 0 && !pickLargest ? 1 : fps;

            return await Task.Run<object>(() =>
            {
                var sceneResults = new List<object>();
                int total = 0;
                bool allOk = true;
                foreach (var sceneDir in sceneDirs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sceneName = Path.GetFileName(sceneDir);
                    var skels = Directory.EnumerateFiles(Path.Combine(sceneDir, "원본"), "*.skel")
                                         .Where(f => pattern.IsMatch(Path.GetFileNameWithoutExtension(f)))
                                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                    var rendered = new List<string>();
                    var missingAnimation = new List<string>();
                    var noParts = new List<string>();
                    var empty = new List<string>();
                    var skinVariants = new List<string>();
                    var chosen = new List<string>();
                    var warnings = new List<string>();
                    string? error = null;

                    try
                    {
                        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                        foreach (var skel in skels)
                        {
                            var parts = SpineRenderer.GuessPartsDir(skel);
                            if (parts is null) { noParts.Add(Path.GetFileNameWithoutExtension(skel)); continue; }
                            if (!groups.TryGetValue(parts, out var list)) groups[parts] = list = new List<string>();
                            list.Add(skel);
                        }

                        // One editor project holds all its skeletons at once, so memory grows with their number
                        // (skin variants included): keep each project to about MaxSkeletonsPerProject.
                        var chunks = new List<(string PartsDir, List<string> Files)>();
                        foreach (var (partsDir, files) in groups)
                        {
                            var current = new List<string>();
                            int cost = 0;
                            foreach (var file in files)
                            {
                                int fileCost = 1 + (includeSkins ? SkelSkinReader.TryRead(file)?.ExtraSkins.Count ?? 0 : 0);
                                if (current.Count > 0 && cost + fileCost > MaxSkeletonsPerProject)
                                {
                                    chunks.Add((partsDir, current));
                                    current = new List<string>();
                                    cost = 0;
                                }
                                current.Add(file);
                                cost += fileCost;
                            }
                            if (current.Count > 0) chunks.Add((partsDir, current));
                        }

                        int groupIndex = 0;
                        foreach (var (partsDir, files) in chunks)
                        {
                            var work = Path.Combine(outputDir, WorkFolder, sceneName, $"g{groupIndex++}");
                            var project = SpineRenderer.PrepareMany(files, partsDir, work, settings, includeSkins, cancellationToken);
                            var names = project.Skeletons;
                            skinVariants.AddRange(names.Where(n => n.Contains("__")));
                            var framesDir = Path.Combine(work, FramesFolder);
                            // Without a crop every PNG needs the same scene-wide canvas; with it, tight per-frame bounds are far cheaper to export.
                            var frames = SpineRenderer.ExportAllSkeletons(project, names, framesDir, sampleFps, maxBounds: !cropToContent, scalePercent: 100, cancellationToken);

                            var outDir = Path.Combine(outputDir, sceneName);
                            Directory.CreateDirectory(outDir);
                            foreach (var name in names)
                            {
                                string source;
                                if (pickLargest)
                                {
                                    // The standby loop of some notes starts squashed, and notes that only have in_* animations
                                    // start hidden: take the frame where the most is painted.
                                    var mine = frames.Where(f => f.Skeleton == name).ToList();
                                    var candidates = mine.Where(f => candidatePattern.IsMatch(f.Animation)).ToList();
                                    // No standby: use the last frame of each remaining animation, where the character has settled
                                    // (a mid-way frame of an in_* animation can still be falling in on a long string).
                                    if (candidates.Count == 0)
                                        candidates = mine.Where(f => !f.Animation.StartsWith("out", StringComparison.OrdinalIgnoreCase))
                                                         .GroupBy(f => f.Animation).Select(g => g.MaxBy(f => f.Index)!).ToList();
                                    if (candidates.Count == 0) { missingAnimation.Add(name); continue; }
                                    var best = candidates.Select(f => (Frame: f, Mass: PaintedMass(f.Path)))
                                                         .OrderByDescending(x => x.Mass).ThenBy(x => x.Frame.Animation, StringComparer.Ordinal).ThenBy(x => x.Frame.Index).First();
                                    source = best.Frame.Path;
                                    chosen.Add($"{name} = {best.Frame.Animation}@{best.Frame.Index / sampleFps:0.00}");
                                }
                                else
                                {
                                    var own = frames.Where(f => f.Skeleton == name && f.Animation == animation).OrderBy(f => f.Index).ToList();
                                    if (own.Count == 0) { missingAnimation.Add(name); continue; }
                                    int index = Math.Clamp((int)Math.Round(time * sampleFps), 0, own[^1].Index);
                                    source = own.First(f => f.Index == index).Path;
                                }
                                var dest = Path.Combine(outDir, name + ".png");
                                if (!SaveFrame(source, dest, cropToContent, padding)) { empty.Add(name); continue; }
                                rendered.Add(name);
                            }
                            warnings.AddRange(project.Warnings.Where(w => !w.Contains("dimensions changed", StringComparison.OrdinalIgnoreCase)));
                            Directory.Delete(framesDir, recursive: true); // full-canvas frames are large and not needed any more
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { error = ex.Message; allOk = false; }

                    // A skeleton that now only has skin variants may still have a plain file from an earlier run
                    // (typically just its shadow). Report those, never delete them.
                    var stale = skinVariants.Select(v => v[..v.IndexOf("__", StringComparison.Ordinal)]).Distinct()
                                            .Where(b => !rendered.Contains(b) && File.Exists(Path.Combine(outputDir, sceneName, b + ".png")))
                                            .ToList();

                    total += rendered.Count;
                    sceneResults.Add(new
                    {
                        scene = sceneName,
                        matched = skels.Count,
                        renderedCount = rendered.Count,
                        rendered,
                        missingAnimation,
                        noPartsFolder = noParts,
                        emptyImage = empty,
                        skinVariants,
                        chosenFrames = chosen,
                        stalePlainFiles = stale,
                        warnings = warnings.Distinct().Take(10),
                        error
                    });
                }
                return new { ok = allOk, animation, time, importScale, totalRendered = total, outputDir = Path.GetFullPath(outputDir), scenes = sceneResults };
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

    // Number of clearly visible pixels (alpha > 24): how much of the character is on screen in this frame.
    private static long PaintedMass(string path)
    {
        var px = RgbaImage.Load(path).Pixels;
        long n = 0;
        for (int i = 3; i < px.Length; i += 4)
            if (px[i] > 24) n++;
        return n;
    }

    // Writes the frame as a PNG, cropped to the painted area plus padding. False if nothing is painted.
    private static bool SaveFrame(string source, string dest, bool crop, int padding)
    {
        if (!crop) { File.Copy(source, dest, overwrite: true); return true; }
        var image = RgbaImage.Load(source);
        if (ContentBounds(image) is not { } b) return false;
        int x = Math.Max(0, b.Left - padding), y = Math.Max(0, b.Top - padding);
        int right = Math.Min(image.Width, b.Right + padding), bottom = Math.Min(image.Height, b.Bottom + padding);
        File.WriteAllBytes(dest, image.Crop(x, y, right - x, bottom - y).EncodePng());
        return true;
    }

    // Bounding box of what is painted (alpha > 4), except a flat blob far below or above the rest: a hovering
    // monster's ground shadow sits hundreds of pixels away and would shrink the monster to a speck in the file.
    // A shadow right under the monster stays.
    private static (int Left, int Top, int Right, int Bottom)? ContentBounds(RgbaImage image)
    {
        const int threshold = 4;
        int w = image.Width, h = image.Height;
        var px = image.Pixels;
        var rowCount = new int[h];
        for (int y = 0; y < h; y++)
        {
            int n = 0;
            for (int x = 0; x < w; x++)
                if (px[(y * w + x) * 4 + 3] > threshold) n++;
            rowCount[y] = n;
        }

        // Runs of consecutive painted rows.
        var runs = new List<(int Start, int End, long Mass)>();
        int start = -1;
        long mass = 0;
        for (int y = 0; y <= h; y++)
        {
            bool painted = y < h && rowCount[y] > 0;
            if (painted) { if (start < 0) start = y; mass += rowCount[y]; }
            else if (start >= 0) { runs.Add((start, y - 1, mass)); start = -1; mass = 0; }
        }
        if (runs.Count == 0) return null;

        var main = runs.MaxBy(r => r.Mass);
        int mainHeight = main.End - main.Start + 1;
        int minGap = Math.Max(12, mainHeight * 15 / 100);
        var kept = runs.Where(r =>
        {
            if (r == main) return true;
            int gap = r.End < main.Start ? main.Start - r.End - 1 : r.Start - main.End - 1;
            int height = r.End - r.Start + 1;
            int width = 0;
            for (int y = r.Start; y <= r.End; y++) width = Math.Max(width, rowCount[y]);
            bool flat = height * 5 <= width * 2; // wider than 2.5 x its height: an ellipse, not a limb
            return !(flat && gap > minGap);
        }).ToList();

        int top = kept.Min(r => r.Start), bottom = kept.Max(r => r.End);
        int left = w, rightEdge = -1;
        for (int y = top; y <= bottom; y++)
            for (int x = 0; x < w; x++)
                if (px[(y * w + x) * 4 + 3] > threshold) { if (x < left) left = x; if (x > rightEdge) rightEdge = x; }
        return (left, top, rightEdge + 1, bottom + 1);
    }

    private static int? TrailingNumber(string name)
    {
        var m = Regex.Match(name, @"(\d+)$");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    // "all" -> null (no filter); "1-10,12" -> {1..10, 12}
    private static HashSet<int>? ParseSceneNumbers(string spec, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(spec) || spec.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) return null;
        var set = new HashSet<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Regex.Match(part, @"^(\d+)(?:-(\d+))?$");
            if (!m.Success) { error = $"scenes 형식이 올바르지 않습니다: '{part}'. 예: 1-10,12"; return null; }
            int a = int.Parse(m.Groups[1].Value), b = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : a;
            for (int n = Math.Min(a, b); n <= Math.Max(a, b); n++) set.Add(n);
        }
        return set;
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
