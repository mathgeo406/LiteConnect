using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Models;
using LiteConnect.Core.Serialization;

namespace LiteConnect.Core.ImportExport;

/// <summary>
/// Full implementation of <see cref="IExportService"/> and <see cref="IImportService"/> (Sprint 7).
/// Supports encrypted <c>.lcx</c> bundles, sessions-only <c>.lcx</c> bundles, RFC 4180 CSV,
/// and mRemoteNG <c>confCons.xml</c> with folder hierarchy preservation and conflict resolution.
/// </summary>
public sealed class ConnectionExportImportService : IExportService, IImportService
{
    private readonly IConnectionRepository _repository;
    private readonly IAppEnvironment? _appEnvironment;

    public ConnectionExportImportService(
        IConnectionRepository repository,
        IAppEnvironment? appEnvironment = null)
    {
        _repository     = repository ?? throw new ArgumentNullException(nameof(repository));
        _appEnvironment = appEnvironment;
    }

    // ── Export ──────────────────────────────────────────────────────────────────

    public async Task ExportAsync(ExportOptions options, Stream outputStream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(outputStream);

        var rootGroup = await _repository.LoadTreeAsync(ct).ConfigureAwait(false);
        var entries   = new List<LcxConnectionEntry>();

        HashSet<Guid>? filterIds = options.ConnectionIds is { Count: > 0 }
            ? new HashSet<Guid>(options.ConnectionIds)
            : null;

        CollectExportEntries(rootGroup, parentPath: string.Empty, filterIds, options, entries);

        switch (options.Format)
        {
            case ExportFormat.EncryptedLcx:
                LcxFormatCodec.WriteLcx(
                    new LcxBundlePayload { Connections = entries },
                    includeCredentials: options.IncludeCredentials,
                    includeSshKeys: options.IncludeSshKeys,
                    encrypt: true,
                    password: options.Password,
                    output: outputStream);
                break;

            case ExportFormat.SessionsOnlyLcx:
                LcxFormatCodec.WriteLcx(
                    new LcxBundlePayload { Connections = entries },
                    includeCredentials: false,
                    includeSshKeys: false,
                    encrypt: !string.IsNullOrEmpty(options.Password),
                    password: options.Password,
                    output: outputStream);
                break;

            case ExportFormat.Csv:
                CsvImportExportCodec.WriteCsv(entries, outputStream);
                break;

            case ExportFormat.MRemoteNGXml:
                MRemoteNgXmlCodec.WriteMRemoteNgXml(entries, outputStream);
                break;
        }
    }

    private static void CollectExportEntries(
        ConnectionGroup group,
        string parentPath,
        HashSet<Guid>? filterIds,
        ExportOptions options,
        List<LcxConnectionEntry> results)
    {
        string folderPath = group.Id == Guid.Empty
            ? string.Empty
            : (string.IsNullOrEmpty(parentPath) ? group.Name : $"{parentPath}/{group.Name}");

        string effectiveFolderPath = options.PreserveFolderStructure ? folderPath : string.Empty;

        foreach (var conn in group.Connections.OrderBy(c => c.SortOrder))
        {
            if (filterIds is not null && !filterIds.Contains(conn.Id))
                continue;

            bool includeCreds = options.IncludeCredentials && options.Format != ExportFormat.SessionsOnlyLcx;
            var cloned = CloneConnectionForExport(conn, includeCreds, options.IncludeRecordingSettings);

            string? embeddedKeyName    = null;
            string? embeddedKeyContent = null;

            if (includeCreds &&
                options.IncludeSshKeys &&
                !string.IsNullOrWhiteSpace(conn.PrivateKeyPath) &&
                File.Exists(conn.PrivateKeyPath))
            {
                try
                {
                    embeddedKeyName    = Path.GetFileName(conn.PrivateKeyPath);
                    embeddedKeyContent = File.ReadAllText(conn.PrivateKeyPath);
                }
                catch
                {
                    // Skip embedding if key file is inaccessible
                }
            }

            results.Add(new LcxConnectionEntry
            {
                Connection                 = cloned,
                FolderPath                 = effectiveFolderPath,
                EmbeddedPrivateKeyFileName = embeddedKeyName,
                EmbeddedPrivateKeyContent  = embeddedKeyContent
            });
        }

        foreach (var sub in group.Groups.OrderBy(g => g.SortOrder))
        {
            CollectExportEntries(sub, folderPath, filterIds, options, results);
        }
    }

