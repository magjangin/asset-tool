using System.ComponentModel;
using ModelContextProtocol.Server;

namespace asset_tool.Mcp;

[McpServerToolType]
public static class RepackTools
{
    [McpServerTool(Name = "repack_parts", Destructive = false, Idempotent = true)]
    [Description("Pack loose part PNGs back into a Spine/libgdx atlas (<atlasName>.atlas + page PNGs). " +
                 "Region names come from each PNG's path relative to partsDir (folders become 'a/b/c', no extension), " +
                 "so a folder produced by split_atlas_folder with naming='keep_folders' round-trips to the original names. " +
                 "Transparent borders are stripped and recorded as orig/offset like Spine's packer does. Existing files in outputDir may be overwritten.")]
    public static async Task<object> RepackParts(
        [Description("Folder with the part PNGs (searched recursively), e.g. <split output>\\<atlas folder>\\<atlas name>.")] string partsDir,
        [Description("Folder to write <atlasName>.atlas and the page PNGs into (created if missing). Must not be inside partsDir.")] string outputDir,
        [Description("Atlas and page file base name. Default: the name of partsDir.")] string? atlasName = null,
        [Description("Largest page width/height in pixels. Default 2048.")] int maxPageSize = 2048,
        [Description("Empty pixels around each part. Default 2.")] int padding = 2,
        [Description("Cut transparent borders and record orig/offset. Default true.")] bool stripWhitespace = true,
        [Description("Round page sizes up to a power of two. Default true.")] bool powerOfTwo = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Directory.Exists(partsDir))
                return new { ok = false, error = $"파츠 폴더가 없습니다: {partsDir}" };

            var parts = Path.TrimEndingDirectorySeparator(Path.GetFullPath(partsDir));
            var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDir));
            if (output.Equals(parts, StringComparison.OrdinalIgnoreCase) ||
                output.StartsWith(parts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return new { ok = false, error = "출력 폴더가 파츠 폴더 안이거나 같습니다. 다른 폴더를 지정해 주세요." };

            var name = string.IsNullOrWhiteSpace(atlasName) ? new DirectoryInfo(parts).Name : atlasName.Trim();
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return new { ok = false, error = $"atlasName에 쓸 수 없는 문자가 있습니다: {name}" };

            var files = Directory.EnumerateFiles(parts, "*.png", SearchOption.AllDirectories)
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0)
                return new { ok = false, error = $"PNG 파일이 없습니다: {partsDir}" };

            var result = await Task.Run(() =>
            {
                var inputs = new List<PackInput>(files.Count);
                foreach (var f in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rel = Path.GetRelativePath(parts, f);
                    var regionName = rel[..^Path.GetExtension(rel).Length].Replace(Path.DirectorySeparatorChar, '/');
                    inputs.Add(new PackInput { Name = regionName, Image = RgbaImage.Load(f) });
                }

                var pages = AtlasPacker.Pack(inputs, name, new PackOptions
                {
                    MaxPageSize = maxPageSize,
                    Padding = padding,
                    StripWhitespace = stripWhitespace,
                    PowerOfTwo = powerOfTwo
                });

                Directory.CreateDirectory(output);
                foreach (var page in pages)
                    File.WriteAllBytes(Path.Combine(output, page.FileName), page.Image.EncodePng());
                var atlasPath = Path.Combine(output, name + ".atlas");
                File.WriteAllText(atlasPath, AtlasPacker.WriteAtlasText(pages), new System.Text.UTF8Encoding(false));
                return (pages, atlasPath, inputs.Count);
            }, cancellationToken);

            return new
            {
                ok = true,
                atlasPath = result.atlasPath,
                regionCount = result.Item3,
                pageCount = result.pages.Count,
                pages = result.pages.Select(p => new { file = p.FileName, width = p.Image.Width, height = p.Image.Height, regions = p.Regions.Count })
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
}
