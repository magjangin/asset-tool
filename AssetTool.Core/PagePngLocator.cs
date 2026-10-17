using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace asset_tool
{
    // Finds the PNG an atlas page refers to. Asset rippers rarely keep the two
    // side by side - AssetStudio puts atlases in TextAsset\ and textures in
    // Texture2D\ - so besides the atlas's own folder we look in sibling folders,
    // and (batch mode) anywhere under the input root, preferring the candidate
    // whose path looks most like the atlas's (TextAsset\1_rampage\skin1 pairs
    // with Texture2D\1_rampage\skin1, not ...\skin2).
    public static class PagePngLocator
    {
        // Single-file mode: atlas folder, its subfolders, its parent and the
        // parent's other subfolders (one level deep each).
        public static string? FindNear(string atlasPath, string imageFile)
        {
            var atlasDir = Path.GetDirectoryName(Path.GetFullPath(atlasPath)) ?? "";
            var direct = Path.Combine(atlasDir, imageFile);
            if (File.Exists(direct)) return Path.GetFullPath(direct);

            var fileName = Path.GetFileName(imageFile);
            var dirs = new List<string> { atlasDir };
            dirs.AddRange(SafeSubdirectories(atlasDir));
            var parent = Path.GetDirectoryName(atlasDir);
            if (parent != null)
            {
                dirs.Add(parent);
                dirs.AddRange(SafeSubdirectories(parent));
            }

            var candidates = dirs.Select(d => Path.Combine(d, fileName)).Where(File.Exists).ToList();
            return PickClosest(atlasDir, candidates);
        }

        // Batch mode: `pngsByName` indexes every PNG under the input root by file name.
        public static string? Find(string atlasPath, string imageFile, IReadOnlyDictionary<string, List<string>> pngsByName)
        {
            var atlasDir = Path.GetDirectoryName(Path.GetFullPath(atlasPath)) ?? "";
            var direct = Path.Combine(atlasDir, imageFile);
            if (File.Exists(direct)) return Path.GetFullPath(direct);

            return pngsByName.TryGetValue(Path.GetFileName(imageFile), out var candidates)
                ? PickClosest(atlasDir, candidates)
                : null;
        }

        // A texture can exist twice under one name (AssetStudio keeps both and renames
        // the clash to "name_#123.png"; which of the two keeps the plain name varies from
        // run to run). They are different pictures - e.g. a white silhouette and its
        // pink-glow version - and nothing in the files says which one the atlas means.
        // Returns `primary` plus every same-folder "<name>_#<digits>" sibling with
        // different content, ordered by content hash so the order never depends on
        // extraction order. Identical copies are dropped.
        public static List<string> FindVariants(string primary)
        {
            var dir = Path.GetDirectoryName(primary) ?? "";
            var stem = Path.GetFileNameWithoutExtension(primary);
            var ext = Path.GetExtension(primary);
            var pattern = new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(stem) + @"_#\d+" + System.Text.RegularExpressions.Regex.Escape(ext) + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var files = new List<string> { primary };
            try
            {
                files.AddRange(Directory.EnumerateFiles(dir).Where(f => pattern.IsMatch(Path.GetFileName(f))));
            }
            catch (Exception) { /* unreadable folder: just the primary */ }

            var byHash = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in files)
            {
                var hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(f)));
                if (!byHash.ContainsKey(hash)) byHash[hash] = f;
            }
            return byHash.Values.ToList();
        }

        // A same-named texture whose size differs from the atlas page is a different picture,
        // not a variant of this page. If some candidates match the declared size exactly those
        // are the real ones; otherwise (e.g. every candidate was re-sampled) keep them all.
        public static List<string> FilterByPageSize(List<string> variants, int pageWidth, int pageHeight)
        {
            if (variants.Count <= 1 || pageWidth <= 0 || pageHeight <= 0) return variants;
            var exact = variants.Where(v => ReadPngSize(v) == (pageWidth, pageHeight)).ToList();
            return exact.Count > 0 && exact.Count < variants.Count ? exact : variants;
        }

        // Width/height straight from the PNG header (no decode); null if it isn't a PNG.
        public static (int Width, int Height)? ReadPngSize(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var header = new byte[24];
                if (fs.Read(header, 0, 24) < 24) return null;
                // 8-byte signature, then the IHDR chunk: length(4) "IHDR"(4) width(4) height(4).
                if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47) return null;
                int w = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                int h = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                return (w, h);
            }
            catch (Exception) { return null; }
        }

        private static string? PickClosest(string atlasDir, List<string> candidates)
        {
            if (candidates.Count == 0) return null;

            var atlasParts = Split(atlasDir);
            return candidates
                .OrderByDescending(c => Similarity(atlasParts, Split(Path.GetDirectoryName(c) ?? "")))
                .ThenBy(c => c.Length)
                .First();
        }

        // Shared leading folders weigh most (same tree), then shared trailing
        // folders (same relative spot inside a parallel tree).
        private static int Similarity(string[] a, string[] b)
        {
            int prefix = 0;
            while (prefix < a.Length && prefix < b.Length &&
                   string.Equals(a[prefix], b[prefix], StringComparison.OrdinalIgnoreCase))
                prefix++;

            int suffix = 0;
            while (suffix < a.Length - prefix && suffix < b.Length - prefix &&
                   string.Equals(a[^(suffix + 1)], b[^(suffix + 1)], StringComparison.OrdinalIgnoreCase))
                suffix++;

            return prefix * 100 + suffix;
        }

        private static string[] Split(string path) =>
            path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

        private static IEnumerable<string> SafeSubdirectories(string dir)
        {
            try { return Directory.GetDirectories(dir); }
            catch (Exception) { return Array.Empty<string>(); }
        }
    }
}
