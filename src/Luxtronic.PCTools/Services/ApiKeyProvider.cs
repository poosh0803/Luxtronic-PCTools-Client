namespace Luxtronic.PCTools.Services;

/// <summary>
/// Holds the technician's static API key (CONTRACT.md §1). One key per install - this app is
/// technician-operated on a single PC. The key itself is entered on the Settings page and stored by
/// <see cref="AppSettingsProvider"/>; this class just carries it to the services that send it.
/// </summary>
public sealed class ApiKeyProvider
{
    public string Key { get; }
    public string SourcePath { get; }

    public ApiKeyProvider(string? key, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "No API key set. Open Settings, paste the technician's API key (issued by the server) and Save.");
        }

        Key = key.Trim();
        SourcePath = sourcePath;
    }
}
