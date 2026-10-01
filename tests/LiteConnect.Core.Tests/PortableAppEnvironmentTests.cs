using System.IO;
using FluentAssertions;
using LiteConnect.Core.Services;
using Xunit;

namespace LiteConnect.Core.Tests;

/// <summary>
/// Tests for <see cref="PortableAppEnvironment"/>.
/// Because the portable mode depends on a file existing next to the EXE,
/// we test the installed mode path (marker file absent) and verify the
/// portable mode path structure independently.
/// </summary>
public class PortableAppEnvironmentTests : IDisposable
{
    // Track directories created by the constructor so we can clean them up.
    private readonly List<string> _dirsToClean = [];

    [Fact]
    public void Installed_mode_uses_appdata_paths()
    {
        // In the test runner the portable.marker is NOT present next to the EXE.
        var env = new PortableAppEnvironment();

        env.IsPortable.Should().BeFalse("no portable.marker file exists next to the test runner");

        var expectedAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LiteConnect");

        env.DataDirectory.Should().StartWith(expectedAppData);
        env.LogDirectory.Should().StartWith(expectedAppData);
        env.DatabasePath.Should().EndWith("connections.db");
        env.DpapiKeyPath.Should().EndWith("dpapi.key");

        _dirsToClean.Add(env.DataDirectory);
        _dirsToClean.Add(env.LogDirectory);
    }

    [Fact]
    public void Installed_mode_creates_directories_on_first_run()
    {
        var env = new PortableAppEnvironment();

        Directory.Exists(env.DataDirectory).Should().BeTrue();
        Directory.Exists(env.LogDirectory).Should().BeTrue();

        _dirsToClean.Add(env.DataDirectory);
        _dirsToClean.Add(env.LogDirectory);
    }

    [Fact]
    public void DatabasePath_is_inside_DataDirectory()
    {
        var env = new PortableAppEnvironment();

        Path.GetDirectoryName(env.DatabasePath)
            .Should().Be(env.DataDirectory);

        _dirsToClean.Add(env.DataDirectory);
    }

    [Fact]
    public void DpapiKeyPath_is_inside_DataDirectory()
    {
        var env = new PortableAppEnvironment();

        Path.GetDirectoryName(env.DpapiKeyPath)
            .Should().Be(env.DataDirectory);

        _dirsToClean.Add(env.DataDirectory);
    }

    public void Dispose()
    {
        foreach (var dir in _dirsToClean.Distinct())
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch { /* best effort */ }
        }
    }
}
