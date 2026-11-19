using System.Diagnostics;
using System.Net.Http;
using System.Windows.Threading;
using FreshExile.Core;

namespace FreshExile.App;

public sealed partial class MainViewModel
{
    DispatcherTimer? _updateTimer;
    bool _updating;
    ReleaseInfo? _release;
    string? _updateStatus;
    bool _updateReady;

    /// <summary>Checks GitHub for a newer release a little after start and every few hours, and installs it.</summary>
    public bool AutoUpdate
    {
        get => State.AutoUpdate;
        set
        {
            if (State.AutoUpdate == value) return;
            State.AutoUpdate = value;
            Raise();
            Save();
            if (value) _ = CheckUpdateAsync(install: true);
        }
    }

    /// <summary>"Последняя версия", "Обновлено до 0.3.9 — перезапусти"…; null before the first check.</summary>
    public string? UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }

    /// <summary>The new version is downloaded: it goes in place of the exe on exit, a restart runs it.</summary>
    public bool UpdateReady { get => _updateReady; private set => Set(ref _updateReady, value); }

    public RelayCommand CheckUpdateCommand => _checkUpdate ??= new RelayCommand(() => _ = CheckUpdateAsync(install: true, byHand: true));
    RelayCommand? _checkUpdate;

    public RelayCommand OpenReleaseCommand => _openRelease ??= new RelayCommand(() =>
        Process.Start(new ProcessStartInfo(_release?.PageUrl is { Length: > 0 } p ? p : $"https://github.com/{UpdateCheck.Repo}/releases/latest") { UseShellExecute = true }));
    RelayCommand? _openRelease;

    void StartUpdates()
    {
        Updater.CleanUp();
        if (Updater.Pending)
        {
            UpdateReady = true;
            UpdateStatus = Loc.T("Новая версия скачана — встанет при выходе или перезапуске");
        }
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(6);
            if (AutoUpdate) _ = CheckUpdateAsync(install: true);
        };
        _updateTimer.Start();
    }

    async Task CheckUpdateAsync(bool install, bool byHand = false)
    {
        if (_updating || UpdateReady) return;
        _updating = true;
        try
        {
            if (byHand) UpdateStatus = Loc.T("Проверяю…");
            ReleaseInfo? latest;
            try { latest = await UpdateCheck.LatestAsync(_http); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                if (byHand) UpdateStatus = Loc.T("Не удалось проверить обновления");
                return;
            }
            _release = latest;
            if (latest == null || !UpdateCheck.IsNewer(latest, Updater.Current))
            {
                UpdateStatus = Loc.T("Последняя версия");
                return;
            }
            var v = latest.Version.ToString(3);
            if (!install || !Updater.CanInstall)
            {
                UpdateStatus = Loc.T($"Есть версия {v}");
                return;
            }
            UpdateStatus = Loc.T($"Скачиваю версию {v}…");
            if (await Updater.DownloadAsync(latest) is { } error)
            {
                UpdateStatus = Loc.T($"Версия {v}: {error}");
                return;
            }
            UpdateReady = true;
            UpdateStatus = Loc.T($"Версия {v} скачана — встанет при выходе или перезапуске");
            Toast(Loc.T($"Fresh Exile {v} скачан — встанет при выходе или перезапуске"));
        }
        finally { _updating = false; }
    }
}
