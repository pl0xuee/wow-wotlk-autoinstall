using WowWotlk.Gui.Models;

namespace WowWotlk.Gui.Services;

/// <summary>Owns settings.json (~/.config/wow-wotlk-autoinstall/).</summary>
public class SettingsService
{
    public AppSettings Settings { get; private set; } = AppSettings.Load();

    public Task SaveAsync() => Settings.SaveAsync();

    public void Reload() => Settings = AppSettings.Load();
}
