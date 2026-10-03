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
