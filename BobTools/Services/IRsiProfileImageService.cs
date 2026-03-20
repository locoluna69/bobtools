namespace BobTools.Services;

public interface IRsiProfileImageService
{
    Task<string?> GetProfileImagePathAsync(string rsiHandle, CancellationToken cancellationToken = default);
}