using System;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace asset_tool
{
    // Straight-alpha (unpremultiplied) RGBA8888 pixels held in managed memory.
    // All processing works on this instead of SKBitmap.GetPixel/SetPixel:
    // it's much faster (no native call per pixel), safe to use off the UI
    // thread, and lossless - decoding into Skia's default premultiplied format
    // rounds away the color of nearly transparent pixels (e.g. an alpha-3
    // pixel (123,45,67) came back as (85,85,85)).
    public sealed class RgbaImage
    {
        public int Width { get; }
        public int Height { get; }

        // R, G, B, A per pixel, row-major, stride = Width * 4.
        public byte[] Pixels { get; }

        public RgbaImage(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), $"잘못된 이미지 크기: {width}x{height}");
            Width = width;
            Height = height;
            Pixels = new byte[width * height * 4];
        }

        // Bilinear resample done on alpha-weighted colors, so transparent pixels
        // (whose RGB is meaningless) don't bleed dark/white fringes into edges.
        public RgbaImage ResizedTo(int newWidth, int newHeight)
        {
            if (newWidth == Width && newHeight == Height) return this;
            var dst = new RgbaImage(newWidth, newHeight);
            double sx = (double)Width / newWidth, sy = (double)Height / newHeight;
            for (int y = 0; y < newHeight; y++)
            {
                double fy = Math.Clamp((y + 0.5) * sy - 0.5, 0, Height - 1);
                int y0 = (int)fy, y1 = Math.Min(y0 + 1, Height - 1);
                double wy = fy - y0;
                for (int x = 0; x < newWidth; x++)
                {
                    double fx = Math.Clamp((x + 0.5) * sx - 0.5, 0, Width - 1);
                    int x0 = (int)fx, x1 = Math.Min(x0 + 1, Width - 1);
                    double wx = fx - x0;

                    double r = 0, g = 0, b = 0, a = 0;
                    void Add(int px, int py, double w)
                    {
                        int i = (py * Width + px) * 4;
                        double al = Pixels[i + 3] * w;
                        r += Pixels[i] * al; g += Pixels[i + 1] * al; b += Pixels[i + 2] * al; a += al;
                    }
                    Add(x0, y0, (1 - wx) * (1 - wy));
                    Add(x1, y0, wx * (1 - wy));
                    Add(x0, y1, (1 - wx) * wy);
                    Add(x1, y1, wx * wy);

                    int o = (y * newWidth + x) * 4;
                    if (a > 0)
                    {
                        dst.Pixels[o] = (byte)Math.Clamp(Math.Round(r / a), 0, 255);
                        dst.Pixels[o + 1] = (byte)Math.Clamp(Math.Round(g / a), 0, 255);
                        dst.Pixels[o + 2] = (byte)Math.Clamp(Math.Round(b / a), 0, 255);
                        dst.Pixels[o + 3] = (byte)Math.Clamp(Math.Round(a), 0, 255);
                    }
                }
            }
            return dst;
        }

        public byte AlphaAt(int x, int y) => Pixels[(y * Width + x) * 4 + 3];

        public void ClearPixel(int x, int y) => Array.Clear(Pixels, (y * Width + x) * 4, 4);

        public int CountVisiblePixels()
        {
            int n = 0;
            for (int i = 3; i < Pixels.Length; i += 4)
                if (Pixels[i] != 0) n++;
            return n;
        }

        public static RgbaImage Load(string path) => Decode(File.ReadAllBytes(path));

        public static RgbaImage Decode(byte[] encoded)
        {
            using var data = SKData.CreateCopy(encoded);
            using var codec = SKCodec.Create(data)
                ?? throw new InvalidDataException("이미지 형식을 해석할 수 없습니다.");

            // No color space on the target info -> no color conversion, raw values kept.
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var image = new RgbaImage(info.Width, info.Height);

            var handle = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);
            try
            {
                var result = codec.GetPixels(info, handle.AddrOfPinnedObject());
                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                    throw new InvalidDataException($"이미지 디코딩 실패: {result}");
            }
            finally
            {
                handle.Free();
            }

            return image;
        }

        public byte[] EncodePng()
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var image = SKImage.FromPixelCopy(info, Pixels, Width * 4)
                ?? throw new InvalidOperationException("PNG 인코딩용 이미지를 만들 수 없습니다.");
            using var data = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("PNG 인코딩 실패");
            return data.ToArray();
        }

        public RgbaImage Crop(int x, int y, int width, int height)
        {
            if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > Width || y + height > Height)
                throw new ArgumentOutOfRangeException(nameof(x), $"잘라낼 영역이 이미지를 벗어남: {x},{y} {width}x{height}");

            var result = new RgbaImage(width, height);
            for (int row = 0; row < height; row++)
                Buffer.BlockCopy(Pixels, ((y + row) * Width + x) * 4, result.Pixels, row * width * 4, width * 4);
            return result;
        }

        // Exact pixel remap (no resampling). Texture packers rotate regions when
        // packing; rotating clockwise by the declared amount restores them
        // (verified on Muse Dash atlases: scrap_3/scrap_4 are stored unrotated in
        // 0103.atlas and with "rotate: true" in 0109_road.atlas, and a +90 turn of
        // the latter matches the former).
        public RgbaImage RotateClockwise(int degrees)
        {
            degrees = ((degrees % 360) + 360) % 360;
            if (degrees == 0) return Copy();
            if (degrees != 90 && degrees != 180 && degrees != 270)
                throw new ArgumentOutOfRangeException(nameof(degrees), $"지원하지 않는 회전 각도: {degrees}");

            bool swap = degrees != 180;
            var result = swap ? new RgbaImage(Height, Width) : new RgbaImage(Width, Height);
            var src = Pixels;
            var dst = result.Pixels;

            for (int y = 0; y < result.Height; y++)
            {
                for (int x = 0; x < result.Width; x++)
                {
                    int sx, sy;
                    switch (degrees)
                    {
                        case 90: sx = y; sy = Height - 1 - x; break;
                        case 180: sx = Width - 1 - x; sy = Height - 1 - y; break;
                        default: sx = Width - 1 - y; sy = x; break; // 270
                    }
                    Buffer.BlockCopy(src, (sy * Width + sx) * 4, dst, (y * result.Width + x) * 4, 4);
                }
            }

            return result;
        }

        // Places this image on a transparent canvas of the given size, clipping
        // anything that falls outside.
        public RgbaImage PlacedOnCanvas(int canvasWidth, int canvasHeight, int left, int top)
        {
            var result = new RgbaImage(canvasWidth, canvasHeight);
            int x0 = Math.Max(0, left), x1 = Math.Min(canvasWidth, left + Width);
            if (x1 <= x0) return result;

            for (int y = 0; y < Height; y++)
            {
                int dy = top + y;
                if (dy < 0 || dy >= canvasHeight) continue;
                Buffer.BlockCopy(Pixels, (y * Width + (x0 - left)) * 4, result.Pixels, (dy * canvasWidth + x0) * 4, (x1 - x0) * 4);
            }

            return result;
        }

        public RgbaImage Copy()
        {
            var result = new RgbaImage(Width, Height);
            Buffer.BlockCopy(Pixels, 0, result.Pixels, 0, Pixels.Length);
            return result;
        }
    }
}
