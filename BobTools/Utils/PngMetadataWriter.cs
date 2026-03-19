using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using BobTools.Models;

namespace BobTools.Utils;

internal static class PngMetadataWriter
{
    public static void SaveBitmapWithSessionMetadata(
        Bitmap bitmap,
        string outputPath,
        SessionMetadata sessionMetadata,
        string sessionId,
        string captureProfile,
        string imageRole,
        IReadOnlyDictionary<string, string?>? extraMetadata = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            bitmapSource.Freeze();

            var metadata = BuildMetadata(sessionMetadata, sessionId, captureProfile, imageRole, extraMetadata);
            var frame = BitmapFrame.Create(bitmapSource, null, metadata, null);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(frame);

            using var fileStream = File.Create(outputPath);
            encoder.Save(fileStream);
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    public static void SaveFileWithSessionMetadata(
        string sourcePath,
        string outputPath,
        SessionMetadata sessionMetadata,
        string sessionId,
        string captureProfile,
        string imageRole,
        IReadOnlyDictionary<string, string?>? extraMetadata = null)
    {
        using var bitmap = new Bitmap(sourcePath);
        SaveBitmapWithSessionMetadata(bitmap, outputPath, sessionMetadata, sessionId, captureProfile, imageRole, extraMetadata);
    }

    private static BitmapMetadata BuildMetadata(
        SessionMetadata sessionMetadata,
        string sessionId,
        string captureProfile,
        string imageRole,
        IReadOnlyDictionary<string, string?>? extraMetadata)
    {
        var metadata = new BitmapMetadata("png");
        SetText(metadata, "Software", "BobTools");
        SetText(metadata, "Comment", $"SessionId={sessionId}; RsiHandle={sessionMetadata.RsiHandle}; Ship={sessionMetadata.Ship}; StarSystem={sessionMetadata.StarSystem}; LocationPath={sessionMetadata.LocationPath}");
        SetText(metadata, "BobTools.SessionId", sessionId);
        SetText(metadata, "BobTools.RsiHandle", sessionMetadata.RsiHandle);
        SetText(metadata, "BobTools.Ship", sessionMetadata.Ship);
        SetText(metadata, "BobTools.StarSystem", sessionMetadata.StarSystem);
        SetText(metadata, "BobTools.LocationKey", sessionMetadata.LocationKey);
        SetText(metadata, "BobTools.LocationName", sessionMetadata.LocationName);
        SetText(metadata, "BobTools.LocationPath", sessionMetadata.LocationPath);
        SetText(metadata, "BobTools.CaptureProfile", captureProfile);
        SetText(metadata, "BobTools.ImageRole", imageRole);
        SetText(metadata, "BobTools.TimestampUtc", DateTime.UtcNow.ToString("O"));

        if (extraMetadata is not null)
        {
            foreach (var pair in extraMetadata)
            {
                SetText(metadata, pair.Key, pair.Value);
            }
        }

        return metadata;
    }

    private static void SetText(BitmapMetadata metadata, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        metadata.SetQuery($"/tEXt/{{str={key}}}", value);
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}