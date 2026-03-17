using StarCitizenOverlayStarter.Models;

namespace StarCitizenOverlayStarter.Services;

public interface ISettingsService
{
    AppSettings Load();
    void Save(AppSettings settings);
}
