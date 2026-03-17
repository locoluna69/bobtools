using System.Drawing;
using BobTools.Models;

namespace BobTools.Services;

public interface IOcrService
{
    Task<OcrResult> ExtractTextAsync(Bitmap bitmap, string? debugOutputDirectory = null, string? preferredMode = null, string? captureProfile = null);
}
