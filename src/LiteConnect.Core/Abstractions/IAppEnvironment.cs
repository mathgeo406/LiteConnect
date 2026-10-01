namespace LiteConnect.Core.Abstractions;

/// <summary>
/// Provides path resolution for all data, log, and config directories.
/// Two modes:
///   - Portable: paths are relative to the executable directory (portable.marker present)
///   - Installed: paths are under %APPDATA%\LiteConnect
/// </summary>
public interface IAppEnvironment
{
    /// <summary>True when running in portable mode (portable.marker file detected).</summary>
    bool IsPortable { get; }

    /// <summary>Root data directory — contains connections.db and dpapi.key.</summary>
    string DataDirectory { get; }

    /// <summary>Session recording log directory.</summary>
    string LogDirectory { get; }

    /// <summary>Full path to the SQLCipher database file.</summary>
    string DatabasePath { get; }

    /// <summary>Full path to the DPAPI-encrypted SQLCipher key file.</summary>
    string DpapiKeyPath { get; }
}
