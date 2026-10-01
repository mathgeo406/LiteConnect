using FluentAssertions;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Data;
using LiteConnect.Core.Models;

namespace LiteConnect.Core.Tests;

/// <summary>
/// Integration tests for <see cref="SqliteConnectionRepository"/>.
/// Each test class gets its own temp database file, initialized via
/// <see cref="DatabaseInitializer"/>, and cleaned up on disposal.
/// </summary>
public sealed class ConnectionRepositoryTests : IAsyncLifetime, IDisposable
{
    // -------------------------------------------------------------------------
    // Test infrastructure
    // -------------------------------------------------------------------------

    private readonly TestEnvironment _env = new();
    private SqliteConnectionRepository _repo = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        var initializer = new DatabaseInitializer(_env);
        await initializer.InitializeAsync();
        _repo = new SqliteConnectionRepository(_env);
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _repo?.Dispose();
        // Delete temp file — ignore errors if already gone.
        try { File.Delete(_env.DatabasePath); } catch { /* intentional */ }
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveConnectionAsync_and_LoadTree_roundtrip()
    {
        // Arrange
        var group = MakeGroup("Production");
        var connection = MakeConnection(group.Id, "web-01", "web01.example.com");

        await _repo.SaveGroupAsync(group);
        await _repo.SaveConnectionAsync(connection);

        // Act
        var root = await _repo.LoadTreeAsync();

        // Assert
        root.Groups.Should().ContainSingle(g => g.Id == group.Id);
        var loadedGroup = root.Groups.Single(g => g.Id == group.Id);
        loadedGroup.Connections.Should().ContainSingle(c => c.Id == connection.Id);

        var loaded = loadedGroup.Connections.Single(c => c.Id == connection.Id);
        loaded.Name.Should().Be("web-01");
        loaded.Hostname.Should().Be("web01.example.com");
        loaded.Protocol.Should().Be(ProtocolType.SSH);
    }

    [Fact]
    public async Task DeleteConnectionAsync_removes_connection()
    {
        // Arrange
        var group = MakeGroup("Staging");
        var connection = MakeConnection(group.Id, "db-01", "db01.staging.local");

        await _repo.SaveGroupAsync(group);
        await _repo.SaveConnectionAsync(connection);

        // Act
        await _repo.DeleteConnectionAsync(connection.Id);
        var root = await _repo.LoadTreeAsync();

        // Assert
        var loadedGroup = root.Groups.FirstOrDefault(g => g.Id == group.Id);
        loadedGroup.Should().NotBeNull();
        loadedGroup!.Connections.Should().NotContain(c => c.Id == connection.Id);
    }

    [Fact]
    public async Task SearchAsync_finds_by_hostname()
    {
        // Arrange
        var group = MakeGroup("Dev");
        var c1 = MakeConnection(group.Id, "alpha", "alpha.dev.internal");
        var c2 = MakeConnection(group.Id, "beta",  "beta.prod.internal");

        await _repo.SaveGroupAsync(group);
        await _repo.SaveConnectionAsync(c1);
        await _repo.SaveConnectionAsync(c2);

        // Act
        var results = await _repo.SearchAsync("alpha");

        // Assert
        results.Should().ContainSingle(c => c.Id == c1.Id);
        results.Should().NotContain(c => c.Id == c2.Id);
    }

    [Fact]
    public async Task MoveConnectionAsync_changes_group()
    {
        // Arrange
        var groupA = MakeGroup("GroupA");
        var groupB = MakeGroup("GroupB");
        var connection = MakeConnection(groupA.Id, "srv-01", "srv01.local");

        await _repo.SaveGroupAsync(groupA);
        await _repo.SaveGroupAsync(groupB);
        await _repo.SaveConnectionAsync(connection);

        // Act
        await _repo.MoveConnectionAsync(connection.Id, groupB.Id, sortOrder: 0);
        var root = await _repo.LoadTreeAsync();

        // Assert
        var loadedA = root.Groups.Single(g => g.Id == groupA.Id);
        var loadedB = root.Groups.Single(g => g.Id == groupB.Id);

        loadedA.Connections.Should().NotContain(c => c.Id == connection.Id);
        loadedB.Connections.Should().ContainSingle(c => c.Id == connection.Id);
    }

    [Fact]
    public async Task SaveGroupAsync_and_LoadTree_shows_group()
    {
        // Arrange
        var group = MakeGroup("Infrastructure");

        // Act
        await _repo.SaveGroupAsync(group);
        var root = await _repo.LoadTreeAsync();

        // Assert
        root.Groups.Should().ContainSingle(g => g.Id == group.Id);
        root.Groups.Single(g => g.Id == group.Id).Name.Should().Be("Infrastructure");
    }

