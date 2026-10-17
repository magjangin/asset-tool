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
    }

    // Walks a folder tree (e.g. an AssetStudio export with TextAsset\ and
    // Texture2D\), splits every .atlas it finds and writes the parts to
    //   <output>\<atlas's folder relative to input>\<atlas name>\<region>.png
    // Mirroring the relative folder keeps same-named atlases apart (skin1 vs
    // skin2). A failing atlas is logged and skipped; it never stops the run.
    public static class BatchProcessor
    {
        public const string LogFileName = "_split_log.txt";

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
                    ProcessAtlas(atlasPath, relAtlas, inputRoot, outputRoot, options, pngsByName, usedPngs, summary, Log, ct);
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
                        var dest = Path.Combine(outputRoot, rel);
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
                                         HashSet<string> usedPngs, BatchSummary summary, Action<string> log,
                                         CancellationToken ct)
        {
            var pages = AtlasParser.Parse(atlasPath);
            if (pages.Count == 0)
            {
                summary.Warnings++;
                log($"  ! {relAtlas}: 페이지가 없음 (아틀라스 파일이 아닌 듯)");
                return;
            }

            var relDir = Path.GetDirectoryName(relAtlas) ?? "";
            var outDir = Path.Combine(outputRoot, relDir, AtlasStem(atlasPath));
            var namer = new ExportNamer(options.Naming); // one namespace per atlas folder
            var notes = new List<string>();
            int atlasParts = 0, atlasCleaned = 0;

            foreach (var page in pages)
            {
                ct.ThrowIfCancellationRequested();

                var pngPath = PagePngLocator.Find(atlasPath, page.ImageFile, pngsByName);
                if (pngPath is null)
                {
                    summary.Warnings++;
                    notes.Add($"페이지 PNG를 찾지 못함: {page.ImageFile}");
                    continue;
                }
                usedPngs.Add(pngPath);

                var image = RgbaImage.Load(pngPath);
                if (page.Width > 0 && page.Height > 0 && (page.Width != image.Width || page.Height != image.Height))
                {
                    summary.Warnings++;
                    notes.Add($"{page.ImageFile} 크기가 아틀라스({page.Width}x{page.Height})와 PNG({image.Width}x{image.Height})에서 다름");
                }

                var extraction = AtlasExtractor.ExtractPage(image, page, options.Extract);
                foreach (var part in extraction.Parts)
                {
                    var rel = namer.Next(part.Region.ExportName, $"part_{atlasParts + 1:D2}");
                    var outPath = Path.Combine(outDir, rel + ".png");
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    File.WriteAllBytes(outPath, part.Image.EncodePng());
                    atlasParts++;
                    if (part.OverlapPixelsRemoved > 0) atlasCleaned++;
                }

                if (extraction.SkippedRegions.Count > 0)
                {
                    summary.Warnings++;
                    notes.Add($"범위를 벗어나 제외: {string.Join(", ", extraction.SkippedRegions)}");
                }
                summary.Pages++;
            }

            summary.Atlases++;
            summary.Parts += atlasParts;
            summary.CleanedRegions += atlasCleaned;

            var cleanedNote = atlasCleaned > 0 ? $", 겹침 정리 {atlasCleaned}개" : "";
            log($"  {relAtlas} → {atlasParts}개{cleanedNote}");
            foreach (var note in notes)
                log($"      ! {note}");
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
