using System.Text;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Models;

namespace LiteConnect.Core.ImportExport;

/// <summary>
/// Exports and imports connections in RFC 4180 CSV format (Sprint 7.4).
/// </summary>
public static class CsvImportExportCodec
{
    public static readonly string[] StandardHeaders =
        ["Name", "Protocol", "Hostname", "Port", "Username", "Domain", "FolderPath", "Notes"];

    public static void WriteCsv(IEnumerable<LcxConnectionEntry> entries, Stream output)
    {
        using var writer = new StreamWriter(output, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true);
        writer.WriteLine(string.Join(",", StandardHeaders));

        foreach (var entry in entries)
        {
            var c = entry.Connection;
            string[] fields =
            [
                EscapeCsv(c.Name),
                EscapeCsv(c.Protocol.ToString()),
                EscapeCsv(c.Hostname),
                c.Port?.ToString() ?? string.Empty,
                EscapeCsv(c.Username),
                EscapeCsv(c.Domain),
                EscapeCsv(entry.FolderPath),
                EscapeCsv(c.Notes)
            ];
            writer.WriteLine(string.Join(",", fields));
        }

        writer.Flush();
    }

    public static IReadOnlyList<string> ReadHeaders(Stream input)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        string? firstLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(firstLine))
            return [];
        return ParseCsvLine(firstLine);
    }

    public static List<ImportPreviewItem> ReadCsv(Stream input, CsvColumnMapping? mapping = null)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        string content = reader.ReadToEnd();
        var rows = ParseCsvRows(content);
        if (rows.Count == 0)
            return [];

        var effectiveMapping = mapping ?? AutoDetectMapping(rows[0]);
        int startRow = effectiveMapping.HasHeaderRow ? 1 : 0;

        var items = new List<ImportPreviewItem>();
        for (int r = startRow; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Count == 0 || row.All(string.IsNullOrWhiteSpace))
                continue;

            string hostname = GetColumn(row, effectiveMapping.HostnameColumn);
            string name     = GetColumn(row, effectiveMapping.NameColumn);

            if (string.IsNullOrWhiteSpace(hostname) && string.IsNullOrWhiteSpace(name))
                continue;

            if (string.IsNullOrWhiteSpace(hostname))
                hostname = name;
            if (string.IsNullOrWhiteSpace(name))
                name = hostname;

            string protoStr = GetColumn(row, effectiveMapping.ProtocolColumn);
            if (!Enum.TryParse<ProtocolType>(protoStr, ignoreCase: true, out var protocol))
            {
                protocol = ProtocolType.SSH;
            }

            string portStr = GetColumn(row, effectiveMapping.PortColumn);
            int? port = int.TryParse(portStr, out int parsedPort) && parsedPort is > 0 and <= 65535
                ? parsedPort
                : null;

            string? username  = NullIfWhiteSpace(GetColumn(row, effectiveMapping.UsernameColumn));
            string? domain    = NullIfWhiteSpace(GetColumn(row, effectiveMapping.DomainColumn));
            string folderPath = GetColumn(row, effectiveMapping.FolderPathColumn).Trim();
            string? notes     = NullIfWhiteSpace(GetColumn(row, effectiveMapping.NotesColumn));

            items.Add(new ImportPreviewItem
            {
                Connection = new ConnectionInfo
                {
                    Name     = name.Trim(),
                    Protocol = protocol,
                    Hostname = hostname.Trim(),
                    Port     = port,
                    Username = username,
                    Domain   = domain,
                    Notes    = notes
                },
                FolderPath = folderPath
            });
        }

        return items;
    }

    public static CsvColumnMapping AutoDetectMapping(IReadOnlyList<string> headerRow)
    {
        var map = new CsvColumnMapping
        {
            NameColumn       = -1,
            ProtocolColumn   = -1,
            HostnameColumn   = -1,
            PortColumn       = -1,
            UsernameColumn   = -1,
            DomainColumn     = -1,
            FolderPathColumn = -1,
            NotesColumn      = -1,
            HasHeaderRow     = true
        };

        for (int i = 0; i < headerRow.Count; i++)
        {
            string h = headerRow[i].Trim().ToLowerInvariant();
            switch (h)
            {
                case "name" or "connection" or "title" or "displayname":
                    if (map.NameColumn < 0) map.NameColumn = i;
                    break;
                case "protocol" or "type":
                    if (map.ProtocolColumn < 0) map.ProtocolColumn = i;
                    break;
                case "hostname" or "host" or "server" or "ip" or "address":
                    if (map.HostnameColumn < 0) map.HostnameColumn = i;
                    break;
                case "port":
                    if (map.PortColumn < 0) map.PortColumn = i;
                    break;
                case "username" or "user" or "login":
                    if (map.UsernameColumn < 0) map.UsernameColumn = i;
                    break;
                case "domain":
                    if (map.DomainColumn < 0) map.DomainColumn = i;
                    break;
                case "folderpath" or "folder" or "group" or "path" or "parent":
                    if (map.FolderPathColumn < 0) map.FolderPathColumn = i;
                    break;
                case "notes" or "description" or "descr" or "comment":
                    if (map.NotesColumn < 0) map.NotesColumn = i;
                    break;
            }
        }

        // Fallback if headers were not recognized
        if (map.HostnameColumn < 0 && map.NameColumn < 0)
        {
            map.NameColumn       = 0;
            map.ProtocolColumn   = headerRow.Count > 1 ? 1 : -1;
            map.HostnameColumn   = headerRow.Count > 2 ? 2 : 0;
            map.PortColumn       = headerRow.Count > 3 ? 3 : -1;
            map.UsernameColumn   = headerRow.Count > 4 ? 4 : -1;
            map.DomainColumn     = headerRow.Count > 5 ? 5 : -1;
            map.FolderPathColumn = headerRow.Count > 6 ? 6 : -1;
            map.NotesColumn      = headerRow.Count > 7 ? 7 : -1;
        }

        return map;
    }

    private static string GetColumn(IReadOnlyList<string> row, int colIndex)
    {
        if (colIndex < 0 || colIndex >= row.Count)
            return string.Empty;
        return row[colIndex];
    }

    private static string? NullIfWhiteSpace(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeCsv(string? field)
    {
        if (string.IsNullOrEmpty(field))
            return string.Empty;

        if (field.IndexOfAny([',', '"', '\r', '\n']) >= 0)
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }
        return field;
    }

    public static List<string> ParseCsvLine(string line)
    {
        var rows = ParseCsvRows(line);
        return rows.Count > 0 ? rows[0] : [];
    }

    public static List<List<string>> ParseCsvRows(string csvText)
    {
        var rows = new List<List<string>>();
        var currentRow = new List<string>();
        var currentField = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < csvText.Length; i++)
        {
            char c = csvText[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < csvText.Length && csvText[i + 1] == '"')
                    {
                        currentField.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    currentField.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    currentRow.Add(currentField.ToString());
                    currentField.Clear();
                }
                else if (c == '\r')
                {
                    if (i + 1 < csvText.Length && csvText[i + 1] == '\n')
                        i++;
                    currentRow.Add(currentField.ToString());
                    currentField.Clear();
                    rows.Add(currentRow);
                    currentRow = [];
                }
                else if (c == '\n')
                {
                    currentRow.Add(currentField.ToString());
                    currentField.Clear();
                    rows.Add(currentRow);
                    currentRow = [];
                }
                else
                {
                    currentField.Append(c);
                }
            }
        }

        if (currentField.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(currentField.ToString());
            rows.Add(currentRow);
        }

        return rows;
    }
}
