using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using QuickRemote.PCClient.Models;
using QuickRemote.PCClient.Services;

namespace QuickRemote.PCClient.ViewModels;

/// <summary>
/// 主界面 ViewModel，管理所有状态与服务协调。
/// </summary>
public sealed class MainViewModel : BaseViewModel
{
    private readonly Logger _logger;
    private readonly ConfigService _configService;
    private readonly TunnelManager _tunnelManager;
    private readonly RelayConnection _relay;
    private readonly UpdateChecker _updateChecker;
    private readonly DispatcherTimer _refreshTimer;

    // 状态卡片
    private ConnectionStatus _status = ConnectionStatus.Disconnected;
    private string _serverAddressDisplay = string.Empty;
    private string _deviceId = string.Empty;
    private DateTime _lastHeartbeat;
    private string _version = App.Version;

    // RDP 配置
    private bool _rdpEnabled;
    private int _rdpPort = 3389;
    private bool _firewallEnabled;

    // 设置
    private string _serverAddressInput = string.Empty;
    private string _preSharedKeyInput = string.Empty;
    private bool _autoStart;
    private bool _checkUpdateOnStart;
    private bool _autoUploadLogs;

    // 更新
    private bool _isUpdateAvailable;
    private string _latestVersion = string.Empty;

    public MainViewModel()
    {
        _logger = App.Logger;
        _configService = App.ConfigService;

        _tunnelManager = new TunnelManager(_logger);
        _relay = new RelayConnection(_tunnelManager, _logger);
        _updateChecker = new UpdateChecker(App.Version, _logger);

        Sessions = new ObservableCollection<SessionInfo>();

        // 命令
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        CheckUpdateCommand = new RelayCommand(async () => await CheckUpdateAsync());
        ShowChangelogCommand = new RelayCommand(ShowChangelog);
        EnableRdpCommand = new RelayCommand(EnableRdp);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);

        // 订阅事件
        _relay.PropertyChanged += OnRelayPropertyChanged;
        _tunnelManager.SessionStarted += OnSessionStarted;
        _tunnelManager.SessionEnded += OnSessionEnded;
        _updateChecker.PropertyChanged += OnUpdateCheckerPropertyChanged;

