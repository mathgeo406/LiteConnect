using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LiteConnect.App.Views;
using LiteConnect.Core.Models;
using LiteConnect.Core.Services;
using Microsoft.Win32;

namespace LiteConnect.App.Services;

/// <summary>
/// Monitors user inactivity and Windows session lock (<c>Win+L</c>) to automatically lock
/// the <see cref="SqlCipherCredentialStore"/> when in <see cref="EncryptionMode.MasterPassword"/> mode (Sprint 4.6).
/// In DPAPI mode, auto-lock is inactive because the Windows session itself protects DPAPI.
/// </summary>
public sealed class AutoLockService : IDisposable
{
    private readonly SqlCipherCredentialStore _store;
    private readonly DispatcherTimer _idleTimer;
    private DateTimeOffset _lastActivityUtc = DateTimeOffset.UtcNow;
    private bool _promptOpen;
    private bool _disposed;

    public AutoLockService(SqlCipherCredentialStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));

        _idleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _idleTimer.Tick += OnIdleTimerTick;

        InputManager.Current.PreProcessInput += OnPreProcessInput;
        SystemEvents.SessionSwitch           += OnWindowsSessionSwitch;
        _store.Locked                        += OnStoreLocked;
    }

    public void Start()
    {
        _lastActivityUtc = DateTimeOffset.UtcNow;
        _idleTimer.Start();
    }

    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is System.Windows.Input.MouseEventArgs or System.Windows.Input.KeyboardEventArgs)
        {
            _lastActivityUtc = DateTimeOffset.UtcNow;
        }
    }

    private void OnIdleTimerTick(object? sender, EventArgs e)
    {
        if (_disposed || _promptOpen) return;

        // Auto-lock is only active in Master Password mode
        if (_store.Mode != EncryptionMode.MasterPassword || !_store.IsUnlocked)
            return;

        int timeoutMinutes = _store.AutoLockMinutes;
        if (timeoutMinutes <= 0) return;

        if ((DateTimeOffset.UtcNow - _lastActivityUtc).TotalMinutes >= timeoutMinutes)
        {
            _store.Lock();
        }
    }

    private void OnWindowsSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (_disposed) return;

        if (e.Reason == SessionSwitchReason.SessionLock &&
            _store.Mode == EncryptionMode.MasterPassword &&
            _store.IsUnlocked)
        {
            _store.Lock();
        }
    }

    private void OnStoreLocked(object? sender, EventArgs e)
    {
        if (_disposed || _promptOpen || _store.Mode != EncryptionMode.MasterPassword)
            return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.BeginInvoke(DispatcherPriority.Normal, ShowUnlockPrompt);
    }

    private void ShowUnlockPrompt()
    {
        if (_promptOpen || _store.IsUnlocked) return;
        _promptOpen = true;

        var mainWin = System.Windows.Application.Current?.MainWindow;
        bool wasVisible = mainWin?.IsVisible == true;

        try
        {
            if (wasVisible && mainWin is not null)
                mainWin.Hide();

            var dlg = new MasterPasswordDialog(_store, isAutoLock: true);
            if (dlg.ShowDialog() == true && _store.IsUnlocked)
            {
                _lastActivityUtc = DateTimeOffset.UtcNow;
                if (wasVisible && mainWin is not null)
                {
                    mainWin.Show();
                    mainWin.Activate();
                }
            }
            else
            {
                System.Windows.Application.Current?.Shutdown();
            }
        }
        finally
        {
            _promptOpen = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _idleTimer.Stop();
        _idleTimer.Tick                      -= OnIdleTimerTick;
        InputManager.Current.PreProcessInput -= OnPreProcessInput;
        SystemEvents.SessionSwitch           -= OnWindowsSessionSwitch;
        _store.Locked                        -= OnStoreLocked;
    }
}
