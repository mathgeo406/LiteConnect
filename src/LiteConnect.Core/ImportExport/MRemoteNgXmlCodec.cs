using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Models;

namespace LiteConnect.Core.ImportExport;

/// <summary>
/// Parses and writes mRemoteNG <c>confCons.xml</c> connection files (Sprint 7.3).
/// Preserves folder hierarchy, maps RDP/SSH/VNC settings, and decrypts mRemoteNG passwords
/// encrypted with the default <c>"mR3m"</c> master key or a user-supplied password.
/// </summary>
public static class MRemoteNgXmlCodec
{
    private const string DefaultMRemoteNgKey = "mR3m";

    public static bool IsMRemoteNgXml(Stream input)
    {
        try
        {
            using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 2048, leaveOpen: true);
            char[] buf = new char[1024];
            int read = reader.Read(buf, 0, buf.Length);
            if (read <= 0)
                return false;

            string head = new string(buf, 0, read);
            return head.Contains("<mrng:Connections", StringComparison.OrdinalIgnoreCase)
                || head.Contains("<Connections", StringComparison.OrdinalIgnoreCase)
                || head.Contains("http://mremoteng.org", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static List<ImportPreviewItem> ReadMRemoteNgXml(Stream input, string? encryptionPassword = null)
    {
        var doc = XDocument.Load(input);
        var root = doc.Root;
        if (root is null)
            return [];

        string effectiveKey = string.IsNullOrEmpty(encryptionPassword) ? DefaultMRemoteNgKey : encryptionPassword;
        var items = new List<ImportPreviewItem>();

        foreach (var child in root.Elements().Where(e => e.Name.LocalName.Equals("Node", StringComparison.OrdinalIgnoreCase)))
        {
            TraverseNode(child, parentFolderPath: string.Empty, effectiveKey, items);
        }

        return items;
    }

    private static void TraverseNode(
        XElement node,
        string parentFolderPath,
        string encryptionKey,
        List<ImportPreviewItem> items)
    {
        string nodeType = Attr(node, "Type") ?? "Connection";
        string nodeName = Attr(node, "Name") ?? "Unnamed";

        if (nodeType.Equals("Container", StringComparison.OrdinalIgnoreCase))
        {
            string currentFolder = string.IsNullOrEmpty(parentFolderPath)
                ? nodeName
                : $"{parentFolderPath}/{nodeName}";

            foreach (var child in node.Elements().Where(e => e.Name.LocalName.Equals("Node", StringComparison.OrdinalIgnoreCase)))
            {
                TraverseNode(child, currentFolder, encryptionKey, items);
            }
            return;
        }

        if (!nodeType.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            return;

        string? hostname = Attr(node, "Hostname");
        if (string.IsNullOrWhiteSpace(hostname))
            hostname = nodeName;

        var protocol = MapProtocol(Attr(node, "Protocol"));
        int? port = int.TryParse(Attr(node, "Port"), out int parsedPort) && parsedPort > 0
            ? parsedPort
            : null;

        // Strip default protocol ports if redundant or keep explicit
        string? username = NullIfEmpty(Attr(node, "Username"));
        string? domain   = NullIfEmpty(Attr(node, "Domain"));
        string? notes    = NullIfEmpty(Attr(node, "Descr"));

        string? rawPassword = Attr(node, "Password");
        string? decryptedPassword = TryDecryptMRemoteNgPassword(rawPassword, encryptionKey);

        var conn = new ConnectionInfo
        {
            Name     = nodeName,
            Protocol = protocol,
            Hostname = hostname.Trim(),
            Port     = port,
            Username = username,
            Password = decryptedPassword,
            Domain   = domain,
            Notes    = notes
        };

        if (protocol == ProtocolType.RDP)
        {
            conn.RdpSettings = MapRdpSettings(node, encryptionKey);
        }
        else if (protocol == ProtocolType.SSH)
        {
            conn.SshSettings = new SshConnectionSettings();
        }

        items.Add(new ImportPreviewItem
        {
            Connection = conn,
            FolderPath = parentFolderPath
        });
    }

    private static ProtocolType MapProtocol(string? mrngProto)
    {
        if (string.IsNullOrWhiteSpace(mrngProto))
            return ProtocolType.RDP;

        return mrngProto.Trim().ToUpperInvariant() switch
        {
            "SSH1" or "SSH2" or "SSH" => ProtocolType.SSH,
            "VNC"                     => ProtocolType.VNC,
            _                         => ProtocolType.RDP
        };
    }

    private static RdpConnectionSettings MapRdpSettings(XElement node, string encryptionKey)
    {
        var rdp = new RdpConnectionSettings();

        string? res = Attr(node, "Resolution");
        if (!string.IsNullOrWhiteSpace(res))
        {
            if (res.Equals("FitToWindow", StringComparison.OrdinalIgnoreCase) ||
                res.Equals("SmartSize", StringComparison.OrdinalIgnoreCase))
            {
                rdp.ResolutionMode = "Auto";
                rdp.SmartSizing    = res.Equals("SmartSize", StringComparison.OrdinalIgnoreCase);
            }
            else if (res.Equals("Fullscreen", StringComparison.OrdinalIgnoreCase))
            {
                rdp.ResolutionMode = "FullScreen";
            }
            else if (res.StartsWith("Res", StringComparison.OrdinalIgnoreCase))
            {
                // e.g. "Res1920x1080"
                string dims = res[3..];
                string[] parts = dims.Split('x', 'X');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out int w) &&
                    int.TryParse(parts[1], out int h))
                {
                    rdp.ResolutionMode = "Custom";
                    rdp.DesktopWidth   = w;
                    rdp.DesktopHeight  = h;
                }
            }
        }

        string? colors = Attr(node, "Colors");
        if (!string.IsNullOrWhiteSpace(colors))
        {
            rdp.ColorDepth = colors switch
            {
                var c when c.Contains("32") => 32,
                var c when c.Contains("24") => 24,
                var c when c.Contains("16") => 16,
                var c when c.Contains("15") => 15,
                var c when c.Contains("256") || c.Contains('8') => 8,
                _ => 32
            };
        }

        if (bool.TryParse(Attr(node, "RedirectDiskDrives"), out bool drives))
            rdp.RedirectDrives = drives;
        if (bool.TryParse(Attr(node, "RedirectClipboard"), out bool clip))
            rdp.RedirectClipboard = clip;
        if (bool.TryParse(Attr(node, "RedirectPrinters"), out bool printers))
            rdp.RedirectPrinters = printers;
        if (bool.TryParse(Attr(node, "RedirectSmartCards"), out bool sc))
            rdp.RedirectSmartCards = sc;
        if (bool.TryParse(Attr(node, "ConnectToConsole"), out bool console))
            rdp.ConsoleSession = console;

        string? sound = Attr(node, "RedirectSound");
        if (!string.IsNullOrWhiteSpace(sound))
        {
            rdp.AudioRedirectionMode = sound switch
            {
                var s when s.Contains("Leave", StringComparison.OrdinalIgnoreCase) => 1,
                var s when s.Contains("DoNotPlay", StringComparison.OrdinalIgnoreCase) => 2,
                _ => 0
            };
            rdp.RedirectAudio = rdp.AudioRedirectionMode != 2;
        }

        string? gwUsage = Attr(node, "RDGatewayUsageMethod");
        string? gwHost  = NullIfEmpty(Attr(node, "RDGatewayHostname"));
        if (!string.IsNullOrWhiteSpace(gwHost) &&
            !string.Equals(gwUsage, "Never", StringComparison.OrdinalIgnoreCase))
        {
            rdp.UseGateway      = true;
            rdp.GatewayHostname = gwHost;
            rdp.GatewayUsername = NullIfEmpty(Attr(node, "RDGatewayUsername"));
            rdp.GatewayDomain   = NullIfEmpty(Attr(node, "RDGatewayDomain"));
            rdp.GatewayPassword = TryDecryptMRemoteNgPassword(Attr(node, "RDGatewayPassword"), encryptionKey);
        }

        return rdp;
    }

    /// <summary>
    /// Attempts to decrypt an mRemoteNG encrypted password attribute.
    /// Supports mRemoteNG v1.76+ AES-GCM (PBKDF2-SHA1, 1000 iterations, 32B salt + 12B nonce + ciphertext + 16B tag)
    /// and legacy Rijndael/AES-CBC (MD5 key derivation, 16B IV + ciphertext).
    /// </summary>
    public static string? TryDecryptMRemoteNgPassword(string? encryptedBase64, string password = DefaultMRemoteNgKey)
    {
        if (string.IsNullOrWhiteSpace(encryptedBase64))
            return null;

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(encryptedBase64.Trim());
        }
        catch
        {
            return null;
        }

        // 1. Try mRemoteNG v1.76+ AES-GCM format: [32B salt][12B nonce][ciphertext][16B tag] (min 60 bytes)
        if (raw.Length >= 60)
        {
            try
            {
                ReadOnlySpan<byte> salt       = raw.AsSpan(0, 32);
                ReadOnlySpan<byte> nonce      = raw.AsSpan(32, 12);
                int cipherLen                 = raw.Length - 32 - 12 - 16;
                ReadOnlySpan<byte> ciphertext = raw.AsSpan(44, cipherLen);
                ReadOnlySpan<byte> tag        = raw.AsSpan(44 + cipherLen, 16);

                byte[] key = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(password),
                    salt,
                    iterations: 1000,
                    HashAlgorithmName.SHA1,
                    outputLength: 32);

                byte[] plain = new byte[cipherLen];
                using var aesGcm = new AesGcm(key, 16);
                aesGcm.Decrypt(nonce, ciphertext, tag, plain, associatedData: null);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                // Fall through to legacy CBC
            }
        }

