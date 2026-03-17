namespace BobTools.Models;

public sealed class CaptureRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public bool IsValid => Width > 0 && Height > 0;

    public override string ToString() => $"X={X}, Y={Y}, W={Width}, H={Height}";
}