    private static ConnectionInfo CloneConnectionForExport(
        ConnectionInfo src,
        bool includeCredentials,
        bool includeRecordingSettings)
    {
        // Roundtrip settings through JSON to avoid sharing mutable object references
        var rdpClone   = ConnectionSettingsSerializer.Deserialize<RdpConnectionSettings>(ConnectionSettingsSerializer.Serialize(src.RdpSettings));
        var sshClone   = ConnectionSettingsSerializer.Deserialize<SshConnectionSettings>(ConnectionSettingsSerializer.Serialize(src.SshSettings));
        var vncClone   = ConnectionSettingsSerializer.Deserialize<VncConnectionSettings>(ConnectionSettingsSerializer.Serialize(src.VncSettings));
        var proxyClone = ConnectionSettingsSerializer.Deserialize<ProxySettings>(ConnectionSettingsSerializer.Serialize(src.ProxySettings));

        if (!includeCredentials)
        {
            if (rdpClone is not null)
                rdpClone.GatewayPassword = null;
            if (proxyClone is not null)
            {
                proxyClone.Password         = null;
                proxyClone.JumpHostPassword = null;
            }
        }

        return new ConnectionInfo
        {
            Id                   = src.Id,
            GroupId              = src.GroupId,
            Name                 = src.Name,
            Protocol             = src.Protocol,
            Hostname             = src.Hostname,
            Port                 = src.Port,
            SortOrder            = src.SortOrder,
            Username             = src.Username,
            Password             = includeCredentials ? src.Password : null,
            Domain               = src.Domain,
            PrivateKeyPath       = includeCredentials ? src.PrivateKeyPath : null,
            KeyPassphrase        = includeCredentials ? src.KeyPassphrase : null,
            RdpSettings          = rdpClone,
            SshSettings          = sshClone,
            VncSettings          = vncClone,
            ProxySettings        = proxyClone,
            AutoRecord           = includeRecordingSettings && src.AutoRecord,
            RecordingFormat      = includeRecordingSettings ? src.RecordingFormat : RecordingFormat.PlainText,
            RecordingDirectory   = includeRecordingSettings ? src.RecordingDirectory : null,
            RecordingFilePattern = includeRecordingSettings ? src.RecordingFilePattern : null,
            Notes                = src.Notes,
            CreatedAt            = src.CreatedAt,
            UpdatedAt            = src.UpdatedAt
        };
    }

    // ── Import Inspection & Preview ─────────────────────────────────────────────

    public Task<ImportFileInfo> InspectFileAsync(
        Stream inputStream,
        string? fileNameHint = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputStream);

        long startPos = inputStream.CanSeek ? inputStream.Position : 0;
        Span<byte> header = stackalloc byte[LcxFormatCodec.HeaderSize];
        int read = inputStream.Read(header);
        if (inputStream.CanSeek)
            inputStream.Position = startPos;

        if (LcxFormatCodec.TryInspectHeader(
                header[..read],
                out bool isEncrypted,
                out bool includesCreds,
                out bool includesKeys))
        {
            return Task.FromResult(new ImportFileInfo(
                ImportFormat.Lcx,
                IsEncrypted: isEncrypted,
                IncludesCredentials: includesCreds,
                IncludesSshKeys: includesKeys));
        }

        if (MRemoteNgXmlCodec.IsMRemoteNgXml(inputStream))
        {
            if (inputStream.CanSeek)
                inputStream.Position = startPos;

            return Task.FromResult(new ImportFileInfo(
                ImportFormat.MRemoteNGXml,
                IsEncrypted: false,
                IncludesCredentials: true,
                IncludesSshKeys: false));
        }

        if (inputStream.CanSeek)
            inputStream.Position = startPos;

        string ext = Path.GetExtension(fileNameHint ?? string.Empty).ToLowerInvariant();
        if (ext == ".xml")
        {
            return Task.FromResult(new ImportFileInfo(
                ImportFormat.MRemoteNGXml,
                IsEncrypted: false,
                IncludesCredentials: true,
                IncludesSshKeys: false));
        }

        var csvHeaders = CsvImportExportCodec.ReadHeaders(inputStream);
        if (inputStream.CanSeek)
            inputStream.Position = startPos;

        if (csvHeaders.Count > 0)
        {
            return Task.FromResult(new ImportFileInfo(
                ImportFormat.Csv,
                IsEncrypted: false,
                IncludesCredentials: false,
                IncludesSshKeys: false,
                CsvHeaders: csvHeaders));
        }

