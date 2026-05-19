using SimpleViewer.Models;

namespace SimpleViewer.Services;

/// <summary>
/// Load and persist <see cref="AppSettings"/> to local app data.
/// </summary>
public interface ISettingsService
{
    string SettingsFilePath { get; }

    AppSettings Load();

    void Save(AppSettings settings);
}
