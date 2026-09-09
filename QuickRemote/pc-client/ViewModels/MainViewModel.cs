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
    private readonly RemoteSessionManager _remoteSessionManager;
    private readonly RelayConnection _relay;
    private readonly UpdateChecker _updateChecker;
    private readonly DispatcherTimer _refreshTimer;
    private LanListener? _lanListener;
    // 已打开的远程查看窗口（deviceId → window），防止对同一设备重复开窗
    private readonly Dictionary<string, Views.RemoteViewerWindow> _viewerWindows = new();

    // 状态卡片
    private ConnectionStatus _status = ConnectionStatus.Disconnected;
    private string _serverAddressDisplay = string.Empty;
    private string _deviceId = string.Empty;
    private DateTime _lastHeartbeat;
    private string _version = App.Version;

    // 设置
    private string _serverAddressInput = string.Empty;
    private string _preSharedKeyInput = string.Empty;
    private string _deviceNameInput = string.Empty;
    private bool _autoStart;
    private bool _checkUpdateOnStart;
    private bool _autoUploadLogs;
    private string _accessCodeInput = string.Empty;
    private bool _lockOnDisconnect = true;

    // 更新
    private bool _isUpdateAvailable;
    private string _latestVersion = string.Empty;

    public MainViewModel()
    {
        _logger = App.Logger;
        _configService = App.ConfigService;

        _tunnelManager = new TunnelManager(_logger);
        _remoteSessionManager = new RemoteSessionManager(_logger);
        // 被控端会话参数默认值（HostConfig，无设置界面：被远程时的参数由主控端
        // configure 帧下发，此处仅作为旧客户端/异常路径的兜底默认）
        _remoteSessionManager.UpdateHostSettings(_configService.Config.Host);
        _relay = new RelayConnection(_tunnelManager, _remoteSessionManager, _logger);
        _updateChecker = new UpdateChecker(App.Version, _logger);

        Sessions = new ObservableCollection<SessionInfo>();
        RemoteDevices = new ObservableCollection<Models.RemoteDeviceInfo>();

        // 命令
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        CheckUpdateCommand = new RelayCommand(async () => await CheckUpdateAsync());
        ShowChangelogCommand = new RelayCommand(ShowChangelog);
        ExportConfigCommand = new RelayCommand(ExportConfig);
        ViewLogsCommand = new RelayCommand(ViewLogs);
        ConnectDeviceCommand = new RelayCommand(ConnectDevice);
        SetDeviceRemarkCommand = new RelayCommand(SetDeviceRemark);
        RemoveDeviceCommand = new RelayCommand(RemoveDevice);

        // 订阅事件
        _relay.PropertyChanged += OnRelayPropertyChanged;
        _relay.DeviceListUpdated += OnDeviceListUpdated;
        _relay.RenameResult += OnRenameResult;
        _tunnelManager.SessionStarted += OnSessionStarted;
        _tunnelManager.SessionEnded += OnSessionEnded;
        _remoteSessionManager.SessionStarted += OnSessionStarted;
        _remoteSessionManager.SessionEnded += OnSessionEnded;
        _updateChecker.PropertyChanged += OnUpdateCheckerPropertyChanged;

        // 刷新定时器：更新心跳/时长文本
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) =>
        {
            OnPropertyChanged(nameof(HeartbeatText));
            foreach (var s in Sessions) s.RefreshDuration();
        };
        _refreshTimer.Start();

        // 局域网直连监听（同网段 Android 优先直连，低延迟）
        StartLanListener();

        LoadFromConfig();
    }

    /// <summary>启动局域网直连监听（8447），并尝试添加防火墙入站规则（失败仅记录）。</summary>
    private void StartLanListener()
    {
        try
        {
            _lanListener = new LanListener(_logger, _remoteSessionManager,
                () => ComputeAuthKey(_configService.Config.Server.PreSharedKey));
            _lanListener.Start(LanListener.DefaultPort);
            TryAddFirewallRule(LanListener.DefaultPort);
        }
        catch (Exception ex)
        {
            _logger.Warn($"LAN listener init failed: {ex.Message}");
        }
    }

    /// <summary>auth_key = SHA256(psk) hex 小写（与 Android/relay 一致）。</summary>
    private static string ComputeAuthKey(string preSharedKey)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(preSharedKey));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>添加防火墙入站规则（8447 端口，所有网络配置文件）。失败仅警告，不影响功能。
    /// 不限定 profile：网络被识别为"公用"时 private 规则不生效，会导致局域网直连失败回退中继。</summary>
    private static void TryAddFirewallRule(int port)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall add rule name=\"QuickRemote LAN\" dir=in action=allow protocol=TCP localport={port}",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            App.Logger?.Warn($"Add firewall rule failed: {ex.Message}");
        }
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

    // ========== 远程设备列表（PC→PC 远程控制） ==========

    /// <summary>可远程设备（服务器全量广播，过滤本机；含离线设备）。</summary>
    public ObservableCollection<Models.RemoteDeviceInfo> RemoteDevices { get; }

    public bool HasRemoteDevices => RemoteDevices.Count > 0;

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

    /// <summary>本机设备名称（空 = 服务器分配默认名"PC客户端N"）。</summary>
    public string DeviceNameInput
    {
        get => _deviceNameInput;
        set => SetField(ref _deviceNameInput, value);
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

    // ========== 被远程安全（本机被其他设备连接时） ==========

    /// <summary>访问验证码（被控端）：非空时其他设备连接本机需先验证；空 = 不验证。</summary>
    public string AccessCodeInput
    {
        get => _accessCodeInput;
        set => SetField(ref _accessCodeInput, value);
    }

    /// <summary>连接断开时自动锁屏（被控端）。</summary>
    public bool LockOnDisconnect
    {
        get => _lockOnDisconnect;
        set
        {
            if (SetField(ref _lockOnDisconnect, value))
            {
                _configService.Config.LockOnDisconnect = value;
                _remoteSessionManager.LockOnDisconnect = value; // 活跃会话即时生效
            }
        }
    }

    // ========== 远程配置（本机作为主控端远程其他主机时的参数） ==========

    /// <summary>目标分辨率高度上限（0 = 原始分辨率不缩放；连接后经 configure 帧下发）。</summary>
    public int ViewerTargetMaxHeight
    {
        get => _configService.Config.Viewer.TargetMaxHeight;
        set
        {
            var v = Math.Max(0, value);
            if (_configService.Config.Viewer.TargetMaxHeight != v)
            {
                _configService.Config.Viewer.TargetMaxHeight = v;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>图像质量百分比（20-100，映射被控端编码码率缩放）。</summary>
    public int ViewerQualityPercent
    {
        get => _configService.Config.Viewer.QualityPercent;
        set
        {
            var v = Math.Clamp(value, 20, 100);
            if (_configService.Config.Viewer.QualityPercent != v)
            {
                _configService.Config.Viewer.QualityPercent = v;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>目标帧率（5-60）。</summary>
    public int ViewerFps
    {
        get => _configService.Config.Viewer.Fps;
        set
        {
            var v = Math.Clamp(value, 5, 60);
            if (_configService.Config.Viewer.Fps != v)
            {
                _configService.Config.Viewer.Fps = v;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>颜色深度（32 = 真彩色；16 = 高彩色，通道量化提升压缩率）。</summary>
    public int ViewerColorDepth
    {
        get => _configService.Config.Viewer.ColorDepth;
        set
        {
            var v = value == 16 ? 16 : 32;
            if (_configService.Config.Viewer.ColorDepth != v)
            {
                _configService.Config.Viewer.ColorDepth = v;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>局域网直连优先（同网段低延迟；关闭则强制走公网中继）。</summary>
    public bool ViewerPreferLan
    {
        get => _configService.Config.Viewer.PreferLan;
        set
        {
            if (_configService.Config.Viewer.PreferLan != value)
            {
                _configService.Config.Viewer.PreferLan = value;
                OnPropertyChanged();
            }
        }
    }

    // ========== 设置页下拉选项（值 + 显示文本） ==========

    /// <summary>分辨率高度上限选项（0 = 原始分辨率不缩放）。</summary>
    public IReadOnlyList<Models.OptionItem> MaxHeightOptions { get; } = new[]
    {
        new Models.OptionItem(0, "原始分辨率"),
        new Models.OptionItem(720, "720P 及以下"),
        new Models.OptionItem(1080, "1080P 及以下"),
        new Models.OptionItem(1440, "1440P 及以下"),
        new Models.OptionItem(2160, "2160P（4K）及以下"),
    };

    /// <summary>图像质量百分比选项。</summary>
    public IReadOnlyList<Models.OptionItem> QualityOptions { get; } = new[]
    {
        new Models.OptionItem(20, "20%（最省流量）"),
        new Models.OptionItem(40, "40%"),
        new Models.OptionItem(60, "60%"),
        new Models.OptionItem(80, "80%（推荐）"),
        new Models.OptionItem(100, "100%（最佳画质）"),
    };

    /// <summary>帧率选项。</summary>
    public IReadOnlyList<Models.OptionItem> FpsOptions { get; } = new[]
    {
        new Models.OptionItem(5, "5 fps"),
        new Models.OptionItem(10, "10 fps"),
        new Models.OptionItem(15, "15 fps"),
        new Models.OptionItem(20, "20 fps"),
        new Models.OptionItem(24, "24 fps"),
        new Models.OptionItem(30, "30 fps（流畅）"),
        new Models.OptionItem(45, "45 fps"),
        new Models.OptionItem(60, "60 fps（极流畅）"),
    };

    /// <summary>颜色深度选项。</summary>
    public IReadOnlyList<Models.OptionItem> ColorDepthOptions { get; } = new[]
    {
        new Models.OptionItem(32, "真彩色（32 位）"),
        new Models.OptionItem(16, "高彩色（16 位，省流量）"),
    };

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
    public ICommand ExportConfigCommand { get; }
    public ICommand ViewLogsCommand { get; }

    /// <summary>点击远程设备 → 打开远程查看窗口（参数：RemoteDeviceInfo）。</summary>
    public ICommand ConnectDeviceCommand { get; }

    /// <summary>设置远程设备备注（参数：RemoteDeviceInfo；仅本机可见，本地持久化）。</summary>
    public ICommand SetDeviceRemarkCommand { get; }

    /// <summary>软删除离线设备（参数：RemoteDeviceInfo；设备再次上线自动恢复）。</summary>
    public ICommand RemoveDeviceCommand { get; }

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
        DeviceNameInput = cfg.DeviceName;
        _autoStart = cfg.AutoStart;
        _checkUpdateOnStart = cfg.CheckUpdateOnStart;
        _autoUploadLogs = cfg.AutoUploadLogs;
        _accessCodeInput = cfg.AccessCode;
        _lockOnDisconnect = cfg.LockOnDisconnect;
        // 同步被控端安全参数到会话管理器（新连接立即生效）
        _remoteSessionManager.AccessCode = cfg.AccessCode;
        _remoteSessionManager.LockOnDisconnect = cfg.LockOnDisconnect;
        ServerAddressDisplay = cfg.Server.Address;
        OnPropertyChanged(nameof(AutoStart));
        OnPropertyChanged(nameof(CheckUpdateOnStart));
        OnPropertyChanged(nameof(AutoUploadLogs));
        OnPropertyChanged(nameof(AccessCodeInput));
        OnPropertyChanged(nameof(LockOnDisconnect));
        // 远程配置（直接读写 Config 对象，重载后通知 UI 刷新）
        OnPropertyChanged(nameof(ViewerTargetMaxHeight));
        OnPropertyChanged(nameof(ViewerQualityPercent));
        OnPropertyChanged(nameof(ViewerFps));
        OnPropertyChanged(nameof(ViewerColorDepth));
        OnPropertyChanged(nameof(ViewerPreferLan));
    }

    private void StartConnection()
    {
        var cfg = _configService.Config;
        var addr = string.IsNullOrWhiteSpace(ServerAddressInput) ? cfg.Server.Address : ServerAddressInput;
        ServerAddressDisplay = addr;
        // 远程连接统一走截屏方案（DXGI 捕获 + 编码 + 中继隧道），
        // 不再依赖系统 RDP（曾导致本地控制台黑屏，相关实现已彻底移除）。
        // rdpPort 仅为兼容服务端注册表字段，不用于实际连接。
        _relay.Start(addr, cfg.Server.PreSharedKey, cfg.MachineId, 3389, App.Version,
            DeviceNameInput?.Trim() ?? string.Empty);
    }

    // ========== 命令实现 ==========

    private void SaveSettings()
    {
        var cfg = _configService.Config;
        cfg.Server.Address = ServerAddressInput;
        cfg.Server.PreSharedKey = PreSharedKeyInput;

        // 设备名称：持久化 + 已连接时立即生效（rename 协议）
        var newName = (DeviceNameInput ?? string.Empty).Trim();
        var oldName = (cfg.DeviceName ?? string.Empty).Trim();
        cfg.DeviceName = newName;
        if (newName != oldName && _relay.Status == ConnectionStatus.Connected && newName.Length > 0)
        {
            _ = _relay.SendRenameAsync(newName); // 结果经 RenameResult 事件提示
        }

        cfg.AutoStart = AutoStart;
        cfg.CheckUpdateOnStart = CheckUpdateOnStart;
        cfg.AutoUploadLogs = AutoUploadLogs;

        // 被远程安全：验证码 + 断开锁屏（即时同步到会话管理器，新连接生效）
        cfg.AccessCode = (AccessCodeInput ?? string.Empty).Trim();
        _remoteSessionManager.AccessCode = cfg.AccessCode;
        cfg.LockOnDisconnect = LockOnDisconnect;
        _remoteSessionManager.LockOnDisconnect = LockOnDisconnect;

        _configService.Save();
        _logger.Info("Settings saved");

        // 重启连接以应用新地址
        StartConnection();

        Views.DialogWindow.Show("设置已保存并应用", "成功", Views.DialogWindow.DialogType.Success);
    }

    // ========== 远程设备（PC→PC 远程控制） ==========

    /// <summary>服务器设备列表广播 → 更新远程设备列表（过滤本机，含离线）。
    /// 同时合并本机私有状态：备注（DeviceRemarks）与软删除（HiddenDevices）；
    /// 软删除设备再次上线时自动恢复（移出隐藏列表并重新显示）。</summary>
    private void OnDeviceListUpdated(System.Collections.Generic.List<Models.RemoteDeviceInfo> devices)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var myId = _relay.DeviceId;
            var cfg = _configService.Config;
            var remarks = cfg.DeviceRemarks;
            var hidden = cfg.HiddenDevices;
            var revived = false;

            // 上线自动恢复：软删除设备出现在广播中且在线 → 移出隐藏列表
            foreach (var d in devices)
            {
                if (d.DeviceId != myId && d.IsOnline && hidden.Remove(d.DeviceId))
                    revived = true;
            }
            if (revived)
                _configService.Save();

            RemoteDevices.Clear();
            foreach (var d in devices)
            {
                if (d.DeviceId == myId) continue;      // 不展示自己
                if (hidden.Contains(d.DeviceId)) continue; // 软删除的设备不显示

                if (remarks.TryGetValue(d.DeviceId, out var remark))
                    d.Remark = remark ?? string.Empty;
                RemoteDevices.Add(d);
            }
            OnPropertyChanged(nameof(HasRemoteDevices));
        });
    }

    /// <summary>改名结果提示。</summary>
    private void OnRenameResult(bool ok, string message)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            Views.DialogWindow.Show(
                ok ? $"设备名称已更新为「{message}」" : $"设备名称修改失败：{message}",
                "设备名称", ok ? Views.DialogWindow.DialogType.Success : Views.DialogWindow.DialogType.Warning);
        });
    }

    /// <summary>点击远程设备 → 打开远程查看窗口（已打开则激活）。</summary>
    private void ConnectDevice(object? param)
    {
        if (param is not Models.RemoteDeviceInfo device) return;

        if (!device.IsOnline)
        {
            Views.DialogWindow.Show($"设备「{device.DisplayName}」当前离线，无法远程控制。\n设备上线后会自动更新状态。", "远程控制", Views.DialogWindow.DialogType.Warning);
            return;
        }

        if (_relay.Status != ConnectionStatus.Connected)
        {
            Views.DialogWindow.Show("未连接服务器，无法建立远程控制。\n请检查服务器地址与密钥后重试。", "远程控制", Views.DialogWindow.DialogType.Warning);
            return;
        }

        // 已打开：激活既有窗口
        if (_viewerWindows.TryGetValue(device.DeviceId, out var existing))
        {
            existing.Activate();
            return;
        }

        var preSharedKey = _configService.Config.Server.PreSharedKey;
        var serverAddress = ServerAddressDisplay;
        var win = new Views.RemoteViewerWindow(device, () =>
            // 执行时读取最新远程配置（含未保存的修改，连接即生效）
            Services.RemoteViewerClient.ConnectAsync(device, _relay, serverAddress, preSharedKey,
                _configService.Config.Viewer, App.Logger));
        _viewerWindows[device.DeviceId] = win;
        win.Closed += (_, _) => _viewerWindows.Remove(device.DeviceId);
        win.Show();
        win.Activate();
        _logger.Info($"Viewer window opened: {device.DeviceId} ({device.DisplayName})");
    }

    /// <summary>设置远程设备备注（列表项 ✎ 按钮）。备注仅本机可见、本地持久化，
    /// 不影响设备自身名称（名称由设备在其设置页自定义）；清空输入即删除备注。</summary>
    private void SetDeviceRemark(object? param)
    {
        if (param is not Models.RemoteDeviceInfo device) return;

        var remark = Views.InputDialogWindow.Show("设备备注",
            $"为「{device.DisplayName}」设置备注（仅本机可见，最长 64 字符；清空则删除备注）：",
            device.Remark, 64);
        if (remark == null) return; // 取消

        var trimmed = remark.Trim();
        var remarks = _configService.Config.DeviceRemarks;
        if (trimmed.Length == 0) remarks.Remove(device.DeviceId);
        else remarks[device.DeviceId] = trimmed;
        _configService.Save();

        // 同步当前列表项显示（广播周期较长，本地即时刷新）
        device.Remark = trimmed;
    }

    /// <summary>软删除离线设备（列表项 ✕ 按钮）：仅本机隐藏，设备再次上线时自动恢复显示。</summary>
    private void RemoveDevice(object? param)
    {
        if (param is not Models.RemoteDeviceInfo device) return;

        if (device.IsOnline)
        {
            Views.DialogWindow.Show("在线设备不可移除。\n设备离线后可从列表移除，再次上线时自动恢复。", "移除设备", Views.DialogWindow.DialogType.Info);
            return;
        }

        var confirmed = Views.DialogWindow.Confirm(
            $"确定将「{device.DisplayName}」从列表移除？\n\n仅在当前主机隐藏（软删除），不影响其他主机；该设备再次上线后将自动恢复显示。",
            "移除设备", Views.DialogWindow.DialogType.Question);
        if (!confirmed) return;

        _configService.Config.HiddenDevices.Add(device.DeviceId);
        _configService.Save();
        RemoteDevices.Remove(device);
        OnPropertyChanged(nameof(HasRemoteDevices));
        _logger.Info($"Device hidden (soft delete): {device.DeviceId} ({device.DisplayName})");
    }

    private async Task CheckUpdateAsync()
    {
        await _updateChecker.CheckAsync(_configService.Config.QuickDeploy.ManifestUrl);

        // 检查完成后弹出结果提示
        if (!string.IsNullOrEmpty(_updateChecker.LastErrorMessage))
        {
            Views.DialogWindow.Show(_updateChecker.LastErrorMessage, "检查更新", Views.DialogWindow.DialogType.Warning);
        }
        else if (_updateChecker.IsUpdateAvailable)
        {
            var result = Views.DialogWindow.Confirm(
                $"发现新版本 v{_updateChecker.LatestVersion}\n当前版本 v{App.Version}\n\n是否立即下载并安装？",
                "更新可用",
                Views.DialogWindow.DialogType.Question);
            if (result && !string.IsNullOrEmpty(_updateChecker.DownloadUrl))
            {
                // 启动独立更新程序（update.exe）完成下载/解压/覆盖/重启，
                // 成功后主进程会退出，因此这行之后的代码不会执行。
                Services.AutoUpdater.StartUpdate(
                    _updateChecker.DownloadUrl, _updateChecker.LatestVersion, _logger);
            }
        }
        else
        {
            Views.DialogWindow.Show($"当前 v{App.Version} 已是最新版本", "检查更新", Views.DialogWindow.DialogType.Info);
        }
    }

    private void ShowChangelog()
    {
        Views.ChangelogWindow.Show(_configService.Config.QuickDeploy.ChangelogUrl, _logger);
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

    private void ViewLogs()
    {
        Views.LogViewerWindow.ShowWindow();
    }

    /// <summary>导出当前配置为 JSON：优先复制到剪贴板，剪贴板被占用时回退到导出文件。</summary>
    private void ExportConfig()
    {
        string json;
        try
        {
            json = _configService.ExportJson();
        }
        catch (Exception ex)
        {
            _logger.Error("Export config failed", ex);
            Views.DialogWindow.Show($"导出配置失败：{ex.Message}", "错误", Views.DialogWindow.DialogType.Error);
            return;
        }

        // 优先复制到剪贴板（带重试）
        try
        {
            SetClipboardWithRetry(json);
            _logger.Info("Config exported to clipboard");
            Views.DialogWindow.Show("当前配置已复制到剪贴板，可直接粘贴到其他机器或分享。", "导出配置", Views.DialogWindow.DialogType.Success);
            return;
        }
        catch (Exception clipEx)
        {
            // 剪贴板被持续占用（远程控制/剪贴板工具），回退到导出文件
            _logger.Warn($"Clipboard unavailable, exporting to file: {clipEx.Message}");
        }

        // 回退：写入程序目录的配置文件
        try
        {
            var exportPath = System.IO.Path.Combine(AppContext.BaseDirectory, "config-export.json");
            System.IO.File.WriteAllText(exportPath, json);
            _logger.Info($"Config exported to file: {exportPath}");
            Views.DialogWindow.Show(
                $"剪贴板被其他程序持续占用（远程控制工具/剪贴板管理器），无法复制。\n\n配置已导出到文件：\n{exportPath}\n\n可直接打开该文件复制内容。",
                "导出配置", Views.DialogWindow.DialogType.Warning);
        }
        catch (Exception fileEx)
        {
            _logger.Error("Export config to file also failed", fileEx);
            Views.DialogWindow.Show($"导出配置失败：剪贴板被占用且写入文件也失败。\n\n{fileEx.Message}", "错误", Views.DialogWindow.DialogType.Error);
        }
    }

    /// <summary>
    /// 写入剪贴板，带重试。剪贴板是独占资源，常被远程控制/剪贴板工具短暂占用，
    /// OpenClipboard 失败（CLIPBRD_E_CANT_OPEN）多为临时现象，重试几次即可。
    /// </summary>
    private static void SetClipboardWithRetry(string text)
    {
        const int maxRetries = 10;
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return;
            }
            catch (System.Runtime.InteropServices.COMException ex)
                when (ex.HResult == unchecked((int)0x800401D0)) // CLIPBRD_E_CANT_OPEN
            {
                if (i == maxRetries - 1) throw;
                // 递增延迟，给占用剪贴板的进程时间释放
                System.Threading.Thread.Sleep(50 + i * 20);
            }
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

    /// <summary>
    /// 托盘「退出」：终止进程。逐项清理均加保护，任何一步异常都不影响退出；
    /// Shutdown 优雅关闭后兜底 Environment.Exit 强杀，确保托盘退出后进程必然终止。
    /// </summary>
    private void OnExitRequested()
    {
        _logger.Info("Exit requested from tray");
        try
        {
            // 先标记 IsExiting，让主窗口 OnClosing 放行（不再隐藏到托盘）
            IsExiting = true;

            try { _relay.Stop(); } catch (Exception ex) { _logger.Warn($"Relay stop error: {ex.Message}"); }
            try { _tunnelManager.Dispose(); } catch (Exception ex) { _logger.Warn($"Tunnel dispose error: {ex.Message}"); }
            try { _remoteSessionManager.Dispose(); } catch (Exception ex) { _logger.Warn($"Session dispose error: {ex.Message}"); }
            try { Tray?.Dispose(); } catch (Exception ex) { _logger.Warn($"Tray dispose error: {ex.Message}"); }
        }
        finally
        {
            // 优雅关闭（触发 OnClosing 与 OnExit）；若被任何原因阻断，1.5s 后强制结束进程
            try { Application.Current?.Shutdown(); } catch { }
            Task.Run(async () =>
            {
                await Task.Delay(1500);
                try { Environment.Exit(0); } catch { }
            });
        }
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _relay.Stop();
        _tunnelManager.Dispose();
        try { _lanListener?.Dispose(); } catch { }
        Tray?.Dispose();
    }
}
