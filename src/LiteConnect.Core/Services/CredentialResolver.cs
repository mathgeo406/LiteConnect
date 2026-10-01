using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Models;

namespace LiteConnect.Core.Services;

/// <summary>
/// Resolves effective credentials for a <see cref="ConnectionInfo"/> by combining
/// connection-level credentials from <see cref="ICredentialStore"/> with inherited
/// <c>DefaultUsername</c> and <c>DefaultDomain</c> from parent <see cref="ConnectionGroup"/>s (Sprint 4.7).
/// </summary>
public static class CredentialResolver
{
    /// <summary>
    /// Returns a <see cref="ConnectionInfo"/> populated with the effective credentials
    /// (Connection → Parent Group hierarchy).
    /// </summary>
    public static async Task<ConnectionInfo> ResolveEffectiveAsync(
        ConnectionInfo connection,
        IConnectionRepository repository,
        ICredentialStore? credentialStore = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(repository);

        string? username       = connection.Username;
        string? password       = connection.Password;
        string? domain         = connection.Domain;
        string? privateKeyPath = connection.PrivateKeyPath;
        string? keyPassphrase  = connection.KeyPassphrase;

        // 1. Check ICredentialStore for fresh decrypted credentials for this connection
        if (credentialStore is { IsUnlocked: true })
        {
            var stored = await credentialStore.GetCredentialAsync(connection.Id, ct);
            if (stored is not null)
            {
                username       = FirstNonEmpty(username,       stored.Username);
                password       = FirstNonEmpty(password,       stored.Password);
                domain         = FirstNonEmpty(domain,         stored.Domain);
                privateKeyPath = FirstNonEmpty(privateKeyPath, stored.PrivateKeyPath);
                keyPassphrase  = FirstNonEmpty(keyPassphrase,  stored.KeyPassphrase);
            }
        }

        // 2. Walk up the ConnectionGroup hierarchy if Username or Domain is still unset
        if ((string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(domain)) &&
            connection.GroupId != Guid.Empty)
        {
            var root = await repository.LoadTreeAsync(ct);
            var groupLookup = new Dictionary<Guid, ConnectionGroup>();
            IndexGroups(root, groupLookup);

            Guid? currentGroupId = connection.GroupId;
            while (currentGroupId is { } gid && gid != Guid.Empty && groupLookup.TryGetValue(gid, out var group))
            {
                username = FirstNonEmpty(username, group.DefaultUsername);
                domain   = FirstNonEmpty(domain,   group.DefaultDomain);

                if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(domain))
                    break;

                currentGroupId = group.ParentId;
            }
        }

        return new ConnectionInfo
        {
            Id                   = connection.Id,
            GroupId              = connection.GroupId,
            Name                 = connection.Name,
            Protocol             = connection.Protocol,
            Hostname             = connection.Hostname,
            Port                 = connection.Port,
            SortOrder            = connection.SortOrder,
            Username             = username,
            Password             = password,
            Domain               = domain,
            PrivateKeyPath       = privateKeyPath,
            KeyPassphrase        = keyPassphrase,
            SshSettings          = connection.SshSettings,
            RdpSettings          = connection.RdpSettings,
            VncSettings          = connection.VncSettings,
            ProxySettings        = connection.ProxySettings,
            AutoRecord           = connection.AutoRecord,
            RecordingFormat      = connection.RecordingFormat,
            RecordingDirectory   = connection.RecordingDirectory,
            RecordingFilePattern = connection.RecordingFilePattern,
            LastConnected        = connection.LastConnected,
            TimesConnected       = connection.TimesConnected,
            Notes                = connection.Notes,
            CreatedAt            = connection.CreatedAt,
            UpdatedAt            = connection.UpdatedAt
        };
    }

    private static void IndexGroups(ConnectionGroup group, Dictionary<Guid, ConnectionGroup> map)
    {
        if (group.Id != Guid.Empty)
            map[group.Id] = group;

        foreach (var child in group.Groups)
            IndexGroups(child, map);
    }

    private static string? FirstNonEmpty(string? primary, string? fallback) =>
        !string.IsNullOrWhiteSpace(primary) ? primary : (!string.IsNullOrWhiteSpace(fallback) ? fallback : null);
}
