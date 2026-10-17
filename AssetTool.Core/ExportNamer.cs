using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace asset_tool
{
    public enum NamingMode
    {
        KeepFolders, // "0103/images_air/fx" -> 0103\images_air\fx.png (what Spine expects when re-packing)
        Flatten,     // -> 0103_images_air_fx.png
        LeafOnly     // -> fx.png
    }

    // Turns region names into safe, unique relative file paths (no extension)
    // for one output folder. Never lets two parts overwrite each other just
    // because they ended up with the same name.
    public sealed class ExportNamer
    {
        private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

        private readonly NamingMode _mode;
        private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

        public ExportNamer(NamingMode mode) => _mode = mode;

        public string Next(string name, string fallback)
        {
            var segments = name.Split('/', '\\')
                               .Select(SanitizeSegment)
                               .Where(s => s.Length > 0)
                               .ToList();
            if (segments.Count == 0)
                segments.Add(SanitizeSegment(fallback));

            var baseName = _mode switch
            {
                NamingMode.KeepFolders => Path.Combine(segments.ToArray()),
                NamingMode.Flatten => string.Join("_", segments),
                _ => segments[^1]
            };

            var candidate = baseName;
            int suffix = 2;
            while (!_used.Add(candidate))
                candidate = $"{baseName}_{suffix++}";
            return candidate;
        }

        private static string SanitizeSegment(string segment)
        {
            // Trailing dots are dropped by Windows anyway; trimming them also turns
            // "." / ".." into nothing, so a name can never climb out of the folder.
            var cleaned = string.Join("_", segment.Split(InvalidChars)).Trim().TrimEnd('.');
            if (ReservedNames.Contains(cleaned)) return "_" + cleaned;
            return cleaned;
        }
    }
}
