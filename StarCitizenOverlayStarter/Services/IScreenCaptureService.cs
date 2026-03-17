using System.Drawing;
using StarCitizenOverlayStarter.Models;

namespace StarCitizenOverlayStarter.Services;

public interface IScreenCaptureService
{
    Bitmap Capture(CaptureRegion region);
}
