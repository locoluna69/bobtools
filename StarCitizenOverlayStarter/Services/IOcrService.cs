using System.Drawing;
using StarCitizenOverlayStarter.Models;

namespace StarCitizenOverlayStarter.Services;

public interface IOcrService
{
    Task<OcrResult> ExtractTextAsync(Bitmap bitmap, string? debugOutputDirectory = null, string? preferredMode = null, string? captureProfile = null);
}
