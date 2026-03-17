using System.IO;

namespace BobTools.Models;

public sealed class OfflineInputImageItem
{
    public OfflineInputImageItem(string filePath)
    {
        FilePath = filePath;
        DisplayName = Path.GetFileName(filePath);
    }

    public string FilePath { get; }

    public string DisplayName { get; }
}