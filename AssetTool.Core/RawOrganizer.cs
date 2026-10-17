using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace asset_tool
{
    public sealed record SkelPlacement(string Skel, string Folder, string Reason, string OtherCandidates);

    public sealed class RawOrganizeResult
    {
        public int Folders;
        public int LooseFiles;
        public List<SkelPlacement> Skeletons { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    // Rearranges a flat extraction folder (atlases, skeletons, page PNGs side by side) so it
    // has exactly the same sub-folders as the split output: one folder per atlas, holding
    //   <atlas>.atlas, its page PNG(s), and the skeletons that use it.
    // A "<atlas>_v2" folder (a second, different picture sharing the page name) gets the
    // matching page picture, stored under the name the atlas refers to.
    // Whatever belongs to no atlas (unused textures, ...) stays loose in the folder.
    public static class RawOrganizer
    {
        public const string PlacementCsvName = "스켈_짝맞춤.csv";

        public static RawOrganizeResult Organize(string rawDir, string partsDir)
        {
            var result = new RawOrganizeResult();
            var wanted = Directory.GetDirectories(partsDir).Select(Path.GetFileName).Select(n => n!)
                                  .OrderBy(n => n, StringComparer.Ordinal).ToList();

            var atlasFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // stem -> file name
            foreach (var file in Directory.GetFiles(rawDir))
            {
                var name = Path.GetFileName(file);
                foreach (var ext in new[] { ".atlas.txt", ".atlas" })
                    if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        atlasFiles[name[..^ext.Length]] = name;
                        break;
                    }
            }

            var atlasPages = new Dictionary<string, List<AtlasPage>>(StringComparer.OrdinalIgnoreCase);
            var atlasRegionNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (stem, file) in atlasFiles)
            {
                try
                {
                    var pages = AtlasParser.Parse(Path.Combine(rawDir, file));
                    atlasPages[stem] = pages;
                    atlasRegionNames[stem] = pages.SelectMany(p => p.Regions).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"{file}: 아틀라스를 읽지 못함 - {ex.Message}");
                }
            }

            var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // top-level files copied into a folder

            // 1) atlas + page pictures into one folder per split folder
            foreach (var folder in wanted)
            {
                var m = Regex.Match(folder, @"^(.+)_v(\d+)$");
                string stem = folder;
                int variant = 0;
                if (m.Success && atlasPages.ContainsKey(m.Groups[1].Value))
                {
                    stem = m.Groups[1].Value;
                    variant = int.Parse(m.Groups[2].Value) - 1;
                }
                if (!atlasPages.TryGetValue(stem, out var pages))
                {
                    result.Warnings.Add($"{folder}: 같은 이름의 아틀라스가 없음");
                    continue;
                }

                var dest = Path.Combine(rawDir, folder);
                Directory.CreateDirectory(dest);
                var atlasName = atlasFiles[stem];
                File.Copy(Path.Combine(rawDir, atlasName), Path.Combine(dest, atlasName), overwrite: true);
                consumed.Add(Path.Combine(rawDir, atlasName));

                foreach (var page in pages)
                {
                    var primary = Path.Combine(rawDir, page.ImageFile);
                    if (!File.Exists(primary))
                    {
                        result.Warnings.Add($"{folder}: 페이지 PNG를 찾지 못함 - {page.ImageFile}");
                        continue;
                    }
                    var candidates = PagePngLocator.FilterByPageSize(PagePngLocator.FindVariants(primary), page.Width, page.Height);
                    var source = candidates[Math.Min(variant, candidates.Count - 1)];
                    File.Copy(source, Path.Combine(dest, page.ImageFile), overwrite: true); // name the atlas refers to
                    consumed.Add(source);
                }
            }

            // 2) skeletons: the same-named atlas folder, else the atlas whose region names it uses most
            var wantedSet = wanted.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(rawDir, "*.skel").OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                var skelStem = Path.GetFileNameWithoutExtension(name);
                var strings = AsciiStrings(File.ReadAllBytes(file));

                var ranked = atlasRegionNames
                    .Select(kv => (Atlas: kv.Key, Score: kv.Value.Count(strings.Contains)))
                    .Where(t => t.Score > 0)
                    .OrderByDescending(t => t.Score).ThenBy(t => t.Atlas, StringComparer.Ordinal)
                    .ToList();

                string target, reason;
                if (atlasFiles.ContainsKey(skelStem) && wantedSet.Contains(skelStem)) { target = skelStem; reason = "이름 같음"; }
                else if (ranked.Count > 0) { target = ranked[0].Atlas; reason = $"내용 일치 {ranked[0].Score}개"; }
                else { target = ""; reason = "짝 못 찾음"; }

                result.Skeletons.Add(new SkelPlacement(name, target, reason, string.Join(",", ranked.Skip(1).Take(3).Select(t => t.Atlas))));
                if (target.Length > 0 && wantedSet.Contains(target))
                {
                    File.Copy(file, Path.Combine(rawDir, target, name), overwrite: true);
                    consumed.Add(file);
                }
            }

            // 3) the top-level originals now live in a folder; drop only those copies
            foreach (var path in consumed)
                if (File.Exists(path)) File.Delete(path);

            result.Folders = Directory.GetDirectories(rawDir).Length;
            result.LooseFiles = Directory.GetFiles(rawDir).Length;
            return result;
        }

        public static void WritePlacementCsv(string path, IEnumerable<SkelPlacement> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("스켈,넣은 폴더,근거,다른 후보(일치 많은 순)");
            foreach (var r in rows)
                sb.AppendLine(string.Join(",", Csv(r.Skel), Csv(r.Folder), Csv(r.Reason), Csv(r.OtherCandidates)));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string Csv(string v) => v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

        // Printable-ASCII runs (>= 3 chars) of a binary file: Spine's .skel stores its image
        // and attachment names as plain text, which is enough to see which atlas it uses.
        private static HashSet<string> AsciiStrings(byte[] data)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            int start = -1;
            for (int i = 0; i <= data.Length; i++)
            {
                bool printable = i < data.Length && data[i] >= 0x20 && data[i] <= 0x7E;
                if (printable) { if (start < 0) start = i; }
                else if (start >= 0)
                {
                    if (i - start >= 3) set.Add(Encoding.ASCII.GetString(data, start, i - start));
                    start = -1;
                }
            }
            return set;
        }
    }
}
