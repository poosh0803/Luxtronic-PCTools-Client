using System.IO;

namespace Luxtronic.PCTools.Services;

/// <summary>
/// Reads the technician's static API key from a local file (CONTRACT.md §1: "small API key
/// file per technician"). One key per install - this app is technician-operated on a single
/// PC, not multi-user, so there's no in-app key management UI.
/// </summary>
public sealed class ApiKeyProvider
{
    public string Key { get; }
    public string SourcePath { get; }

    public ApiKeyProvider(string apiKeyFilePath)
    {
        SourcePath = apiKeyFilePath;

        if (!File.Exists(apiKeyFilePath))
        {
            throw new FileNotFoundException(
                $"API key file not found at '{apiKeyFilePath}'. Create it and paste the technician's " +
                "API key (issued by the server side) as the file's only contents, no quotes/newlines needed. " +
                "See README.md for details.");
        }

        var raw = File.ReadAllText(apiKeyFilePath).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidDataException($"API key file at '{apiKeyFilePath}' is empty.");
        }

        Key = raw;
    }
}
