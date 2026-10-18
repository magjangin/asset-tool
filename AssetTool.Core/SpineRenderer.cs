using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

namespace asset_tool
{
    public sealed class SpineSettings
    {
        public string? CliPath { get; init; }
        // Spine editor version to run through the launcher (-u). Muse Dash skeletons are 3.8.99.
        public string Version { get; init; } = "3.8.99";
        // Skeleton scale applied when importing (-s). Muse Dash part PNGs are half the size
        // the skeleton data was authored for, so 0.5 makes the parts line up again.
        public double ImportScale { get; init; } = 0.5;
        // Java heap for the editor (-Xmx); the launcher's default of 4096 overflows with many skeletons in one project.
        public int MaxMemoryMb { get; init; } = 8192;
    }

    public sealed class SpineProject
    {
        public string Name = "";
        public string ProjectPath = "";
        public string PartsDir = "";
        public string WorkDir = "";
        public SpineSettings Settings = new();
        public string CliPath = "";
        public List<string> Warnings = new();
        // Skeleton names inside the project (PrepareMany); skin variants are "<name>__<skin>".
        public List<string> Skeletons = new();
    }

    public sealed class SpineFrame
    {
        // Set by multi-skeleton exports.
        public string Skeleton = "";
        public string Animation = "";
        public int Index;
        public string Path = "";
    }

    // Drives the Spine editor's command line (Spine.com) to render a skeleton the way the
    // editor would: import the .skel, point it at the part PNGs, export PNG frames.
    // The .skel header of Muse Dash files has no images path, and the CLI cannot set one,
    // so the skeleton goes through JSON once: skel -> project -> JSON, "images" patched,
    // JSON -> project (with the import scale).
    public static class SpineRenderer
    {
        public const string CliEnvVar = "SPINE_CLI";
        private const string DefaultCli = @"C:\Program Files\Spine\Spine.com";

        public static string? FindCli(string? explicitPath)
        {
            foreach (var c in new[] { explicitPath, Environment.GetEnvironmentVariable(CliEnvVar), DefaultCli })
                if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) return c;
            return null;
        }

