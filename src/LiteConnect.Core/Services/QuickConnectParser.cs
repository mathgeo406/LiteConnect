using LiteConnect.Core.Models;

namespace LiteConnect.Core.Services;

public sealed record QuickConnectTarget(
    string Hostname,
    ProtocolType Protocol,
    int? Port,
    string? Username,
    string? Domain,
    bool ProtocolAutoDetected);

/// <summary>
/// Parses Quick Connect strings of the form <c>hostname</c>, <c>user@hostname</c>,
/// <c>user@hostname:port</c>, <c>domain\user@hostname:port</c>, or <c>ssh://user@hostname:port</c> (Sprint 8.1).
/// Auto-detects protocol from URI scheme or standard ports (22/2222 → SSH, 3389 → RDP, 5900/5901 → VNC).
/// </summary>
public static class QuickConnectParser
{
    public static bool TryParse(
        string? rawInput,
        ProtocolType fallbackProtocol,
        out QuickConnectTarget? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(rawInput))
            return false;

        string input = rawInput.Trim();
        ProtocolType protocol = fallbackProtocol;
        bool autoDetected = false;

        // 1. Optional URI scheme prefix: ssh://, rdp://, vnc://
        int schemeIdx = input.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx > 0)
        {
            string scheme = input[..schemeIdx].Trim();
            if (Enum.TryParse<ProtocolType>(scheme, ignoreCase: true, out var schemeProto))
            {
                protocol     = schemeProto;
                autoDetected = true;
            }
            input = input[(schemeIdx + 3)..].Trim();
        }

        // Strip trailing slash if present
        input = input.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(input))
            return false;

        // 2. Optional user@ prefix (supports domain\user@host)
        string? username = null;
        string? domain   = null;
        string hostPart  = input;

        int atIdx = hostPart.LastIndexOf('@');
        if (atIdx > 0 && atIdx < hostPart.Length - 1)
        {
            string userPart = hostPart[..atIdx].Trim();
            hostPart        = hostPart[(atIdx + 1)..].Trim();

            int slashIdx = userPart.IndexOfAny(['\\', '/']);
            if (slashIdx > 0 && slashIdx < userPart.Length - 1)
            {
                domain   = userPart[..slashIdx];
                username = userPart[(slashIdx + 1)..];
            }
            else
            {
                username = userPart;
            }
        }

        // 3. Hostname and optional :port suffix (handling IPv6 [::1]:22 or host:port)
        string hostname = hostPart;
        int? port       = null;

        if (hostPart.StartsWith('['))
        {
            int closeBracket = hostPart.IndexOf(']');
            if (closeBracket > 1)
            {
                hostname = hostPart[1..closeBracket];
                if (closeBracket + 2 < hostPart.Length &&
                    hostPart[closeBracket + 1] == ':' &&
                    int.TryParse(hostPart[(closeBracket + 2)..], out int v6Port) &&
                    v6Port is > 0 and <= 65535)
                {
                    port = v6Port;
                }
            }
        }
        else
        {
            int colonIdx = hostPart.LastIndexOf(':');
            if (colonIdx > 0 &&
                hostPart.IndexOf(':') == colonIdx && // single colon (not bare IPv6)
                int.TryParse(hostPart[(colonIdx + 1)..], out int parsedPort) &&
                parsedPort is > 0 and <= 65535)
            {
                hostname = hostPart[..colonIdx].Trim();
                port     = parsedPort;
            }
        }

        if (string.IsNullOrWhiteSpace(hostname))
            return false;

        // 4. Auto-detect protocol from port if scheme wasn't explicitly given
        if (!autoDetected && port.HasValue)
        {
            switch (port.Value)
            {
                case 22 or 2222:
                    protocol     = ProtocolType.SSH;
                    autoDetected = true;
                    break;
                case 3389:
                    protocol     = ProtocolType.RDP;
                    autoDetected = true;
                    break;
                case 5900 or 5901:
                    protocol     = ProtocolType.VNC;
                    autoDetected = true;
                    break;
            }
        }

        result = new QuickConnectTarget(hostname, protocol, port, username, domain, autoDetected);
        return true;
    }
}
