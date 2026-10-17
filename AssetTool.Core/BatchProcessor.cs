using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace asset_tool
{
    public sealed class BatchOptions
    {
        public ExtractOptions Extract { get; init; } = new();
        public NamingMode Naming { get; init; } = NamingMode.KeepFolders;

        // Standalone sprites no atlas refers to (e.g. Muse Dash's 0102_* long
        // note textures) are copied as-is so the output is a complete set.
        public bool CopyUnusedPngs { get; init; } = true;

        // Drop the atlas's source folder from the output path: parts go to
        //   <output>\<atlas name>\<region>.png
        // (what Spine's image-folder import expects, e.g. a "TextAsset" style tree)
        // instead of <output>\<atlas folder>\<atlas name>\<region>.png. Two atlases
        // with the same name get a _2, _3 ... suffix so they never overwrite each other.
        public bool FlattenFolders { get; init; }
    }

    // Done/Total drive the progress bar; LogLine (if any) is appended to the log.
    public readonly record struct BatchProgress(int Done, int Total, string? LogLine);

    public sealed class BatchSummary
    {
        public int Atlases;
        public int Pages;
        public int Parts;
        public int CleanedRegions;
        public int Warnings;
        public int Failures;
        public int CopiedPngs;
        public string LogPath = "";
        // .skel files (relative to the input) that define skins besides "default".
        public List<string> SkinSkeletons = new();
    }

    // Walks a folder tree (e.g. an AssetStudio export with TextAsset\ and
    // Texture2D\), splits every .atlas it finds and writes the parts to
    //   <output>\<atlas's folder relative to input>\<atlas name>\<region>.png
    // Mirroring the relative folder keeps same-named atlases apart (skin1 vs
    // skin2). A failing atlas is logged and skipped; it never stops the run.
    public static class BatchProcessor
    {
        public const string LogFileName = "_split_log.txt";
        public const string UnusedPngFolder = "_unused_pngs";

        public static BatchSummary Run(string inputRoot, string outputRoot, BatchOptions options,
                                       IProgress<BatchProgress>? progress, CancellationToken ct)
        {
            inputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputRoot));
            outputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
            if (string.Equals(inputRoot, outputRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("입력 폴더와 출력 폴더가 같습니다. 결과는 다른 폴더에 저장해 주세요.");

            var summary = new BatchSummary();
            var log = new StringBuilder();
            int done = 0, total = 0;

            void Log(string line)
            {
                log.AppendLine(line);
                progress?.Report(new BatchProgress(done, total, line));
            }

            // Never re-process our own output when it lives inside the input tree.
            var outputPrefix = outputRoot + Path.DirectorySeparatorChar;
            bool IsInOutput(string path) => path.StartsWith(outputPrefix, StringComparison.OrdinalIgnoreCase);

            var files = Directory.EnumerateFiles(inputRoot, "*", new EnumerationOptions
                                 {
                                     RecurseSubdirectories = true,
                                     IgnoreInaccessible = true
                                 })
                                 .Where(f => !IsInOutput(f))
                                 .ToList();

            var atlases = files.Where(f => f.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase) ||
                                           f.EndsWith(".atlas.txt", StringComparison.OrdinalIgnoreCase))
                               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                               .ToList();
            var pngs = files.Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
            var pngsByName = pngs.GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                                 .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var usedPngs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedFlatNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            total = atlases.Count + (options.CopyUnusedPngs ? 1 : 0);
            Log($"입력: {inputRoot}");
            Log($"출력: {outputRoot}");
            Log($"아틀라스 {atlases.Count}개, PNG {pngs.Count}개 발견");
            Log("");

            foreach (var atlasPath in atlases)
            {
                ct.ThrowIfCancellationRequested();
                var relAtlas = Path.GetRelativePath(inputRoot, atlasPath);
                try
                {
                    ProcessAtlas(atlasPath, relAtlas, inputRoot, outputRoot, options, pngsByName, usedPngs, usedFlatNames, summary, Log, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    summary.Failures++;
                    Log($"  ✗ {relAtlas}: 실패 - {ex.Message}");
                }
                done++;
                progress?.Report(new BatchProgress(done, total, null));
            }

            if (options.CopyUnusedPngs)
            {
                var unused = pngs.Where(p => !usedPngs.Contains(p)).ToList();
                if (unused.Count > 0)
                {
                    Log("");
                    Log($"아틀라스에 쓰이지 않은 PNG {unused.Count}개를 그대로 복사:");
                }
                foreach (var png in unused)
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = Path.GetRelativePath(inputRoot, png);
                    try
                    {
                        // Flattened output has no per-folder structure, so keep stray PNGs apart.
                        var dest = Path.Combine(outputRoot, options.FlattenFolders ? Path.Combine(UnusedPngFolder, rel) : rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Copy(png, dest, overwrite: true);
                        summary.CopiedPngs++;
                        Log($"  {rel}");
                    }
                    catch (Exception ex)
                    {
                        summary.Failures++;
                        Log($"  ✗ {rel}: 복사 실패 - {ex.Message}");
                    }
                }
                done++;
                progress?.Report(new BatchProgress(done, total, null));
            }

            ReportSkins(files.Where(f => f.EndsWith(".skel", StringComparison.OrdinalIgnoreCase)), inputRoot, summary, Log, ct);

            Log("");
            Log($"완료: 아틀라스 {summary.Atlases}개 / 페이지 {summary.Pages}개 / 파츠 {summary.Parts}개" +
                $" (겹침 정리된 리전 {summary.CleanedRegions}개), 복사한 PNG {summary.CopiedPngs}개," +
                $" 경고 {summary.Warnings}개, 실패 {summary.Failures}개");

            Directory.CreateDirectory(outputRoot);
            summary.LogPath = Path.Combine(outputRoot, LogFileName);
            File.WriteAllText(summary.LogPath, log.ToString(), new UTF8Encoding(true));
            return summary;
        }

        private static void ProcessAtlas(string atlasPath, string relAtlas, string inputRoot, string outputRoot,
                                         BatchOptions options, IReadOnlyDictionary<string, List<string>> pngsByName,
                                         HashSet<string> usedPngs, HashSet<string> usedFlatNames, BatchSummary summary,
                                         Action<string> log, CancellationToken ct)
        {
            var pages = AtlasParser.Parse(atlasPath);
            if (pages.Count == 0)
            {
                summary.Warnings++;
                log($"  ! {relAtlas}: 페이지가 없음 (아틀라스 파일이 아닌 듯)");
                return;
            }

            var relDir = Path.GetDirectoryName(relAtlas) ?? "";
            var notes = new List<string>();
            string outDir;
            if (options.FlattenFolders)
            {
                var stem = AtlasStem(atlasPath);
                var flatName = stem;
                for (int n = 2; !usedFlatNames.Add(flatName); n++)
                    flatName = $"{stem}_{n}";
                if (flatName != stem)
                {
                    summary.Warnings++;
                    notes.Add($"같은 이름의 아틀라스가 있어 폴더 이름을 {flatName}(으)로 저장");
                }
                outDir = Path.Combine(outputRoot, flatName);
            }
            else
            {
                outDir = Path.Combine(outputRoot, relDir, AtlasStem(atlasPath));
            }
            // Per page, every picture the atlas might mean (usually exactly one).
            var candidates = new List<List<string>?>();
            foreach (var page in pages)
            {
                var found = PagePngLocator.Find(atlasPath, page.ImageFile, pngsByName);
                if (found is null)
                {
                    summary.Warnings++;
                    notes.Add($"페이지 PNG를 찾지 못함: {page.ImageFile}");
                    candidates.Add(null);
                    continue;
                }
                var variants = PagePngLocator.FindVariants(found);
                foreach (var v in variants) usedPngs.Add(v);

                // A same-named texture whose size differs from the atlas page is a
                // different picture, not a variant of this page. If some candidates match
                // the declared size exactly, those are the real ones.
                if (variants.Count > 1 && page.Width > 0 && page.Height > 0)
                {
                    var exact = variants.Where(v => PagePngLocator.ReadPngSize(v) == (page.Width, page.Height)).ToList();
                    if (exact.Count > 0 && exact.Count < variants.Count)
                    {
                        notes.Add($"{page.ImageFile}와 이름이 같지만 크기가 아틀라스({page.Width}x{page.Height})와 다른 텍스처 " +
                                  $"{variants.Count - exact.Count}장은 다른 그림이라 제외");
                        variants = exact;
                    }
                }
                candidates.Add(variants);
            }

            // Several different pictures under one name: output every one of them,
            // in a stable order. The first keeps the plain folder, the rest get _v2, _v3.
            int variantCount = Math.Max(1, candidates.Max(c => c?.Count ?? 0));
            if (variantCount > 1)
            {
                summary.Warnings++;
                notes.Add($"같은 이름의 서로 다른 텍스처가 {variantCount}장 있어 모두 저장 (폴더 {Path.GetFileName(outDir)}, " +
                          $"{string.Join(", ", Enumerable.Range(2, variantCount - 1).Select(n => $"{Path.GetFileName(outDir)}_v{n}"))}). " +
                          "어느 쪽이 원래 짝인지 파일만으로는 알 수 없습니다");
            }

            int atlasParts = 0, atlasCleaned = 0;
            for (int variant = 0; variant < variantCount; variant++)
            {
                var variantDir = variant == 0 ? outDir : $"{outDir}_v{variant + 1}";
                if (variant > 0 && options.FlattenFolders)
                    usedFlatNames.Add(Path.GetFileName(variantDir));
                var namer = new ExportNamer(options.Naming); // one namespace per output folder
                int variantParts = 0;

                for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
                {
                    ct.ThrowIfCancellationRequested();
                    var page = pages[pageIndex];
                    var pageCandidates = candidates[pageIndex];
                    if (pageCandidates is null) continue;
                    var pngPath = pageCandidates[Math.Min(variant, pageCandidates.Count - 1)];

                    var image = RgbaImage.Load(pngPath);
                    if (variant == 0 && page.Width > 0 && page.Height > 0 && (page.Width != image.Width || page.Height != image.Height))
                    {
                        summary.Warnings++;
                        notes.Add($"{page.ImageFile} 크기가 아틀라스({page.Width}x{page.Height})와 PNG({image.Width}x{image.Height})에서 다름 → 아틀라스 크기로 보정해 처리 (화질이 약간 떨어질 수 있음)");
                    }

                    var extraction = AtlasExtractor.ExtractPage(image, page, options.Extract);
                    foreach (var part in extraction.Parts)
                    {
                        var rel = namer.Next(part.Region.ExportName, $"part_{variantParts + 1:D2}");
                        var outPath = Path.Combine(variantDir, rel + ".png");
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                        File.WriteAllBytes(outPath, part.Image.EncodePng());
                        atlasParts++;
                        variantParts++;
                        if (part.OverlapPixelsRemoved > 0) atlasCleaned++;
                    }

                    if (variant == 0 && extraction.KeptUncleanedRegions.Count > 0)
                    {
                        summary.Warnings++;
                        notes.Add($"겹침 정리 시 통째로 사라질 리전은 정리 없이 그대로 사용: {string.Join(", ", extraction.KeptUncleanedRegions)}");
                    }

                    if (variant == 0 && extraction.SkippedRegions.Count > 0)
                    {
                        summary.Warnings++;
                        notes.Add($"범위를 벗어나 제외: {string.Join(", ", extraction.SkippedRegions)}");
                    }
                    summary.Pages++;
                }
            }

            summary.Atlases++;
            summary.Parts += atlasParts;
            summary.CleanedRegions += atlasCleaned;

            var cleanedNote = atlasCleaned > 0 ? $", 겹침 정리 {atlasCleaned}개" : "";
            log($"  {relAtlas} → {atlasParts}개{cleanedNote}");
            foreach (var note in notes)
                log($"      ! {note}");
        }

        // A skeleton with extra skins shows some slots empty in an editor until one of
        // those skins is picked, which looks like missing parts. Say so in the log.
        private static void ReportSkins(IEnumerable<string> skelPaths, string inputRoot, BatchSummary summary,
                                        Action<string> log, CancellationToken ct)
        {
            var found = new List<string>();
            int unreadable = 0;
            foreach (var skel in skelPaths.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var info = SkelSkinReader.TryRead(skel);
                if (info is null) { unreadable++; continue; }
                if (info.ExtraSkins.Count == 0) continue;

                var rel = Path.GetRelativePath(inputRoot, skel);
                var names = string.Join(", ", info.ExtraSkins.Select(s => s.Name));
                var line = $"  {rel}: 스킨 {names}";
                if (info.SlotsOnlyInExtraSkins.Count > 0)
                    line += $" / 기본 스킨에는 없고 이 스킨에만 있는 슬롯: {string.Join(", ", info.SlotsOnlyInExtraSkins)}";
                found.Add(line);
                summary.SkinSkeletons.Add(rel);
            }

            if (found.Count == 0 && unreadable == 0) return;
            log("");
            if (found.Count > 0)
            {
                summary.Warnings++;
                log($"! 기본 외 스킨이 있는 스켈레톤 {found.Count}개 - Spine에서 몸통 같은 부위가 비어 보이면 스킨을 선택해 보세요:");
                foreach (var line in found) log(line);
            }
            if (unreadable > 0)
                log($"  (Spine 3.8 형식이 아니거나 읽지 못한 .skel {unreadable}개는 스킨을 확인하지 못함)");
        }

        // "foo.atlas" -> "foo", "foo.atlas.txt" -> "foo"
        private static string AtlasStem(string atlasPath)
        {
            var name = Path.GetFileName(atlasPath);
            foreach (var ext in new[] { ".atlas.txt", ".atlas" })
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return name[..^ext.Length];
            return Path.GetFileNameWithoutExtension(name);
        }
    }
}