        // 刷新定时器：更新心跳/时长文本
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) =>
        {
            OnPropertyChanged(nameof(HeartbeatText));
            foreach (var s in Sessions) s.RefreshDuration();
        };
        _refreshTimer.Start();

        LoadFromConfig();
    }

    // ========== 状态卡片属性 ==========

    public ConnectionStatus Status
    {
        get => _status;
        private set { SetField(ref _status, value); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusDotClass)); }
    }

    public string StatusText => _status switch
    {
        ConnectionStatus.Connected => "已连接",
        ConnectionStatus.Connecting => "连接中",
        ConnectionStatus.Reconnecting => "重连中",
        _ => "未连接"
    };

    /// <summary>状态指示灯 CSS 类名（用于样式切换）。</summary>
    public string StatusDotClass => _status switch
    {
        ConnectionStatus.Connected => "connected",
        ConnectionStatus.Connecting => "connecting",
        ConnectionStatus.Reconnecting => "connecting",
        _ => "disconnected"
    };

    public string ServerAddressDisplay
    {
        get => _serverAddressDisplay;
        private set => SetField(ref _serverAddressDisplay, value);
    }

    public string DeviceId
    {
        get => _deviceId;
        private set => SetField(ref _deviceId, value);
    }

    public DateTime LastHeartbeat
    {
        get => _lastHeartbeat;
        private set { SetField(ref _lastHeartbeat, value); OnPropertyChanged(nameof(HeartbeatText)); }
    }

    public string HeartbeatText
    {
        get
        {
            if (_status != ConnectionStatus.Connected || _lastHeartbeat == default)
                return "—";
            var delta = DateTime.Now - _lastHeartbeat;
            if (delta.TotalSeconds < 60) return $"{(int)delta.TotalSeconds}s 前";
            if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m 前";
            return _lastHeartbeat.ToString("HH:mm");
        }
    }

    /// <summary>连接状态详情消息。</summary>
    public string ConnectionMessage
    {
        get => _relay.LastMessage;
    }

    public string Version => _version;

    // ========== 会话列表 ==========

    public ObservableCollection<SessionInfo> Sessions { get; }

    public bool HasSessions => Sessions.Count > 0;

    // ========== RDP 配置 ==========

    public bool RdpEnabled
    {
        get => _rdpEnabled;
        private set { SetField(ref _rdpEnabled, value); OnPropertyChanged(nameof(RdpStatusText)); OnPropertyChanged(nameof(RdpNeedsEnable)); }
    }

    public int RdpPort
    {
        get => _rdpPort;
        private set { SetField(ref _rdpPort, value); OnPropertyChanged(nameof(RdpStatusText)); }
    }

    public string RdpStatusText => _rdpEnabled ? $"已启用 (端口 {_rdpPort})" : "未启用";

    public bool FirewallEnabled
    {
        get => _firewallEnabled;
        private set { SetField(ref _firewallEnabled, value); OnPropertyChanged(nameof(FirewallStatusText)); OnPropertyChanged(nameof(RdpNeedsEnable)); }
    }

    public string FirewallStatusText => _firewallEnabled ? "已放行" : "未放行";

    /// <summary>RDP 或防火墙未就绪时为 true，显示警告与一键启用按钮。</summary>
    public bool RdpNeedsEnable => !_rdpEnabled || !_firewallEnabled;

    // ========== 设置属性 ==========

    public string ServerAddressInput
    {
        get => _serverAddressInput;
        set => SetField(ref _serverAddressInput, value);
    }

    public string PreSharedKeyInput
    {
        get => _preSharedKeyInput;
        set => SetField(ref _preSharedKeyInput, value);
    }

    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (SetField(ref _autoStart, value))
            {
                AutoStartHelper.Apply(value);
                _configService.Config.AutoStart = value;
            }
        }
    }

    public bool CheckUpdateOnStart
    {
        get => _checkUpdateOnStart;
        set
        {
            if (SetField(ref _checkUpdateOnStart, value))
                _configService.Config.CheckUpdateOnStart = value;
        }
    }

    public bool AutoUploadLogs
    {
        get => _autoUploadLogs;
        set
        {
            if (SetField(ref _autoUploadLogs, value))
                _configService.Config.AutoUploadLogs = value;
        }
    }

    // ========== 更新 ==========

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set => SetField(ref _isUpdateAvailable, value);
    }

    public string LatestVersion
    {
        get => _latestVersion;
        private set => SetField(ref _latestVersion, value);
    }

    public UpdateChecker UpdateChecker => _updateChecker;

    // ========== 命令 ==========

    public ICommand SaveSettingsCommand { get; }
    public ICommand CheckUpdateCommand { get; }
    public ICommand ShowChangelogCommand { get; }
    public ICommand EnableRdpCommand { get; }
    public ICommand OpenLogFolderCommand { get; }

    // ========== 系统托盘 ==========

    public TrayService? Tray { get; private set; }

    /// <summary>是否正在真正退出（来自托盘退出菜单）。用于区分关闭到托盘与真正退出。</summary>
    public bool IsExiting { get; private set; }

    public void InitTray()
    {
        Tray = new TrayService();
        Tray.ShowMainWindowRequested += OnShowMainWindowRequested;
        Tray.ExitRequested += OnExitRequested;
        Tray.UpdateStatus(_status);
    }

    // ========== 初始化 ==========

    /// <summary>从配置加载并启动服务。</summary>
    public void Initialize()
    {
        // 检查 RDP 状态
        RefreshRdpStatus();

        // 自动启用 RDP（如果配置要求且未启用）
        if (_configService.Config.Rdp.AutoEnable && RdpNeedsEnable)
        {
            EnableRdp();
        }

        // 启动连接
        StartConnection();

        // 启动时检查更新
        if (_configService.Config.CheckUpdateOnStart)
        {
            _ = CheckUpdateAsync();
        }
    }

    private void LoadFromConfig()
    {
        var cfg = _configService.Config;
        ServerAddressInput = cfg.Server.Address;
        PreSharedKeyInput = cfg.Server.PreSharedKey;
        _autoStart = cfg.AutoStart;
        _checkUpdateOnStart = cfg.CheckUpdateOnStart;
        _autoUploadLogs = cfg.AutoUploadLogs;
        ServerAddressDisplay = cfg.Server.Address;
        OnPropertyChanged(nameof(AutoStart));
        OnPropertyChanged(nameof(CheckUpdateOnStart));
        OnPropertyChanged(nameof(AutoUploadLogs));
    }

    private void RefreshRdpStatus()
    {
        try
        {
            RdpEnabled = RdpConfigurator.IsRdpEnabled();
            RdpPort = RdpConfigurator.GetRdpPort();
            FirewallEnabled = RdpConfigurator.IsFirewallRuleEnabled();
            _logger.Info($"RDP status: enabled={RdpEnabled}, port={RdpPort}, firewall={FirewallEnabled}");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to check RDP status", ex);
        }
    }

    private void StartConnection()
    {
        var cfg = _configService.Config;
        var addr = string.IsNullOrWhiteSpace(ServerAddressInput) ? cfg.Server.Address : ServerAddressInput;
        ServerAddressDisplay = addr;
        _relay.Start(addr, cfg.Server.PreSharedKey, cfg.MachineId, cfg.Rdp.Port, App.Version);
    }

    // ========== 命令实现 ==========

    private void SaveSettings()
    {
        var cfg = _configService.Config;
        cfg.Server.Address = ServerAddressInput;
        cfg.Server.PreSharedKey = PreSharedKeyInput;
        cfg.AutoStart = AutoStart;
        cfg.CheckUpdateOnStart = CheckUpdateOnStart;
        cfg.AutoUploadLogs = AutoUploadLogs;
        _configService.Save();
        _logger.Info("Settings saved");

        // 重启连接以应用新地址
        StartConnection();

        MessageBox.Show("设置已保存并应用", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task CheckUpdateAsync()
    {
        await _updateChecker.CheckAsync(_configService.Config.QuickDeploy.ManifestUrl);

        // 检查完成后弹出结果提示
        if (!string.IsNullOrEmpty(_updateChecker.LastErrorMessage))
        {
            MessageBox.Show(_updateChecker.LastErrorMessage, "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (_updateChecker.IsUpdateAvailable)
        {
            var result = MessageBox.Show(
                $"发现新版本 v{_updateChecker.LatestVersion}\n当前版本 v{App.Version}\n\n是否立即下载并安装？",
                "更新可用",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes && !string.IsNullOrEmpty(_updateChecker.DownloadUrl))
            {
                await Services.AutoUpdater.UpdateAsync(_updateChecker.DownloadUrl, _updateChecker.LatestVersion, _logger);
            }
        }
        else
        {
            MessageBox.Show($"当前 v{App.Version} 已是最新版本", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ShowChangelog()
    {
        Views.ChangelogWindow.Show(_configService.Config.QuickDeploy.ChangelogUrl, _logger);
    }

    private void EnableRdp()
    {
        _logger.Info("Enabling RDP...");
        try
        {
            RdpConfigurator.EnableRdp(RdpPort);
            RefreshRdpStatus();
            _logger.Info($"RDP enabled={RdpEnabled}, firewall={FirewallEnabled}");

            if (RdpEnabled && FirewallEnabled)
            {
                MessageBox.Show($"RDP 已成功启用 (端口 {RdpPort})，防火墙规则已添加", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (!RdpEnabled && !FirewallEnabled)
            {
                MessageBox.Show(
                    "启用 RDP 失败，可能需要管理员权限。\n请右键点击程序 → 以管理员身份运行后重试。",
                    "需要管理员权限",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else if (!RdpEnabled)
            {
                MessageBox.Show("注册表修改失败，RDP 服务未开启。\n请以管理员身份运行后重试。", "部分失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show("RDP 服务已开启，但防火墙规则添加失败。\n请手动在 Windows 防火墙中放行 RDP 端口。", "部分失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("RDP enable failed", ex);
            MessageBox.Show($"启用 RDP 时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshRdpStatus();
        }
    }

    // ========== 事件处理（需切换到 UI 线程）==========

    private void OnRelayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(RelayConnection.Status):
                    Status = _relay.Status;
                    Tray?.UpdateStatus(_status);
                    break;
                case nameof(RelayConnection.DeviceId):
                    DeviceId = _relay.DeviceId;
                    break;
                case nameof(RelayConnection.LastHeartbeat):
                    LastHeartbeat = _relay.LastHeartbeat;
                    break;
                case nameof(RelayConnection.ServerAddress):
                    ServerAddressDisplay = _relay.ServerAddress;
                    break;
                case nameof(RelayConnection.LastMessage):
                    OnPropertyChanged(nameof(ConnectionMessage));
                    break;
            }
        });
    }

    private void OpenLogFolder()
    {
        try
        {
            var logDir = Logger.GetLogDirectory();
            if (System.IO.Directory.Exists(logDir))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = logDir,
                    UseShellExecute = true
                });
            else
                MessageBox.Show("日志目录不存在", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开日志目录失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSessionStarted(SessionInfo info)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            Sessions.Add(info);
            OnPropertyChanged(nameof(HasSessions));
            _logger.Info($"Session added: {info.DeviceName}, total={Sessions.Count}");
        });
    }

    private void OnSessionEnded(string sessionId)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            for (int i = 0; i < Sessions.Count; i++)
            {
                if (Sessions[i].SessionId == sessionId)
                {
                    Sessions.RemoveAt(i);
                    break;
                }
            }
            OnPropertyChanged(nameof(HasSessions));
            _logger.Info($"Session ended: {sessionId}, total={Sessions.Count}");
        });
    }

    private void OnUpdateCheckerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (e.PropertyName == nameof(UpdateChecker.IsUpdateAvailable))
                IsUpdateAvailable = _updateChecker.IsUpdateAvailable;
            else if (e.PropertyName == nameof(UpdateChecker.LatestVersion))
                LatestVersion = _updateChecker.LatestVersion;
        });
    }

    private void OnShowMainWindowRequested()
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var win = Application.Current.MainWindow;
            if (win != null)
            {
                win.Show();
                win.WindowState = WindowState.Normal;
                win.Activate();
            }
        });
    }

    private void OnExitRequested()
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            _logger.Info("Exit requested from tray");
            IsExiting = true;
            _relay.Stop();
            _tunnelManager.Dispose();
            Tray?.Dispose();
            Application.Current.Shutdown();
        });
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _relay.Stop();
        _tunnelManager.Dispose();
        Tray?.Dispose();
    }
}
