using System.Drawing.Imaging;

namespace DiscordChatHUD.Rendering;

internal static class LayeredBitmapCopy
{
    // Copy only pixels changed by the renderer. The persistent DIB retains all
    // other pixels, including transparent backgrounds from the last full frame.
    internal static unsafe long Copy(Bitmap bitmap, IntPtr destination, int destinationStride, Rectangle bounds)
    {
        bounds.Intersect(new Rectangle(Point.Empty, bitmap.Size));
        if (bounds.Width <= 0 || bounds.Height <= 0) return 0;
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var rowBytes = checked(bounds.Width * 4);
            for (var row = bounds.Top; row < bounds.Bottom; row++)
            {
                var sourceRow = (byte*)data.Scan0 + row * data.Stride + bounds.Left * 4;
                var destinationRow = (byte*)destination + row * destinationStride + bounds.Left * 4;
                Buffer.MemoryCopy(sourceRow, destinationRow, rowBytes, rowBytes);
            }
            return (long)rowBytes * bounds.Height;
        }
        finally { bitmap.UnlockBits(data); }
    }
}
