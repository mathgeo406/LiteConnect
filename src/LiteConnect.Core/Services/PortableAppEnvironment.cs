using System.Reflection;
using LiteConnect.Core.Abstractions;

namespace LiteConnect.Core.Services;

/// <summary>
/// Resolves all application data paths.
/// Portable mode is activated when a file named <c>portable.marker</c> exists
/// in the same directory as the executable. In portable mode all data lives
/// next to the exe; in installed mode it goes under <c>%APPDATA%\LiteConnect</c>.
/// </summary>
public sealed class PortableAppEnvironment : IAppEnvironment
{
    private const string MarkerFile = "portable.marker";
    private const string AppName = "LiteConnect";

    public bool IsPortable { get; }
    public string DataDirectory { get; }
    public string LogDirectory { get; }
    public string DatabasePath { get; }
    public string DpapiKeyPath { get; }

    public PortableAppEnvironment()
    {
        var exeDir = Path.GetDirectoryName(
            Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location)
            ?? Directory.GetCurrentDirectory();

        IsPortable = File.Exists(Path.Combine(exeDir, MarkerFile));

        if (IsPortable)
        {
            DataDirectory = Path.Combine(exeDir, "data");
            LogDirectory = Path.Combine(exeDir, "logs");
        }
        else
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppName);
            DataDirectory = Path.Combine(appData, "data");
            LogDirectory = Path.Combine(appData, "logs");
        }

        DatabasePath = Path.Combine(DataDirectory, "connections.db");
        DpapiKeyPath = Path.Combine(DataDirectory, "dpapi.key");

        // Ensure directories exist on first run
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
