using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace asset_tool
{
    // The frames someone picked from a contact sheet, plus everything needed to render
    // them again (e.g. at another size) without looking at the sheet.
    public sealed class SpineRecipe
    {
        public string Skel { get; set; } = "";
        public string PartsDir { get; set; } = "";
        public double ImportScale { get; set; } = 0.5;
        public string SpineVersion { get; set; } = "3.8.99";
        public int ExportScalePercent { get; set; } = 100;
        public bool MaxBounds { get; set; } = true;
        // Frames are rendered on this time grid (time = frame / RenderFps).
        public double RenderFps { get; set; } = 60;
        public List<SpineRecipeFrame> Frames { get; set; } = new();

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
        }

        public static SpineRecipe Load(string path) =>
            JsonSerializer.Deserialize<SpineRecipe>(File.ReadAllText(path, Encoding.UTF8), Json)
            ?? throw new InvalidDataException("레시피를 읽을 수 없습니다.");
    }

    public sealed class SpineRecipeFrame
    {
        // Output file name without extension.
        public string Name { get; set; } = "";
        public string Animation { get; set; } = "";
        // Seconds from the start of the animation.
        public double Time { get; set; }
    }
}