        // "<bundle>\원본\X.skel" -> "<bundle>\파츠\X" (what extract_and_split writes).
        public static string? GuessPartsDir(string skelPath)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(skelPath)) ?? "";
            var stem = Path.GetFileNameWithoutExtension(skelPath);
            var candidates = new List<string>();
            if (Directory.GetParent(dir) is { } bundle)
                candidates.Add(Path.Combine(bundle.FullName, "파츠", stem));
            candidates.Add(Path.Combine(dir, stem));
            var own = candidates.FirstOrDefault(Directory.Exists);
            if (own is not null) return own;

            // Scene skeletons such as 0105_air have no atlas of their own: their regions
            // ("0105/images/body") live in a shared atlas, <bundle>\파츠\s01_atlas\0105\...
            var prefix = Regex.Match(stem, @"^\d+").Value;
            if (prefix.Length > 0 && Directory.GetParent(dir) is { } parent && Directory.Exists(Path.Combine(parent.FullName, "파츠")))
                // Prefer the shared "...atlas" folder: a per-skeleton folder such as 0108_air_double also
                // contains a "0108" subfolder but holds only that variant's parts.
                return Directory.EnumerateDirectories(Path.Combine(parent.FullName, "파츠"))
                                .Where(d => Directory.Exists(Path.Combine(d, prefix)))
                                .OrderBy(d => Path.GetFileName(d).Contains("atlas", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                                .ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
                                .FirstOrDefault();
            return null;
        }

        public static SpineProject Prepare(string skelPath, string partsDir, string workDir,
                                           SpineSettings settings, CancellationToken ct)
        {
            var cli = FindCli(settings.CliPath)
                ?? throw new FileNotFoundException($"Spine.com을 찾을 수 없습니다. spineCliPath를 주거나 환경변수 {CliEnvVar}를 설정하세요.");
            skelPath = Path.GetFullPath(skelPath);
            partsDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(partsDir));
            workDir = Path.GetFullPath(workDir);
            var name = Path.GetFileNameWithoutExtension(skelPath);
            Directory.CreateDirectory(workDir);

            var project = new SpineProject
            {
                Name = name, PartsDir = partsDir, WorkDir = workDir, Settings = settings, CliPath = cli,
                ProjectPath = Path.Combine(workDir, name + ".spine")
            };

            // Same inputs as last time -> reuse the project (each CLI start costs ~10 s).
            var fi = new FileInfo(skelPath);
            var key = string.Join("|", fi.Length, fi.LastWriteTimeUtc.Ticks, partsDir,
                                  settings.ImportScale.ToString(CultureInfo.InvariantCulture), settings.Version);
            var metaPath = Path.Combine(workDir, "prepared.json");
            if (File.Exists(project.ProjectPath) && File.Exists(metaPath) &&
                JsonNode.Parse(File.ReadAllText(metaPath))?["key"]?.GetValue<string>() == key)
                return project;

            var warnings = new List<string>();
            var rawDir = Path.Combine(workDir, "raw");
            var jsonDir = Path.Combine(workDir, "json");
            var patchedDir = Path.Combine(workDir, "patched");
            foreach (var d in new[] { rawDir, jsonDir, patchedDir })
            {
                if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
                Directory.CreateDirectory(d);
            }
            if (File.Exists(project.ProjectPath)) File.Delete(project.ProjectPath);

            // 1. skel -> project (copy first: the importer looks around the file it is given).
            var skelCopy = Path.Combine(rawDir, name + ".skel");
            File.Copy(skelPath, skelCopy, overwrite: true);
            var rawProject = Path.Combine(rawDir, name + ".spine");
            RunChecked(cli, settings, workDir, new[] { "-i", skelCopy, "-o", rawProject, "-r" }, rawProject, "스켈레톤 가져오기", new List<string>(), ct);

            // 2. project -> JSON, with the editor-only data (images path) included.
            var exportJson = WriteSettings(workDir, "export_json.json", new Dictionary<string, object?>
            {
                ["class"] = "export-json", ["extension"] = ".json", ["format"] = "JSON",
                ["nonessential"] = true, ["cleanUp"] = false, ["output"] = "single"
            });
            var json = Path.Combine(jsonDir, name + ".json");
            RunChecked(cli, settings, workDir, new[] { "-i", rawProject, "-o", jsonDir, "-e", exportJson }, json, "JSON 내보내기", new List<string>(), ct); // images path is still unset here: its "not found" lines are noise

            // 3. point the skeleton at the part PNGs.
            var root = JsonNode.Parse(File.ReadAllText(json, Encoding.UTF8))
                ?? throw new InvalidDataException("스켈레톤 JSON을 읽을 수 없습니다.");
            (root["skeleton"] ??= new JsonObject())["images"] = partsDir;
            var patched = Path.Combine(patchedDir, name + ".json");
            File.WriteAllText(patched, root.ToJsonString(), new UTF8Encoding(false));

            // 4. JSON -> project at the import scale.
            RunChecked(cli, settings, workDir, new[] { "-i", patched, "-s", settings.ImportScale.ToString(CultureInfo.InvariantCulture),
                                              "-o", project.ProjectPath, "-r" }, project.ProjectPath, "프로젝트 만들기", warnings, ct);

            project.Warnings = warnings.Distinct().ToList();
            File.WriteAllText(metaPath, new JsonObject { ["key"] = key }.ToJsonString());
            return project;
        }

        // Several skeletons that share one parts folder go into a single project, so a whole
        // scene costs a handful of editor starts instead of four per skeleton.
        // Skeletons keep their file names; the project is <workDir>\scene.spine.
        // With skinVariants, a skeleton that has skins besides "default" is split up: one copy per skin named
        // "<name>__<skin>", whose default skin is the original default plus that skin, with every other skin removed.
        // Muse Dash keeps some body parts only in such a skin ("origin", "xiemei", "Miku"...). Removing the other skins
        // matters: while they exist, the editor's PNG export draws with one of them selected and skips the rest.
        // The plain "<name>" is kept only if the default skin already holds every slot some skin fills.
        public static SpineProject PrepareMany(IReadOnlyList<string> skelPaths, string partsDir, string workDir,
                                               SpineSettings settings, bool skinVariants, CancellationToken ct)
        {
            var cli = FindCli(settings.CliPath)
                ?? throw new FileNotFoundException($"Spine.com을 찾을 수 없습니다. spineCliPath를 주거나 환경변수 {CliEnvVar}를 설정하세요.");
            partsDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(partsDir));
            workDir = Path.GetFullPath(workDir);
            Directory.CreateDirectory(workDir);
            var project = new SpineProject
            {
                Name = "scene", PartsDir = partsDir, WorkDir = workDir, Settings = settings, CliPath = cli,
                ProjectPath = Path.Combine(workDir, "scene.spine")
            };

            var skels = skelPaths.Select(Path.GetFullPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            var key = string.Join("|", skels.Select(p => { var f = new FileInfo(p); return $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"; })
                                       .Append(partsDir)
                                       .Append(settings.ImportScale.ToString(CultureInfo.InvariantCulture))
                                       .Append(settings.Version)
                                       .Append(skinVariants ? "skins3" : "noskins"));
            var metaPath = Path.Combine(workDir, "prepared.json");
            if (File.Exists(project.ProjectPath) && File.Exists(metaPath) &&
                JsonNode.Parse(File.ReadAllText(metaPath)) is { } meta && meta["key"]?.GetValue<string>() == key &&
                meta["skeletons"] is JsonArray cached)
            {
                project.Skeletons = cached.Select(n => n!.GetValue<string>()).ToList();
                return project;
            }

            var skelDir = Path.Combine(workDir, "skel");
            var rawDir = Path.Combine(workDir, "raw");
            var jsonDir = Path.Combine(workDir, "json");
            var patchedDir = Path.Combine(workDir, "patched");
            foreach (var d in new[] { skelDir, rawDir, jsonDir, patchedDir })
            {
                if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
                Directory.CreateDirectory(d);
            }
            if (File.Exists(project.ProjectPath)) File.Delete(project.ProjectPath);
            foreach (var s in skels) File.Copy(s, Path.Combine(skelDir, Path.GetFileName(s)), overwrite: true);

            var rawProject = Path.Combine(rawDir, "scene.spine");
            RunChecked(cli, settings, workDir, new[] { "-i", skelDir, "-o", rawProject, "-r" }, rawProject, "스켈레톤 가져오기", new List<string>(), ct);

            var exportJson = WriteSettings(workDir, "export_json.json", new Dictionary<string, object?>
            {
                ["class"] = "export-json", ["extension"] = ".json", ["format"] = "JSON",
                ["nonessential"] = true, ["cleanUp"] = false, ["output"] = "single"
            });
            RunChecked(cli, settings, workDir, new[] { "-i", rawProject, "-o", jsonDir, "-e", exportJson }, null, "JSON 내보내기", new List<string>(), ct);

            foreach (var s in skels)
            {
                var json = Path.Combine(jsonDir, Path.GetFileNameWithoutExtension(s) + ".json");
                if (!File.Exists(json)) throw new InvalidOperationException($"JSON 내보내기에서 빠진 스켈레톤: {Path.GetFileName(s)}");
                var root = JsonNode.Parse(File.ReadAllText(json, Encoding.UTF8)) ?? throw new InvalidDataException("스켈레톤 JSON을 읽을 수 없습니다.");
                (root["skeleton"] ??= new JsonObject())["images"] = partsDir;
                var baseName = Path.GetFileNameWithoutExtension(s);

                void Add(string name, JsonNode node)
                {
                    File.WriteAllText(Path.Combine(patchedDir, name + ".json"), node.ToJsonString(), new UTF8Encoding(false));
                    project.Skeletons.Add(name);
                }

                var extraSkins = skinVariants ? SkinNames(root) : new List<string>();
                if (extraSkins.Count == 0) { Add(baseName, root); continue; }

                foreach (var skin in extraSkins)
                    Add(baseName + "__" + Regex.Replace(skin, @"[^A-Za-z0-9_]", "_"), MergeSkin(root, skin));
                if (DefaultHasEverySkinSlot(root, extraSkins))
                    Add(baseName, DefaultSkinOnly(root));
            }

            var warnings = new List<string>();
            RunChecked(cli, settings, workDir, new[] { "-i", patchedDir, "-s", settings.ImportScale.ToString(CultureInfo.InvariantCulture),
                                                       "-o", project.ProjectPath, "-r" }, project.ProjectPath, "프로젝트 만들기", warnings, ct);
            project.Warnings = warnings.Distinct().ToList();
            File.WriteAllText(metaPath, new JsonObject
            {
                ["key"] = key,
                ["skeletons"] = new JsonArray(project.Skeletons.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray())
            }.ToJsonString());
            return project;
        }

        // Names of the skins besides "default" that carry attachments.
        private static List<string> SkinNames(JsonNode skeletonJson) =>
            (skeletonJson["skins"] as JsonArray)?
                .Select(s => s?["name"]?.GetValue<string>())
                .Where(n => n is not null && n != "default" && SkinAttachments(skeletonJson, n) is { Count: > 0 })
                .Select(n => n!).ToList() ?? new List<string>();

        private static JsonObject? SkinAttachments(JsonNode skeletonJson, string skin) =>
            (skeletonJson["skins"] as JsonArray)?.FirstOrDefault(s => s?["name"]?.GetValue<string>() == skin)?["attachments"] as JsonObject;

        private static bool DefaultHasEverySkinSlot(JsonNode skeletonJson, List<string> skins)
        {
            var have = SkinAttachments(skeletonJson, "default")?.Select(kv => kv.Key).ToHashSet() ?? new HashSet<string>();
            return skins.All(skin => SkinAttachments(skeletonJson, skin)!.All(kv => have.Contains(kv.Key)));
        }

        private static JsonObject CloneDefaultSkin(JsonNode copy)
        {
            var def = (copy["skins"] as JsonArray)?.FirstOrDefault(s => s?["name"]?.GetValue<string>() == "default")?.DeepClone() as JsonObject
                      ?? new JsonObject { ["name"] = "default" };
            if (def["attachments"] is not JsonObject) def["attachments"] = new JsonObject();
            return def;
        }

        // The skeleton with its default skin only.
        private static JsonNode DefaultSkinOnly(JsonNode skeletonJson)
        {
            var copy = skeletonJson.DeepClone();
            copy["skins"] = new JsonArray(CloneDefaultSkin(copy));
            RekeyDeform(copy, null);
            return copy;
        }

        // Copy of the skeleton whose only skin is the default skin plus the given skin's attachments (the skin wins on
        // a clash). Bones and constraints that exist only for a skin are switched on, since no skin is selected any more.
        private static JsonNode MergeSkin(JsonNode skeletonJson, string skin)
        {
            var copy = skeletonJson.DeepClone();
            var def = CloneDefaultSkin(copy);
            var defAttachments = (JsonObject)def["attachments"]!;
            if (SkinAttachments(copy, skin) is { } fromSkin)
            {
                foreach (var (slot, attachments) in fromSkin.ToList())
                {
                    var target = defAttachments[slot] as JsonObject ?? (JsonObject)(defAttachments[slot] = new JsonObject())!;
                    if (attachments is not JsonObject items) continue;
                    foreach (var (attName, att) in items.ToList())
                    {
                        var clone = att?.DeepClone();
                        // Spine re-keys a default-skin attachment by its "name" on import, which would no longer match
                        // the slot's setup attachment (the key). Keep key and name equal; the image lookup moves to "path".
                        if (clone is JsonObject o && o["name"]?.GetValue<string>() is { } realName && realName != attName)
                        {
                            o["path"] ??= realName;
                            o["name"] = attName;
                        }
                        target[attName] = clone;
                    }
                }
            }
            ResolveLinkedMeshes(skeletonJson, defAttachments);
            copy["skins"] = new JsonArray(def);
            RekeyDeform(copy, skin);
            foreach (var listName in new[] { "bones", "ik", "transform", "path" })
                if (copy[listName] is JsonArray list)
                    foreach (var item in list)
                        (item as JsonObject)?.Remove("skin");
            return copy;
        }

        // A linked mesh borrows the geometry of a parent mesh, possibly from another skin that no longer exists
        // in the variant (RinLen's body points at Miku's body). Turn it into a plain mesh carrying that geometry.
        private static void ResolveLinkedMeshes(JsonNode originalSkeleton, JsonObject defaultAttachments)
        {
            foreach (var (slot, atts) in defaultAttachments.ToList())
            {
                if (atts is not JsonObject items) continue;
                foreach (var (key, att) in items.ToList())
                {
                    if (att is not JsonObject linked || linked["type"]?.GetValue<string>() != "linkedmesh") continue;
                    var parentName = linked["parent"]?.GetValue<string>();
                    var parentSkin = linked["skin"]?.GetValue<string>() ?? "default";
                    if (parentName is null) continue;
                    if (SkinAttachments(originalSkeleton, parentSkin)?[slot]?[parentName] is not JsonObject parent) continue;
                    if (parent["type"]?.GetValue<string>() is not ("mesh" or null)) continue;

                    var mesh = (JsonObject)parent.DeepClone();
                    mesh["name"] = key;
                    mesh["path"] = linked["path"]?.DeepClone() ?? linked["name"]?.DeepClone() ?? JsonValue.Create(key);
                    mesh["type"] = "mesh";
                    foreach (var field in new[] { "color", "width", "height" })
                        if (linked[field] is { } v) mesh[field] = v.DeepClone();
                    items[key] = mesh;
                }
            }
        }

        // Deform timelines are keyed by skin name. Keep the default skin's and fold the chosen skin's into it;
        // timelines for skins that were removed make the file unreadable.
        private static void RekeyDeform(JsonNode skeleton, string? mergedSkin)
        {
            if (skeleton["animations"] is not JsonObject animations) return;
            foreach (var (_, animation) in animations.ToList())
            {
                if (animation?["deform"] is not JsonObject deform) continue;
                var merged = deform["default"] as JsonObject ?? new JsonObject();
                if (mergedSkin is not null && deform[mergedSkin] is JsonObject fromSkin)
                {
                    foreach (var (slot, byAttachment) in fromSkin.ToList())
                    {
                        var target = merged[slot] as JsonObject ?? (JsonObject)(merged[slot] = new JsonObject())!;
                        if (byAttachment is JsonObject atts)
                            foreach (var (attName, timeline) in atts.ToList())
                                target[attName] = timeline?.DeepClone();
                    }
                }
                foreach (var key in deform.Select(kv => kv.Key).ToList())
                    deform.Remove(key);
                if (merged.Count > 0) deform["default"] = merged;
                if (deform.Count == 0) ((JsonObject)animation).Remove("deform");
            }
        }

        // Every animation of every skeleton in one run: <framesDir>\<skeleton>-<animation>_NN.png
        public static List<SpineFrame> ExportAllSkeletons(SpineProject project, IReadOnlyCollection<string> skeletons, string framesDir,
                                                          double fps, bool maxBounds, int scalePercent, CancellationToken ct)
        {
            if (Directory.Exists(framesDir)) Directory.Delete(framesDir, recursive: true);
            Directory.CreateDirectory(framesDir);
            var settings = WriteSettings(project.WorkDir, "export_png_scene.json", new Dictionary<string, object?>
            {
                ["class"] = "export-png", ["exportType"] = "animation", ["skeletonType"] = "all", ["animationType"] = "all",
                ["fps"] = fps, ["maxBounds"] = maxBounds, ["scale"] = scalePercent
            });
            var warnings = new List<string>();
            RunChecked(project.CliPath, project.Settings, project.WorkDir, new[] { "-i", project.ProjectPath, "-o", framesDir, "-e", settings },
                       null, "PNG 내보내기", warnings, ct);
            project.Warnings = project.Warnings.Concat(warnings).Distinct().ToList();

            var frames = new List<SpineFrame>();
            foreach (var f in Directory.EnumerateFiles(framesDir, "*.png"))
            {
                var stem = Path.GetFileNameWithoutExtension(f);
                var skeleton = skeletons.FirstOrDefault(s => stem.StartsWith(s + "-", StringComparison.Ordinal));
                if (skeleton is null) continue;
                var m = Regex.Match(stem[(skeleton.Length + 1)..], @"^(.*)_(\d+)$");
                if (m.Success) frames.Add(new SpineFrame { Skeleton = skeleton, Animation = m.Groups[1].Value, Index = int.Parse(m.Groups[2].Value), Path = f });
            }
            return frames;
        }

        // Every animation in one CLI run: <framesDir>\<skeleton>-<animation>_NN.png
        // (frame NN is at time NN / fps).
        public static List<SpineFrame> ExportAll(SpineProject project, string framesDir, double fps, bool maxBounds,
                                                 int scalePercent, CancellationToken ct)
        {
            if (Directory.Exists(framesDir)) Directory.Delete(framesDir, recursive: true);
            Directory.CreateDirectory(framesDir);
            var settings = WriteSettings(project.WorkDir, "export_png_all.json", PngSettings("all", null, fps, maxBounds, scalePercent));
            var warnings = new List<string>();
            RunChecked(project.CliPath, project.Settings, project.WorkDir, new[] { "-i", project.ProjectPath, "-o", framesDir, "-e", settings },
                       null, "PNG 내보내기", warnings, ct);
            project.Warnings = project.Warnings.Concat(warnings).Distinct().ToList();

            var prefix = project.Name + "-";
            var frames = new List<SpineFrame>();
            foreach (var f in Directory.EnumerateFiles(framesDir, "*.png"))
            {
                var stem = Path.GetFileNameWithoutExtension(f);
                if (!stem.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var m = Regex.Match(stem[prefix.Length..], @"^(.*)_(\d+)$");
                if (!m.Success) continue;
                frames.Add(new SpineFrame { Animation = m.Groups[1].Value, Index = int.Parse(m.Groups[2].Value), Path = f });
            }
            if (frames.Count == 0)
                throw new InvalidOperationException("내보낸 프레임이 없습니다. 스켈레톤에 애니메이션이 있는지 확인하세요.");
            return frames.OrderBy(f => f.Animation, StringComparer.Ordinal).ThenBy(f => f.Index).ToList();
        }

        // One animation: <outDir>\<prefix>_NN.png
        public static List<SpineFrame> ExportAnimation(SpineProject project, string animation, string outDir, string prefix,
                                                       double fps, bool maxBounds, int scalePercent, CancellationToken ct)
        {
            Directory.CreateDirectory(outDir);
            var settings = WriteSettings(project.WorkDir, "export_png_one.json", PngSettings("single", animation, fps, maxBounds, scalePercent));
            var warnings = new List<string>();
            RunChecked(project.CliPath, project.Settings, project.WorkDir,
                       new[] { "-i", project.ProjectPath, "-o", Path.Combine(outDir, prefix), "-e", settings },
                       null, $"'{animation}' 내보내기", warnings, ct);
            project.Warnings = project.Warnings.Concat(warnings).Distinct().ToList();

            var frames = new List<SpineFrame>();
            foreach (var f in Directory.EnumerateFiles(outDir, prefix + "_*.png"))
            {
                var m = Regex.Match(Path.GetFileNameWithoutExtension(f), @"_(\d+)$");
                if (m.Success) frames.Add(new SpineFrame { Animation = animation, Index = int.Parse(m.Groups[1].Value), Path = f });
            }
            if (frames.Count == 0)
                throw new InvalidOperationException($"애니메이션 '{animation}' 프레임이 없습니다. 이름이 맞는지 확인하세요.");
            return frames.OrderBy(f => f.Index).ToList();
        }

        private static Dictionary<string, object?> PngSettings(string animationType, string? animation, double fps,
                                                                bool maxBounds, int scalePercent)
        {
            var d = new Dictionary<string, object?>
            {
                ["class"] = "export-png", ["exportType"] = "animation", ["skeletonType"] = "single",
                ["animationType"] = animationType, ["fps"] = fps, ["maxBounds"] = maxBounds, ["scale"] = scalePercent
            };
            if (animation is not null) d["animation"] = animation;
            return d;
        }

        private static string WriteSettings(string dir, string file, Dictionary<string, object?> values)
        {
            var path = Path.Combine(dir, file);
            File.WriteAllText(path, JsonSerializer.Serialize(values), new UTF8Encoding(false));
            return path;
        }

        // Runs the CLI and fails loudly: a non-zero exit, an ERROR line, or a missing expected output.
        private static void RunChecked(string cli, SpineSettings settings, string workDir, IEnumerable<string> args, string? expectedFile,
                                       string what, List<string> warnings, CancellationToken ct)
        {
            var all = new List<string> { $"-Xmx{settings.MaxMemoryMb}m", "-u", settings.Version };
            all.AddRange(args);
            var (exit, lines) = Run(cli, all, workDir, ct);

            foreach (var l in lines)
            {
                var t = l.Trim();
                if (t.Length == 0) continue;
                bool isWarning = t.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase) ||
                                 t.Contains("not found", StringComparison.OrdinalIgnoreCase);
                if (isWarning && !t.Contains("Welcome data download", StringComparison.OrdinalIgnoreCase))
                    warnings.Add(t);
            }

            var errors = lines.Where(l => l.TrimStart().StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) ||
                                          l.Contains("Exception", StringComparison.Ordinal)).Take(5).ToList();
            if (exit != 0 || errors.Count > 0 || (expectedFile is not null && !File.Exists(expectedFile)))
            {
                var tail = string.Join(" / ", (errors.Count > 0 ? errors : lines.TakeLast(5).ToList()).Select(l => l.Trim()));
                throw new InvalidOperationException($"Spine {what} 실패 (종료 코드 {exit}): {tail}");
            }
        }

        private static (int ExitCode, List<string> Lines) Run(string cli, IEnumerable<string> args, string workDir, CancellationToken ct)
        {
            // The launcher unpacks its own libraries into <cwd>\.temp, so run it inside the work folder.
            var psi = new ProcessStartInfo(cli)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Spine을 실행하지 못했습니다.");
            var lines = new List<string>();
            void Add(string? l) { if (l is not null) lock (lines) lines.Add(l); }
            proc.OutputDataReceived += (_, e) => Add(e.Data);
            proc.ErrorDataReceived += (_, e) => Add(e.Data);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using (ct.Register(() => { try { proc.Kill(true); } catch { } }))
                proc.WaitForExit();
            proc.WaitForExit(); // flush the async readers
            ct.ThrowIfCancellationRequested();
            lock (lines) return (proc.ExitCode, lines.ToList());
        }
    }
}