        return Task.FromResult(new ImportFileInfo(ImportFormat.Unknown, false, false, false));
    }

    public async Task<ImportPreview> PreviewAsync(
        Stream inputStream,
        string? password = null,
        CsvColumnMapping? csvMapping = null,
        string? fileNameHint = null,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await inputStream.CopyToAsync(ms, ct).ConfigureAwait(false);
        ms.Position = 0;

        var info = await InspectFileAsync(ms, fileNameHint, ct).ConfigureAwait(false);
        ms.Position = 0;

        List<ImportPreviewItem> items = info.Format switch
        {
            ImportFormat.Lcx => ReadLcxPreviewItems(ms, password),
            ImportFormat.MRemoteNGXml => MRemoteNgXmlCodec.ReadMRemoteNgXml(ms, password),
            ImportFormat.Csv => CsvImportExportCodec.ReadCsv(ms, csvMapping),
            _ => throw new InvalidDataException("Unsupported or unrecognized import file format.")
        };

        // Detect conflicts against existing repository tree
        var rootGroup = await _repository.LoadTreeAsync(ct).ConfigureAwait(false);
        var existingByFolder = new Dictionary<string, List<ConnectionInfo>>(StringComparer.OrdinalIgnoreCase);
        IndexExistingConnections(rootGroup, parentPath: string.Empty, existingByFolder);

        foreach (var item in items)
        {
            string normalizedFolder = NormalizeFolderPath(item.FolderPath);
            item.FolderPath = normalizedFolder;

            if (existingByFolder.TryGetValue(normalizedFolder, out var existingList))
            {
                var match = existingList.FirstOrDefault(e =>
                    string.Equals(e.Name, item.Connection.Name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Hostname, item.Connection.Hostname, StringComparison.OrdinalIgnoreCase) &&
                    e.Protocol == item.Connection.Protocol);

                if (match is not null)
                {
                    item.HasConflict             = true;
                    item.ConflictingConnectionId = match.Id;
                    item.Resolution              = ConflictResolution.Rename;
                }
            }
        }

        return new ImportPreview
        {
            Format = info.Format,
            Items  = items
        };
    }

    private static List<ImportPreviewItem> ReadLcxPreviewItems(Stream stream, string? password)
    {
        var bundle = LcxFormatCodec.ReadLcx(stream, password);
        return bundle.Connections.Select(e => new ImportPreviewItem
        {
            Connection                 = e.Connection,
            FolderPath                 = e.FolderPath,
            EmbeddedPrivateKeyFileName = e.EmbeddedPrivateKeyFileName,
            EmbeddedPrivateKeyContent  = e.EmbeddedPrivateKeyContent
        }).ToList();
    }

    // ── Import Execution ────────────────────────────────────────────────────────

    public async Task<ImportResult> ImportAsync(
        ImportPreview preview,
        ImportOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(options);

        var rootGroup = await _repository.LoadTreeAsync(ct).ConfigureAwait(false);
        var folderMap = new Dictionary<string, ConnectionGroup>(StringComparer.OrdinalIgnoreCase);
        var existingByFolder = new Dictionary<string, List<ConnectionInfo>>(StringComparer.OrdinalIgnoreCase);

        IndexExistingFolders(rootGroup, parentPath: string.Empty, folderMap);
        IndexExistingConnections(rootGroup, parentPath: string.Empty, existingByFolder);

        int imported    = 0;
        int overwritten = 0;
        int skipped     = 0;
        var warnings    = new List<string>();

        foreach (var item in preview.Items.Where(i => i.Selected))
        {
            string folderPath = NormalizeFolderPath(item.FolderPath);
            Guid targetGroupId = await EnsureFolderHierarchyAsync(
                folderPath,
                options.TargetRootGroupId,
                rootGroup,
                folderMap,
                ct).ConfigureAwait(false);

            var conn = item.Connection;

            // Restore embedded SSH key if present and local path is missing
            if (!string.IsNullOrEmpty(item.EmbeddedPrivateKeyContent) &&
                !string.IsNullOrEmpty(item.EmbeddedPrivateKeyFileName) &&
                (string.IsNullOrWhiteSpace(conn.PrivateKeyPath) || !File.Exists(conn.PrivateKeyPath)))
            {
                try
                {
                    string keysDir = Path.Combine(
                        _appEnvironment?.DataDirectory ?? Path.Combine(AppContext.BaseDirectory, "data"),
                        "imported_keys");
                    Directory.CreateDirectory(keysDir);

                    string restoredKeyPath = Path.Combine(keysDir, SanitizeFileName(item.EmbeddedPrivateKeyFileName));
                    await File.WriteAllTextAsync(restoredKeyPath, item.EmbeddedPrivateKeyContent, ct).ConfigureAwait(false);
                    conn.PrivateKeyPath = restoredKeyPath;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not restore SSH key for '{conn.Name}': {ex.Message}");
                }
            }

            if (item.HasConflict && item.ConflictingConnectionId.HasValue)
            {
                var resolution = item.Resolution;
                if (resolution == ConflictResolution.Skip)
                {
                    skipped++;
                    continue;
                }

                if (resolution == ConflictResolution.Overwrite)
                {
                    conn.Id        = item.ConflictingConnectionId.Value;
                    conn.GroupId   = targetGroupId;
                    conn.UpdatedAt = DateTimeOffset.UtcNow;
                    await _repository.SaveConnectionAsync(conn, ct).ConfigureAwait(false);
                    overwritten++;
                    continue;
                }

                if (resolution == ConflictResolution.Rename)
                {
                    conn.Id   = Guid.NewGuid();
                    conn.Name = GenerateUniqueConnectionName(conn.Name, existingByFolder.GetValueOrDefault(folderPath));
                }
                else // KeepBoth
                {
                    conn.Id = Guid.NewGuid();
                }
            }
            else
            {
                conn.Id = Guid.NewGuid();
            }

            conn.GroupId   = targetGroupId;
            conn.CreatedAt = DateTimeOffset.UtcNow;
            conn.UpdatedAt = DateTimeOffset.UtcNow;

            await _repository.SaveConnectionAsync(conn, ct).ConfigureAwait(false);

            if (!existingByFolder.TryGetValue(folderPath, out var list))
            {
                list = [];
                existingByFolder[folderPath] = list;
            }
            list.Add(conn);
            imported++;
        }

        return new ImportResult(imported, overwritten, skipped, warnings);
    }

    private async Task<Guid> EnsureFolderHierarchyAsync(
        string folderPath,
        Guid? targetRootGroupId,
        ConnectionGroup rootGroup,
        Dictionary<string, ConnectionGroup> folderMap,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            if (targetRootGroupId.HasValue && targetRootGroupId.Value != Guid.Empty)
                return targetRootGroupId.Value;

            // Connections table requires a valid GroupId; use first top-level group or create "Imported"
            var firstTop = rootGroup.Groups.FirstOrDefault();
            if (firstTop is not null)
                return firstTop.Id;

            return await EnsureFolderHierarchyAsync("Imported", null, rootGroup, folderMap, ct).ConfigureAwait(false);
        }

        string[] segments = folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string currentPath = string.Empty;
        Guid? parentId = targetRootGroupId is { } id && id != Guid.Empty ? id : null;

        foreach (string seg in segments)
        {
            currentPath = string.IsNullOrEmpty(currentPath) ? seg : $"{currentPath}/{seg}";
            if (!folderMap.TryGetValue(currentPath, out var group))
            {
                group = new ConnectionGroup
                {
                    Id         = Guid.NewGuid(),
                    Name       = seg,
                    ParentId   = parentId,
                    IsExpanded = true,
                    CreatedAt  = DateTimeOffset.UtcNow,
                    UpdatedAt  = DateTimeOffset.UtcNow
                };
                await _repository.SaveGroupAsync(group, ct).ConfigureAwait(false);
                folderMap[currentPath] = group;
            }
            parentId = group.Id;
        }

        return parentId!.Value;
    }

    private static void IndexExistingFolders(
        ConnectionGroup group,
        string parentPath,
        Dictionary<string, ConnectionGroup> folderMap)
    {
        string path = group.Id == Guid.Empty
            ? string.Empty
            : (string.IsNullOrEmpty(parentPath) ? group.Name : $"{parentPath}/{group.Name}");

        if (!string.IsNullOrEmpty(path))
            folderMap[path] = group;

        foreach (var sub in group.Groups)
        {
            IndexExistingFolders(sub, path, folderMap);
        }
    }

    private static void IndexExistingConnections(
        ConnectionGroup group,
        string parentPath,
        Dictionary<string, List<ConnectionInfo>> existingByFolder)
    {
        string path = group.Id == Guid.Empty
            ? string.Empty
            : (string.IsNullOrEmpty(parentPath) ? group.Name : $"{parentPath}/{group.Name}");

        if (group.Connections.Count > 0)
        {
            if (!existingByFolder.TryGetValue(path, out var list))
            {
                list = [];
                existingByFolder[path] = list;
            }
            list.AddRange(group.Connections);
        }

        foreach (var sub in group.Groups)
        {
            IndexExistingConnections(sub, path, existingByFolder);
        }
    }

    private static string NormalizeFolderPath(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return string.Empty;

        string[] parts = folderPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("/", parts);
    }

    private static string GenerateUniqueConnectionName(string baseName, List<ConnectionInfo>? existingInFolder)
    {
        if (existingInFolder is null || existingInFolder.Count == 0)
            return $"{baseName} (Imported)";

        int suffix = 2;
        while (true)
        {
            string candidate = $"{baseName} ({suffix})";
            if (!existingInFolder.Any(c => string.Equals(c.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
            suffix++;
        }
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(ch => Array.IndexOf(invalid, ch) >= 0 ? '_' : ch).ToArray());
    }
}
