using BobTools.Models;

namespace BobTools.Services;

public interface ISettingsService
{
    AppSettings Load();
    void Save(AppSettings settings);
}
