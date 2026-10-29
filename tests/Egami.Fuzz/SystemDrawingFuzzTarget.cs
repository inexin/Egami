using System.Runtime.InteropServices;

using Egami.SystemDrawing;
using Egami.SystemDrawing.Imaging;

using SdRectangle = System.Drawing.Rectangle;

namespace Egami.Fuzz;

/// <summary>
/// The sysdrawing fuzz target (step 2.13 task 34): Image.FromStream on any file, then, for an image of at most a million pixels,
/// the calls drop-in code makes on what it loaded: GetPixel, LockBits in 32-bit ARGB, Clone, RotateFlip, a Graphics drawing the
/// image scaled, and Save as PNG. Only the exceptions System.Drawing documents for a bad file may come out of the load.
/// </summary>
internal static class SystemDrawingFuzzTarget
{
    public static void Run(byte[] data)
    {
        Image image;
        try
        {
            image = Image.FromStream(new MemoryStream(data));
        }
        catch (Exception e) when (e is ArgumentException or OutOfMemoryException or ExternalException or EgamiException)
        {
            return;
        }

        using (image)
        {
            if ((long)image.Width * image.Height > 1 << 20)
            {
                return;
            }

            using Bitmap target = new(64, 64, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(target))
            {
                g.DrawImage(image, new SdRectangle(0, 0, 64, 64));
            }

            if (image is not Bitmap bitmap)
            {
                return;
            }

            _ = bitmap.GetPixel(0, 0);
            BitmapData locked = bitmap.LockBits(new SdRectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            bitmap.UnlockBits(locked);
            using (Bitmap copy = bitmap.Clone(new SdRectangle(0, 0, bitmap.Width, bitmap.Height), PixelFormat.Format32bppArgb))
            {
                copy.RotateFlip(RotateFlipType.Rotate90FlipX);
            }

            using MemoryStream png = new();
            bitmap.Save(png, ImageFormat.Png);
        }
    }
}
