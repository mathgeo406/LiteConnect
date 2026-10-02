using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Models;

namespace LiteConnect.Core.Recording;

/// <summary>
/// Resolves session recording file paths and expands template variables
/// (<c>{connection}</c>, <c>{host}</c>, <c>{user}</c>, <c>{date}</c>, <c>{time}</c>, <c>{datetime}</c>).
/// </summary>
public static class SessionRecordingPathResolver
{
    public const string DefaultPattern = "{connection}_{date}_{time}.log";

    public static string ResolveFilePath(
        ConnectionInfo connection,
        IAppEnvironment? appEnvironment = null,
        DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var now = (timestamp ?? DateTimeOffset.Now).LocalDateTime;

        string baseDir = !string.IsNullOrWhiteSpace(connection.RecordingDirectory)
            ? connection.RecordingDirectory.Trim()
            : appEnvironment?.LogDirectory
              ?? Path.Combine(AppContext.BaseDirectory, "logs");

        string pattern = !string.IsNullOrWhiteSpace(connection.RecordingFilePattern)
            ? connection.RecordingFilePattern.Trim()
            : DefaultPattern;

        string connName = SanitizeFileNameSegment(
            string.IsNullOrWhiteSpace(connection.Name) ? connection.Hostname : connection.Name);
        string hostName = SanitizeFileNameSegment(connection.Hostname);
        string userName = SanitizeFileNameSegment(
            string.IsNullOrWhiteSpace(connection.Username) ? "user" : connection.Username);

        string fileName = pattern
            .Replace("{connection}", connName, StringComparison.OrdinalIgnoreCase)
            .Replace("{host}", hostName, StringComparison.OrdinalIgnoreCase)
            .Replace("{user}", userName, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", now.ToString("HHmmss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{datetime}", now.ToString("yyyy-MM-dd_HHmmss"), StringComparison.OrdinalIgnoreCase);

        fileName = SanitizeFileNameSegment(fileName);

        // If format is Html and pattern ended with default .log, adjust extension to .html
        if (connection.RecordingFormat == RecordingFormat.Html &&
            fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            fileName = Path.ChangeExtension(fileName, ".html");
        }

        return Path.GetFullPath(Path.Combine(baseDir, fileName));
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }

    private static string SanitizeFileNameSegment(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "session";

        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = input.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }
        return new string(chars);
    }
}
