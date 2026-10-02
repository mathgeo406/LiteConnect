using System.Windows;
using LiteConnect.App.Services;
using LiteConnect.App.ViewModels;
using LiteConnect.App.Views;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Explicitly alias to avoid ambiguity with System.Windows.Forms.Application
using WpfApplication = System.Windows.Application;

namespace LiteConnect.App;

/// <summary>
/// Application entry point. Configures the DI container (composition root),
/// initializes the SQLCipher credential vault (DPAPI or Master Password),
/// and launches the main window.
/// </summary>
public partial class App : WpfApplication
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            var errPath = System.IO.Path.Combine(AppContext.BaseDirectory, "startup_error.log");
            System.IO.File.WriteAllText(errPath, args.Exception.ToString());
        };

        // Prevent WPF from shutting down when a startup modal dialog (e.g. MasterPasswordDialog) closes
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((_, services) =>
                {
                    // ── Core services ────────────────────────────────────────────
                    services.AddSingleton<IAppEnvironment, PortableAppEnvironment>();
                    services.AddSingleton<SqlCipherCredentialStore>();
                    services.AddSingleton<ICredentialStore>(sp => sp.GetRequiredService<SqlCipherCredentialStore>());
                    services.AddSingleton<IConnectionRepository, LiteConnect.Core.Data.SqliteConnectionRepository>();
                    services.AddSingleton<IKnownHostsStore, LiteConnect.Core.Data.SqliteKnownHostsStore>();
                    services.AddSingleton<ISessionRecorderFactory, LiteConnect.Core.Recording.SessionRecorderFactory>();
                    services.AddTransient<ISessionRecorder, LiteConnect.Core.Recording.FileSessionRecorder>();
                    services.AddSingleton<LiteConnect.Core.ImportExport.ConnectionExportImportService>();
                    services.AddSingleton<IExportService>(sp => sp.GetRequiredService<LiteConnect.Core.ImportExport.ConnectionExportImportService>());
                    services.AddSingleton<IImportService>(sp => sp.GetRequiredService<LiteConnect.Core.ImportExport.ConnectionExportImportService>());
                    services.AddSingleton<LiteConnect.Core.Data.DatabaseInitializer>();

                    // ── App services ─────────────────────────────────────────────
                    services.AddSingleton<AutoLockService>();

                    // ── ViewModels ───────────────────────────────────────────────
                    services.AddSingleton<ConnectionTreeViewModel>();
                    services.AddSingleton<MainViewModel>();

                    // ── Views ────────────────────────────────────────────────────
                    services.AddSingleton<MainWindow>();

                    // ── Protocol sessions ────────────────────────────────────────
                    services.AddTransient<LiteConnect.Protocols.Rdp.RdpSession>();
                    services.AddTransient<LiteConnect.Protocols.Ssh.SshSession>();
                })
                .Build();

            await _host.StartAsync();

            // ── Initialize Credential Vault (Sprint 4.1, 4.2, 4.3, 8.3) ──────────
            var credentialStore = _host.Services.GetRequiredService<SqlCipherCredentialStore>();
            var appEnvironment  = _host.Services.GetRequiredService<IAppEnvironment>();
            bool unlocked;
            try
            {
                unlocked = await credentialStore.InitializeAsync();
            }
            catch (Exception dbEx)
            {
                var choice = System.Windows.MessageBox.Show(
                    $"The connection database appears to be corrupted or unreadable:\n\n{dbEx.Message}\n\n" +
                    "Would you like to back up the damaged database and create a new one?",
                    "Database Recovery",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning);

                if (choice != System.Windows.MessageBoxResult.Yes)
                {
                    Shutdown(1);
                    return;
                }

                if (System.IO.File.Exists(appEnvironment.DatabasePath))
                {
                    string backupPath = $"{appEnvironment.DatabasePath}.corrupt.{DateTime.Now:yyyyMMdd_HHmmss}";
                    System.IO.File.Move(appEnvironment.DatabasePath, backupPath, overwrite: true);
                }
                if (System.IO.File.Exists(appEnvironment.DpapiKeyPath))
                {
                    System.IO.File.Delete(appEnvironment.DpapiKeyPath);
                }

                unlocked = await credentialStore.InitializeAsync();
            }

            if (!unlocked)
            {
                if (credentialStore.DpapiPortabilityMismatch)
                {
                    // Sprint 4.2: Portable vault moved to a different Windows user/PC
                    var portabilityDlg = new DpapiPortabilityDialog(credentialStore);
                    if (portabilityDlg.ShowDialog() != true || !credentialStore.IsUnlocked)
                    {
                        Shutdown();
                        return;
                    }
                }
                else
                {
                    // Sprint 4.3: Master Password mode — prompt user to unlock
                    var masterPwdDlg = new MasterPasswordDialog(credentialStore);
                    if (masterPwdDlg.ShowDialog() != true || !credentialStore.IsUnlocked)
                    {
                        Shutdown();
                        return;
                    }
                }
            }

            // Start Auto-Lock monitor (active in Master Password mode)
            var autoLock = _host.Services.GetRequiredService<AutoLockService>();
            autoLock.Start();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow     = mainWindow;
            ShutdownMode   = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            var errPath = System.IO.Path.Combine(AppContext.BaseDirectory, "startup_error.log");
            System.IO.File.WriteAllText(errPath, ex.ToString());
            System.Windows.MessageBox.Show(
                $"LiteConnect failed to start:\n\n{ex.Message}",
                "Startup Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown(1);
        }
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
