using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;

namespace asset_tool
{
    // Crop (if any) limits the drawn part of the image, e.g. to what is actually painted.
    public sealed record SheetCell(string Label, string ImagePath, SKRectI? Crop = null);

    // One labelled line of frames (one animation).
    public sealed record SheetRow(string Title, string Subtitle, IReadOnlyList<SheetCell> Cells);

    // Lays frame PNGs out in rows so a person can pick frames by looking at one picture.
    public static class ContactSheet
    {
        private const int LabelWidth = 150;
        private const int TextHeight = 14;
        private const int Pad = 4;

        public static byte[] Render(IReadOnlyList<SheetRow> rows, int cellWidth, int cellHeight)
        {
            int columns = 0;
            foreach (var r in rows) columns = Math.Max(columns, r.Cells.Count);
            int rowHeight = cellHeight + TextHeight + Pad * 2;
            int width = LabelWidth + columns * (cellWidth + Pad) + Pad;
            int height = Math.Max(1, rows.Count) * rowHeight;

            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(0xD6, 0xD6, 0xDC)); // mid grey: white fx lines and dark shadows both stay visible

            using var font = new SKFont { Size = 12 };
            using var bold = new SKFont { Size = 13, Embolden = true };
            using var text = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            using var dim = new SKPaint { Color = new SKColor(0x40, 0x40, 0x48), IsAntialias = true };
            using var stripe = new SKPaint { Color = new SKColor(0xC8, 0xC8, 0xD0) };
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

            for (int r = 0; r < rows.Count; r++)
            {
                int top = r * rowHeight;
                if (r % 2 == 1) canvas.DrawRect(0, top, width, rowHeight, stripe);
                canvas.DrawText(Fit(rows[r].Title, 22), Pad, top + 18, SKTextAlign.Left, bold, text);
                canvas.DrawText(Fit(rows[r].Subtitle, 26), Pad, top + 34, SKTextAlign.Left, font, dim);

                for (int c = 0; c < rows[r].Cells.Count; c++)
                {
                    int x = LabelWidth + c * (cellWidth + Pad);
                    using var image = SKImage.FromEncodedData(rows[r].Cells[c].ImagePath);
                    if (image is not null)
                    {
                        var src = rows[r].Cells[c].Crop is { } crop ? SKRect.Create(crop.Left, crop.Top, crop.Width, crop.Height)
                                                                    : SKRect.Create(image.Width, image.Height);
                        float scale = Math.Min(cellWidth / src.Width, cellHeight / src.Height);
                        float w = src.Width * scale, h = src.Height * scale;
                        var dest = new SKRect(x + (cellWidth - w) / 2, top + Pad + TextHeight + (cellHeight - h) / 2,
                                              x + (cellWidth + w) / 2, top + Pad + TextHeight + (cellHeight + h) / 2);
                        canvas.DrawImage(image, src, dest, sampling, null);
                    }
                    canvas.DrawText(rows[r].Cells[c].Label, x + 2, top + Pad + 11, SKTextAlign.Left, font, text);
                }
            }

            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        private static string Fit(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

        // Smallest rectangle holding every pixel with alpha above the threshold, or null if empty.
        public static SKRectI? OpaqueBounds(string path, byte threshold = 8)
        {
            using var bmp = SKBitmap.Decode(path);
            if (bmp is null) return null;
            var bytes = bmp.GetPixelSpan();
            int w = bmp.Width, h = bmp.Height, bpp = bmp.BytesPerPixel;
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * bmp.RowBytes;
                for (int x = 0; x < w; x++)
                {
                    if (bytes[row + x * bpp + 3] <= threshold) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    maxY = y;
                }
            }
            return maxX < 0 ? null : new SKRectI(minX, minY, maxX + 1, maxY + 1);
        }

        public static (int Width, int Height) ImageSize(string path)
        {
            using var codec = SKCodec.Create(path);
            return codec is null ? (0, 0) : (codec.Info.Width, codec.Info.Height);
        }
    }
}