    [Fact]
    public async Task SaveConnectionAsync_RdpSettings_all_fields_roundtrip()
    {
        // Arrange
        var group = MakeGroup("Windows Servers");
        var conn = MakeConnection(group.Id, "win-dc01", "dc01.corp.local");
        conn.Protocol = ProtocolType.RDP;
        conn.Port     = 3389;
        conn.Domain   = "CORP";
        conn.RdpSettings = new RdpConnectionSettings
        {
            ResolutionMode              = "Custom",
            DesktopWidth                = 2560,
            DesktopHeight               = 1440,
            ColorDepth                  = 32,
            SmartSizing                 = true,
            DpiScaling                  = 150,
            UseGateway                  = true,
            GatewayHostname             = "rdgw.corp.local",
            GatewayPort                 = 443,
            GatewayAuthMethod           = "Password",
            GatewayBypassLocal          = true,
            GatewayUsername             = "gwuser",
            GatewayDomain               = "CORP",
            RedirectClipboard           = true,
            RedirectDrives              = true,
            RedirectedDriveLetters      = "C,D",
            RedirectPrinters            = false,
            AudioRedirectionMode        = 0,
            AudioCaptureRedirectionMode = 1,
            KeyboardHookMode            = 2,
            RedirectSmartCards          = true,
            RedirectUsb                 = false,
            ConnectionSpeed             = "LAN",
            DesktopBackground           = true,
            FontSmoothing               = true,
            DesktopComposition          = true,
            VisualStyles                = true,
            PersistentBitmapCaching     = true,
            UseNla                      = true,
            EnableCredSsp               = true,
            AuthenticationLevel         = 2,
            ConsoleSession              = true,
            ConnectTimeoutSeconds       = 45,
            AutoReconnect               = true,
            MaxReconnectAttempts        = 15,
            ReconnectIntervalSeconds    = 5,
            StartProgram                = "powershell.exe",
            WorkDir                     = @"C:\Windows\System32",
            LoadBalancingInfo           = "tsv://MS Terminal Services Plugin.1.CorpFarm"
        };

        await _repo.SaveGroupAsync(group);
        await _repo.SaveConnectionAsync(conn);

        // Act
        var root = await _repo.LoadTreeAsync();
        var loaded = root.Groups.Single(g => g.Id == group.Id)
                         .Connections.Single(c => c.Id == conn.Id);

        // Assert
        loaded.Protocol.Should().Be(ProtocolType.RDP);
        loaded.RdpSettings.Should().NotBeNull();
        loaded.RdpSettings!.ResolutionMode.Should().Be("Custom");
        loaded.RdpSettings.DesktopWidth.Should().Be(2560);
        loaded.RdpSettings.DesktopHeight.Should().Be(1440);
        loaded.RdpSettings.DpiScaling.Should().Be(150);
        loaded.RdpSettings.UseGateway.Should().BeTrue();
        loaded.RdpSettings.GatewayHostname.Should().Be("rdgw.corp.local");
        loaded.RdpSettings.RedirectedDriveLetters.Should().Be("C,D");
        loaded.RdpSettings.ConnectionSpeed.Should().Be("LAN");
        loaded.RdpSettings.ConsoleSession.Should().BeTrue();
        loaded.RdpSettings.MaxReconnectAttempts.Should().Be(15);
        loaded.RdpSettings.StartProgram.Should().Be("powershell.exe");
    }

    // -------------------------------------------------------------------------
    // Factory helpers
    // -------------------------------------------------------------------------

    private static ConnectionGroup MakeGroup(string name) => new()
    {
        Id        = Guid.NewGuid(),
        Name      = name,
        ParentId  = null,
        SortOrder = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static ConnectionInfo MakeConnection(Guid groupId, string name, string hostname) => new()
    {
        Id        = Guid.NewGuid(),
        GroupId   = groupId,
        Name      = name,
        Hostname  = hostname,
        Protocol  = ProtocolType.SSH,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    // -------------------------------------------------------------------------
    // Test environment stub
    // -------------------------------------------------------------------------

    private sealed class TestEnvironment : IAppEnvironment
    {
        private readonly string _tempPath =
            Path.Combine(Path.GetTempPath(), $"lc_test_{Guid.NewGuid():N}.db");

        public bool   IsPortable     => true;
        public string DataDirectory  => Path.GetTempPath();
        public string LogDirectory   => Path.GetTempPath();
        public string DatabasePath   => _tempPath;
        public string DpapiKeyPath   => _tempPath + ".key";
    }
}
