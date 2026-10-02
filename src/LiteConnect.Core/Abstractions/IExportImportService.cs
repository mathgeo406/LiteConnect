using LiteConnect.Core.Models;

namespace LiteConnect.Core.Abstractions;

public enum ExportFormat
{
    EncryptedLcx,
    SessionsOnlyLcx,
    Csv,
    MRemoteNGXml
}

public enum ImportFormat
{
    Unknown,
    Lcx,
    Csv,
    MRemoteNGXml
}

public enum ConflictResolution
{
    Skip,
    Overwrite,
    Rename,
    KeepBoth
}

public sealed record ExportOptions(
    IReadOnlyList<Guid>? ConnectionIds,
    ExportFormat Format,
    bool IncludeCredentials,
    bool IncludeSshKeys,
    bool IncludeRecordingSettings,
    bool PreserveFolderStructure,
    string? Password = null);

public sealed record ImportFileInfo(
    ImportFormat Format,
    bool IsEncrypted,
    bool IncludesCredentials,
    bool IncludesSshKeys,
    IReadOnlyList<string>? CsvHeaders = null);

public sealed class CsvColumnMapping
{
    public int NameColumn { get; set; } = 0;
    public int ProtocolColumn { get; set; } = 1;
    public int HostnameColumn { get; set; } = 2;
    public int PortColumn { get; set; } = 3;
    public int UsernameColumn { get; set; } = 4;
    public int DomainColumn { get; set; } = 5;
    public int FolderPathColumn { get; set; } = 6;
    public int NotesColumn { get; set; } = 7;
    public bool HasHeaderRow { get; set; } = true;
}

public sealed class ImportPreviewItem
{
    public required ConnectionInfo Connection { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public bool Selected { get; set; } = true;
    public bool HasConflict { get; set; }
    public Guid? ConflictingConnectionId { get; set; }
    public ConflictResolution Resolution { get; set; } = ConflictResolution.Rename;
    public string? EmbeddedPrivateKeyFileName { get; set; }
    public string? EmbeddedPrivateKeyContent { get; set; }
}

public sealed class ImportPreview
{
    public ImportFormat Format { get; init; }
    public List<ImportPreviewItem> Items { get; init; } = [];
}

public sealed record ImportOptions(
    ConflictResolution DefaultConflictResolution = ConflictResolution.Rename,
    Guid? TargetRootGroupId = null);

public sealed record ImportResult(
    int ImportedCount,
    int OverwrittenCount,
    int SkippedCount,
    IReadOnlyList<string> Warnings);

public interface IExportService
{
    Task ExportAsync(ExportOptions options, Stream outputStream, CancellationToken ct = default);
}

public interface IImportService
{
    Task<ImportFileInfo> InspectFileAsync(Stream inputStream, string? fileNameHint = null, CancellationToken ct = default);

    Task<ImportPreview> PreviewAsync(
        Stream inputStream,
        string? password = null,
        CsvColumnMapping? csvMapping = null,
        string? fileNameHint = null,
        CancellationToken ct = default);

    Task<ImportResult> ImportAsync(
        ImportPreview preview,
        ImportOptions options,
        CancellationToken ct = default);
}