        // 2. Try legacy mRemoteNG AES-CBC format: [16B IV][ciphertext], key = MD5(UTF8(password))
        if (raw.Length >= 32 && raw.Length % 16 == 0)
        {
            try
            {
                byte[] md5Key = MD5.HashData(Encoding.UTF8.GetBytes(password));
                using var aes = Aes.Create();
                aes.Key     = md5Key;
                aes.IV      = raw[..16];
                aes.Mode    = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using var decryptor = aes.CreateDecryptor();
                byte[] plain = decryptor.TransformFinalBlock(raw, 16, raw.Length - 16);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                // Could not decrypt
            }
        }

        return null;
    }

    public static void WriteMRemoteNgXml(IEnumerable<LcxConnectionEntry> entries, Stream output)
    {
        XNamespace mrng = "http://mremoteng.org";
        var root = new XElement(mrng + "Connections",
            new XAttribute(XNamespace.Xmlns + "mrng", mrng.NamespaceName),
            new XAttribute("Name", "Connections"),
            new XAttribute("Export", "true"),
            new XAttribute("ConfVersion", "2.6"));

        var folderElements = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            XElement parentElement = root;
            if (!string.IsNullOrWhiteSpace(entry.FolderPath))
            {
                string[] segments = entry.FolderPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
                string currentPath = string.Empty;

                foreach (string seg in segments)
                {
                    currentPath = string.IsNullOrEmpty(currentPath) ? seg : $"{currentPath}/{seg}";
                    if (!folderElements.TryGetValue(currentPath, out var folderNode))
                    {
                        folderNode = new XElement("Node",
                            new XAttribute("Name", seg),
                            new XAttribute("Type", "Container"),
                            new XAttribute("Expanded", "true"));
                        parentElement.Add(folderNode);
                        folderElements[currentPath] = folderNode;
                    }
                    parentElement = folderNode;
                }
            }

            var c = entry.Connection;
            string mrngProto = c.Protocol switch
            {
                ProtocolType.SSH => "SSH2",
                ProtocolType.VNC => "VNC",
                _                => "RDP"
            };

            var connNode = new XElement("Node",
                new XAttribute("Name", c.Name),
                new XAttribute("Type", "Connection"),
                new XAttribute("Protocol", mrngProto),
                new XAttribute("Hostname", c.Hostname),
                new XAttribute("Port", c.EffectivePort),
                new XAttribute("Username", c.Username ?? string.Empty),
                new XAttribute("Domain", c.Domain ?? string.Empty),
                new XAttribute("Descr", c.Notes ?? string.Empty));

            parentElement.Add(connNode);
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        using var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false),
            CloseOutput = false
        });
        doc.Save(writer);
        writer.Flush();
    }

    private static string? Attr(XElement el, string name)
    {
        return el.Attributes()
            .FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    private static string? NullIfEmpty(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
