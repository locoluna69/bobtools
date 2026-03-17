using System.Drawing;
using System.Drawing.Imaging;
using BobTools.Models;
using BobTools.Utils;

namespace BobTools.Services;

public sealed class ScreenCaptureService : IScreenCaptureService
{
    public Bitmap Capture(CaptureRegion region)
    {
        if (!region.IsValid)
        {
            throw new InvalidOperationException("Capture region is not valid.");
        }

        if (!StarCitizenWindowLocator.TryGetClientBounds(out var clientBounds, out var errorMessage))
        {
            throw new InvalidOperationException(errorMessage);
        }

        if (region.X < 0 || region.Y < 0 || region.X + region.Width > clientBounds.Width || region.Y + region.Height > clientBounds.Height)
        {
            throw new InvalidOperationException("Saved capture region is outside the Star Citizen window. Set the scan area again.");
        }

        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(clientBounds.X + region.X, clientBounds.Y + region.Y, 0, 0, new Size(region.Width, region.Height), CopyPixelOperation.SourceCopy);
        return bitmap;
    }
}
