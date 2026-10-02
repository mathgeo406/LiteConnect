using LiteConnect.Core.Models;

namespace LiteConnect.Core.ImportExport;

/// <summary>
/// Root JSON payload stored inside an encrypted or unencrypted <c>.lcx</c> bundle.
/// </summary>
public sealed class LcxBundlePayload
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<LcxConnectionEntry> Connections { get; set; } = [];
}

/// <summary>
/// Represents a single exported connection along with its folder path and optional embedded SSH private key.
/// </summary>
public sealed class LcxConnectionEntry
{
    public required ConnectionInfo Connection { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public string? EmbeddedPrivateKeyFileName { get; set; }
    public string? EmbeddedPrivateKeyContent { get; set; }
}
