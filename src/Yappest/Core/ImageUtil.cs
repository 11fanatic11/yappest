using System.IO;
using SkiaSharp;

namespace Yappest.Core;

public static class ImageUtil
{
    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".ico" };

    /// <summary>Decode any common image (incl. WebP) and save a square PNG thumbnail. Returns the file name inside IconsDir.</summary>
    public static string? ImportIcon(string sourcePath, int size = 128)
    {
        try
        {
            using var bmp = SKBitmap.Decode(sourcePath);
            if (bmp == null) return null;

            using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            // centre-crop to a square
            int side = Math.Min(bmp.Width, bmp.Height);
            var src = new SKRect((bmp.Width - side) / 2f, (bmp.Height - side) / 2f,
                                 (bmp.Width + side) / 2f, (bmp.Height + side) / 2f);
            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            using var image = SKImage.FromBitmap(bmp);
            canvas.DrawImage(image, src, new SKRect(0, 0, size, size), paint);

            using var snap = surface.Snapshot();
            using var png = snap.Encode(SKEncodedImageFormat.Png, 100);
            var name = Guid.NewGuid().ToString("N") + ".png";
            File.WriteAllBytes(Path.Combine(AppPaths.IconsDir, name), png.ToArray());
            return name;
        }
        catch (Exception ex)
        {
            Log.Error("icon import " + sourcePath, ex);
            return null;
        }
    }

    public static string IconPath(string? name) =>
        string.IsNullOrEmpty(name) ? "" : Path.Combine(AppPaths.IconsDir, name);

    /// <summary>PNG data URL for the Stream Deck (72px).</summary>
    public static string? ToDataUrl(string? iconName, int size = 72)
    {
        try
        {
            var p = IconPath(iconName);
            if (!File.Exists(p)) return null;
            using var bmp = SKBitmap.Decode(p);
            using var scaled = bmp.Resize(new SKImageInfo(size, size), SKFilterQuality.High);
            using var img = SKImage.FromBitmap(scaled);
            using var png = img.Encode(SKEncodedImageFormat.Png, 90);
            return "data:image/png;base64," + Convert.ToBase64String(png.ToArray());
        }
        catch { return null; }
    }
}
