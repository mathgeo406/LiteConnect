using LiteConnect.App.ViewModels;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Windows;

// Explicitly alias to avoid ambiguity with System.Windows.Forms.Application
using WpfApplication = System.Windows.Application;

namespace LiteConnect.App;

/// <summary>
/// Application entry point. Configures the DI container (composition root)
/// and launches the main window.
/// </summary>
public partial class App : WpfApplication
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                // ── Core services ────────────────────────────────────────────
                services.AddSingleton<IAppEnvironment, PortableAppEnvironment>();
                services.AddSingleton<IConnectionRepository, LiteConnect.Core.Data.SqliteConnectionRepository>();
                services.AddSingleton<LiteConnect.Core.Data.DatabaseInitializer>();

                // ── ViewModels ───────────────────────────────────────────────
                services.AddSingleton<ConnectionTreeViewModel>();
                services.AddSingleton<MainViewModel>();

                // ── Views ────────────────────────────────────────────────────
                services.AddSingleton<MainWindow>();

                // ── Protocol sessions ────────────────────────────────────────
                services.AddTransient<LiteConnect.Protocols.Rdp.RdpSession>();

                // ── Future services (wired as sprints complete) ──────────────
                // Sprint 4:  ICredentialStore
                // Sprint 5+: SshSession, ISessionRecorderFactory
                // Sprint 7:  IExportService, IImportService
            })
            .Build();

        await _host.StartAsync();

        // Initialize the database schema (creates tables if first run)
        var dbInit = _host.Services.GetRequiredService<LiteConnect.Core.Data.DatabaseInitializer>();
        await dbInit.InitializeAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
