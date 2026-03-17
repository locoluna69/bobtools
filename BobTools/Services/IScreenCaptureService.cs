using System.Drawing;
using BobTools.Models;

namespace BobTools.Services;

public interface IScreenCaptureService
{
    Bitmap Capture(CaptureRegion region);
}
