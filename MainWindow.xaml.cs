using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DriftHub.Models;
using DriftHub.Services;

namespace DriftHub;

public partial class MainWindow : Window
{
    private List<Spot> _spots = new();
    private bool _busy;
    private PeriodicTimer? _updateTimer;
    private string _notifiedAppVersion = "";
    private CancellationTokenSource? _startupCts;
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x3D, 0xD6, 0x8C));
    private static readonly SolidColorBrush Gray = new(Color.FromRgb(0x55, 0x55, 0x55));
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DriftHub", "gta_path.txt");

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = "v" + UpdateService.CurrentVersion;
        if (File.Exists(_settingsPath))
            try { GtaPathBox.Text = File.ReadAllText(_settingsPath).Trim(); } catch { }
        if (string.IsNullOrWhiteSpace(GtaPathBox.Text))
            GtaPathBox.Text = GtaPathService.AutoDetect() ?? "";
        StartupText.Text = "Загрузка спотов...";
        RefreshAll();
        _ = CheckUpdatesOnStartupAsync();
    }

    private void Log(string s)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\n");
        LogBox.ScrollToEnd();
    }

    private string Gta() => GtaPathBox.Text.Trim();
    private bool GtaOk() => GtaPathService.IsValid(Gta());

    private void RefreshAll()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, Gta());
        }
        catch { }
        if (!GtaOk())
        {
            BaseDot.Fill = Gray; BaseStatus.Text = "Укажи папку GTA";
            BaseDetail.Text = "Кнопка «Папка GTA» → выбери GTA5.exe";
            SpotsHint.Text = "";
            _spots = SpotsCatalog.Load();
            SpotsGrid.ItemsSource = _spots;
            return;
        }
        var st = MappingStore.GetBaseStatus(Gta());
        BaseDot.Fill = st.Installed ? Green : Gray;
        BaseStatus.Text = st.Installed ? "База установлена" : "База не установлена";
        BaseDetail.Text = st.Detail + (st.BackupExists ? " · бэкап есть" : "");
        RestoreBtn.IsEnabled = st.BackupExists;

        _spots = SpotsCatalog.Load();
        if (_spots.Count == 0)
            SpotsHint.Text = "Папка spots/ пуста. " + MappingStore.SpotsHint;
        else
        {
            SpotsHint.Text = $"{_spots.Count} спот(ов)";
            var installed = MappingStore.GetInstalledSpots(Gta());
            foreach (var s in _spots) s.IsInstalled = installed.Contains(s.YmapEntry.ToLowerInvariant());
        }
        SpotsGrid.ItemsSource = null;
        SpotsGrid.ItemsSource = _spots;
    }

    // Проверка обновлений и докачка спотов — в фоне, интерфейс не блокируем.
    // При старте висит полноэкранный оверлей; дальше проверки идут каждые
    // 30 минут тихо (только строки в лог + уведомление о новой версии).
    private async Task CheckUpdatesOnStartupAsync()
    {
        await CheckUpdatesAsync(showOverlay: true);
        StartPeriodicUpdateChecks();
    }

    private async Task CheckUpdatesAsync(bool showOverlay)
    {
        if (_busy) return;
        _busy = true;
        using var cts = new CancellationTokenSource();
        _startupCts = cts;
        try
        {
            if (showOverlay) StartupText.Text = "Проверка обновлений...";
            // BeginInvoke вместо Invoke: колбэк никогда не блокирует сетевой поток.
            Action<string> cb = m =>
            {
                try
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (showOverlay) StartupText.Text = m;
                        Log(m);
                    });
                }
                catch { }
            };
            var res = await Task.Run(() => UpdateService.CheckAsync(cb, cts.Token));
            if (cts.IsCancellationRequested) return; // пользователь пропустил проверку
            if (res.SpotsAdded + res.SpotsUpdated > 0)
                RefreshAll();
            if (res.AppUpdated && res.NewVersion != _notifiedAppVersion)
            {
                _notifiedAppVersion = res.NewVersion;
                if (showOverlay) StartupOverlay.Visibility = Visibility.Collapsed;
                MessageBox.Show($"Скачана версия {res.NewVersion}. Приложение перезапустится.",
                    "DriftHub — обновление", MessageBoxButton.OK, MessageBoxImage.Information);
                UpdateService.LaunchUpdaterAndRestart(res.NewExePath);
                Application.Current.Shutdown();
            }
        }
        catch (Exception ex) { Log("Обновления: " + ex.Message); }
        finally
        {
            _busy = false;
            _startupCts = null;
            if (showOverlay) StartupOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void StartupSkip_Click(object sender, RoutedEventArgs e)
    {
        try { _startupCts?.Cancel(); } catch { }
        StartupOverlay.Visibility = Visibility.Collapsed;
        Log("Проверка обновлений пропущена пользователем.");
    }

    private void StartPeriodicUpdateChecks()
    {
        _updateTimer ??= new PeriodicTimer(TimeSpan.FromMinutes(30));
        _ = Task.Run(async () =>
        {
            while (await _updateTimer.WaitForNextTickAsync())
            {
                try { await Dispatcher.InvokeAsync(() => CheckUpdatesAsync(showOverlay: false)).Task.Unwrap(); }
                catch { }
            }
        });
    }

    private async Task RunBusy(Func<Action<string>, Task> op)
    {
        if (_busy) return;
        _busy = true;
        BusyBar.Visibility = Visibility.Visible;
        BusyBar.IsIndeterminate = true;
        InstallBaseBtn.IsEnabled = RestoreBtn.IsEnabled = SpotsGrid.IsEnabled = false;
        try { await Task.Run(() => op(Log)); }
        catch (Exception ex) { Log("Ошибка: " + ex.Message); }
        finally
        {
            _busy = false;
            BusyBar.Visibility = Visibility.Collapsed;
            InstallBaseBtn.IsEnabled = SpotsGrid.IsEnabled = true;
            RefreshAll();
        }
    }

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var p = GtaPathService.AskUserViaExePicker();
        if (p != null) { GtaPathBox.Text = p; Log("Папка GTA: " + p); }
        RefreshAll();
    }

    private void AutoBtn_Click(object sender, RoutedEventArgs e)
    {
        var a = GtaPathService.AutoDetect();
        if (a != null) { GtaPathBox.Text = a; Log("Авто: " + a); }
        else Log("Не нашёл GTA — выбери вручную.");
        RefreshAll();
    }

    private async void CheckUpdatesBtn_Click(object sender, RoutedEventArgs e) =>
        await CheckUpdatesAsync(showOverlay: true);

    private async void InstallBaseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!GtaOk()) { MessageBox.Show("Сначала укажи папку с GTA V."); return; }
        string gta = Gta();
        await RunBusy(log => Task.Run(() =>
        {
            int last = 0;
            MappingStore.InstallBase(gta, m => Dispatcher.Invoke(() => Log(m)),
                p => Dispatcher.Invoke(() => { BusyBar.IsIndeterminate = false; BusyBar.Value = p; last = p; }));
        }));
    }

    private async void RestoreBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!GtaOk()) return;
        string gta = Gta();
        await RunBusy(log => Task.Run(() => MappingStore.RestoreBackup(gta, m => Dispatcher.Invoke(() => Log(m)))));
    }

    private async void SpotToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not Button btn || btn.Tag is not Spot s) return;
        bool want = !s.IsInstalled;
        if (!GtaOk()) { MessageBox.Show("Сначала укажи папку с GTA V."); return; }
        string gta = Gta();
        await RunBusy(log => Task.Run(() =>
        {
            byte[]? data = null;
            if (want) data = File.ReadAllBytes(s.YmapFile);
            MappingStore.SetSpot(gta, s.YmapEntry, data, want, m => Dispatcher.Invoke(() => Log(m)));
        }));
    }
}
