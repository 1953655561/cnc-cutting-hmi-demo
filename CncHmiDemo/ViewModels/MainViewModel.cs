using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CncHmiDemo.Common;
using CncHmiDemo.Controls;
using CncHmiDemo.Models;
using CncHmiDemo.Services;
using Microsoft.Win32;

namespace CncHmiDemo.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly Brush OnlineBrush = CreateBrush(55, 211, 165);
    private static readonly Brush OfflineBrush = CreateBrush(122, 145, 176);
    private static readonly Brush RunningBrush = CreateBrush(47, 168, 255);
    private static readonly Brush PausedBrush = CreateBrush(255, 176, 32);
    private static readonly Brush AlarmBrush = CreateBrush(255, 92, 92);

    private readonly Dispatcher _dispatcher;
    private readonly HistoryStore _history = new();
    private readonly DispatcherTimer _clockTimer;
    private DeviceSimulator? _simulator;
    private IDeviceClient? _client;
    private DeviceSnapshot? _lastSnapshot;
    private CutJob? _currentJob;
    private DateTime _lastPathRender = DateTime.MinValue;
    private int _lastAlarmCode;
    private int _sampleCount;
    private int _frameCount;
    private bool _initialized;
    private bool _disposed;

    private string _connectionText = "未连接";
    private Brush _connectionBrush = OfflineBrush;
    private bool _isConnected;
    private bool _isTcpMode = true;
    private string _serialPortName = "COM3";
    private bool _simulatorRunning;

    private double _xPosition;
    private double _yPosition;
    private int _spindleRpm;
    private double _cutSpeed;
    private double _power;
    private int _progress;
    private string _stateText = "离线";
    private Brush _stateBrush = OfflineBrush;
    private string _alarmSummary = "无活动报警";
    private string _currentJobText = "当前无任务";
    private string _progressText = "0%";

    private string _statusLeft = "未连接";
    private string _statusCenter = "等待连接设备…";
    private string _statusRight = "—";
    private string _clockText = DateTime.Now.ToString("HH:mm:ss");

    private int _paramCutSpeed = 800;
    private int _paramPower = 60;
    private int _paramPulse = 20;
    private int _paramAccel = 500;
    private string _paramMessage = "连接设备后可读取 / 下发加工参数";
    private string _currentEndpoint = "TCP · 127.0.0.1:1502";

    private BitmapSource? _pathImage;
    private int _pathPointCount = SamplePath.Points.Count;
    private string _pathInfo = "等待设备数据…";

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Chart = new ChartBuffer(240);
        _history = new HistoryStore();

        StartSimulatorCommand = new RelayCommand(_ => StartSimulator());
        ConnectCommand = new RelayCommand(async _ => await ConnectAsync(), _ => !IsConnected);
        DisconnectCommand = new RelayCommand(async _ => await DisconnectAsync(), _ => IsConnected);
        AddJobCommand = new RelayCommand(_ => AddSampleJob());
        StartJobCommand = new RelayCommand(async _ => await StartJobAsync(), _ => IsConnected && _currentJob is null && Jobs.Any(j => j.Status == "排队中"));
        PauseJobCommand = new RelayCommand(async _ => await WriteControlAsync(2), _ => IsConnected && _currentJob is not null);
        StopJobCommand = new RelayCommand(async _ => await WriteControlAsync(3), _ => IsConnected && _currentJob is not null);
        ReadParamsCommand = new RelayCommand(_ => ReadParameters(), _ => IsConnected);
        WriteParamsCommand = new RelayCommand(async _ => await WriteParametersAsync(), _ => IsConnected);
        ClearAlarmsCommand = new RelayCommand(_ => ClearAlarms());
        ExportAlarmsCommand = new RelayCommand(_ => ExportAlarms());

        AddSampleJob("前围板-A01", "Q235", "3.0 mm");
        AddSampleJob("侧围板-B02", "SUS304", "2.0 mm");
        AddSampleJob("加强支架-C03", "6061-T6", "4.0 mm");

        UpdatePathPreview(0, 0, 0);
        AddSystemFrame("上位机启动，等待连接下位机");

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            ClockText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        };
        _clockTimer.Start();
    }

    public ObservableCollection<AlarmItem> Alarms { get; } = new();
    public ObservableCollection<CutJob> Jobs { get; } = new();
    public ObservableCollection<FrameLogItem> Frames { get; } = new();
    public ChartBuffer Chart { get; }

    public RelayCommand StartSimulatorCommand { get; }
    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand AddJobCommand { get; }
    public RelayCommand StartJobCommand { get; }
    public RelayCommand PauseJobCommand { get; }
    public RelayCommand StopJobCommand { get; }
    public RelayCommand ReadParamsCommand { get; }
    public RelayCommand WriteParamsCommand { get; }
    public RelayCommand ClearAlarmsCommand { get; }
    public RelayCommand ExportAlarmsCommand { get; }

    public string ConnectionText
    {
        get => _connectionText;
        set => Set(ref _connectionText, value);
    }

    public Brush ConnectionBrush
    {
        get => _connectionBrush;
        set => Set(ref _connectionBrush, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (Set(ref _isConnected, value)) UpdateCommandStates();
        }
    }

    public bool IsTcpMode
    {
        get => _isTcpMode;
        set
        {
            if (!Set(ref _isTcpMode, value)) return;
            OnPropertyChanged(nameof(IsSerialMode));
            CurrentEndpoint = value ? "TCP · 127.0.0.1:1502" : $"RTU · {SerialPortName}";
        }
    }

    public bool IsSerialMode
    {
        get => !_isTcpMode;
        set
        {
            if (value == IsSerialMode) return;
            IsTcpMode = !value;
        }
    }

    public string SerialPortName
    {
        get => _serialPortName;
        set
        {
            if (Set(ref _serialPortName, value) && IsSerialMode) CurrentEndpoint = $"RTU · {value}";
        }
    }

    public bool SimulatorRunning
    {
        get => _simulatorRunning;
        set => Set(ref _simulatorRunning, value);
    }

    public double XPosition
    {
        get => _xPosition;
        set => Set(ref _xPosition, value);
    }

    public double YPosition
    {
        get => _yPosition;
        set => Set(ref _yPosition, value);
    }

    public int SpindleRpm
    {
        get => _spindleRpm;
        set => Set(ref _spindleRpm, value);
    }

    public double CutSpeed
    {
        get => _cutSpeed;
        set => Set(ref _cutSpeed, value);
    }

    public double Power
    {
        get => _power;
        set => Set(ref _power, value);
    }

    public int Progress
    {
        get => _progress;
        set
        {
            if (Set(ref _progress, value)) ProgressText = value + "%";
        }
    }

    public string StateText
    {
        get => _stateText;
        set => Set(ref _stateText, value);
    }

    public Brush StateBrush
    {
        get => _stateBrush;
        set => Set(ref _stateBrush, value);
    }

    public string AlarmSummary
    {
        get => _alarmSummary;
        set => Set(ref _alarmSummary, value);
    }

    public string CurrentJobText
    {
        get => _currentJobText;
        set => Set(ref _currentJobText, value);
    }

    public string ProgressText
    {
        get => _progressText;
        set => Set(ref _progressText, value);
    }

    public string StatusLeft
    {
        get => _statusLeft;
        set => Set(ref _statusLeft, value);
    }

    public string StatusCenter
    {
        get => _statusCenter;
        set => Set(ref _statusCenter, value);
    }

    public string StatusRight
    {
        get => _statusRight;
        set => Set(ref _statusRight, value);
    }

    public string ClockText
    {
        get => _clockText;
        set => Set(ref _clockText, value);
    }

    public int FrameCount
    {
        get => _frameCount;
        set => Set(ref _frameCount, value);
    }

    public int ParamCutSpeed
    {
        get => _paramCutSpeed;
        set => Set(ref _paramCutSpeed, value);
    }

    public int ParamPower
    {
        get => _paramPower;
        set => Set(ref _paramPower, value);
    }

    public int ParamPulse
    {
        get => _paramPulse;
        set => Set(ref _paramPulse, value);
    }

    public int ParamAccel
    {
        get => _paramAccel;
        set => Set(ref _paramAccel, value);
    }

    public string ParamMessage
    {
        get => _paramMessage;
        set => Set(ref _paramMessage, value);
    }

    public string CurrentEndpoint
    {
        get => _currentEndpoint;
        set => Set(ref _currentEndpoint, value);
    }

    public BitmapSource? PathImage
    {
        get => _pathImage;
        set => Set(ref _pathImage, value);
    }

    public int PathPointCount
    {
        get => _pathPointCount;
        set => Set(ref _pathPointCount, value);
    }

    public string PathInfo
    {
        get => _pathInfo;
        set => Set(ref _pathInfo, value);
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        StartSimulator();
        await ConnectAsync();
    }

    private void StartSimulator()
    {
        try
        {
            if (_simulator is { IsRunning: true }) return;
            _simulator = new DeviceSimulator(1502);
            _simulator.Log += message => _dispatcher.BeginInvoke(() => AddSystemFrame(message));
            _simulator.Start();
            SimulatorRunning = true;
            StatusCenter = $"模拟下位机已启动：127.0.0.1:{_simulator.Port}";
        }
        catch (Exception ex)
        {
            SimulatorRunning = false;
            StatusCenter = "模拟器启动失败：" + ex.Message;
            AddSystemFrame("模拟器启动失败：" + ex.Message);
        }
    }

    private async Task ConnectAsync()
    {
        if (IsConnected) return;
        try
        {
            if (IsTcpMode)
            {
                if (_simulator is not { IsRunning: true }) StartSimulator();
                if (_simulator is null) throw new InvalidOperationException("模拟下位机未启动");
                _client = new TcpModbusClient("127.0.0.1", _simulator.Port);
            }
            else
            {
                var portName = SerialPortName.Trim();
                if (string.IsNullOrWhiteSpace(portName))
                {
                    StatusCenter = "请填写串口号，例如 COM3";
                    return;
                }

                _client = new SerialModbusClient(portName);
            }

            _client.SnapshotReceived += OnSnapshotReceived;
            _client.FrameLogged += OnFrameLogged;
            _client.ConnectionChanged += OnConnectionChanged;
            await _client.ConnectAsync().ConfigureAwait(true);

            IsConnected = true;
            ConnectionText = IsTcpMode ? "已连接 · TCP" : "已连接 · 串口";
            ConnectionBrush = OnlineBrush;
            StatusLeft = IsTcpMode
                ? $"Modbus TCP · 127.0.0.1:{_simulator!.Port} · Unit 1"
                : $"Modbus RTU · {SerialPortName} · 9600 8N1";
            StatusCenter = "连接成功，开始轮询保持寄存器（150 ms）";
            AddSystemFrame("连接成功，启动 Modbus 轮询");
            UpdateCommandStates();
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ConnectionBrush = OfflineBrush;
            ConnectionText = "连接失败";
            StatusCenter = "连接失败：" + ex.Message;
            AddSystemFrame("连接失败：" + ex.Message);
        }
    }

    private async Task DisconnectAsync()
    {
        if (_client is null) return;
        try
        {
            _client.SnapshotReceived -= OnSnapshotReceived;
            _client.FrameLogged -= OnFrameLogged;
            _client.ConnectionChanged -= OnConnectionChanged;
            await _client.DisconnectAsync().ConfigureAwait(true);
            _client.Dispose();
        }
        catch
        {
            // ignored
        }
        finally
        {
            _client = null;
            IsConnected = false;
            ConnectionBrush = OfflineBrush;
            ConnectionText = "未连接";
            StateText = "离线";
            StateBrush = OfflineBrush;
            StatusLeft = "未连接";
            StatusCenter = "已断开与下位机的连接";
            AddSystemFrame("断开连接");
            UpdateCommandStates();
        }
    }

    private void OnSnapshotReceived(DeviceSnapshot snapshot)
        => _dispatcher.BeginInvoke(() => ApplySnapshot(snapshot));

    private void ApplySnapshot(DeviceSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        _sampleCount++;

        XPosition = snapshot.X;
        YPosition = snapshot.Y;
        SpindleRpm = snapshot.SpindleRpm;
        CutSpeed = snapshot.CutSpeed;
        Power = snapshot.Power;
        Progress = snapshot.Progress;
        Chart.Append(snapshot.CutSpeed, snapshot.SpindleRpm);

        (StateText, StateBrush) = snapshot.State switch
        {
            MachineState.Running => ("运行中", RunningBrush),
            MachineState.Paused => ("暂停", PausedBrush),
            MachineState.Alarm => ("报警", AlarmBrush),
            MachineState.Idle => ("待机", PausedBrush),
            _ => ("离线", OfflineBrush)
        };

        AlarmSummary = snapshot.AlarmCode == 0
            ? "无活动报警"
            : $"报警代码 {snapshot.AlarmCode} · {AlarmMessage(snapshot.AlarmCode)}";

        CheckAlarm(snapshot);

        if (_currentJob is not null)
        {
            _currentJob.Progress = snapshot.Progress;
            if (snapshot.Progress >= 100 && _currentJob.Status == "加工中")
            {
                _currentJob.Status = "已完成";
                CurrentJobText = "当前无任务";
                _currentJob = null;
                StatusCenter = "加工完成，等待下一个任务";
                AddSystemFrame("任务加工完成");
                UpdateCommandStates();
            }
        }

        if ((DateTime.Now - _lastPathRender).TotalMilliseconds >= 120)
        {
            _lastPathRender = DateTime.Now;
            UpdatePathPreview(snapshot.Progress, snapshot.X, snapshot.Y);
        }

        StatusRight = $"{_sampleCount} 次采样 · {snapshot.Timestamp:HH:mm:ss}";
    }

    private void CheckAlarm(DeviceSnapshot snapshot)
    {
        if (snapshot.AlarmCode != 0 && snapshot.AlarmCode != _lastAlarmCode)
        {
            _lastAlarmCode = snapshot.AlarmCode;
            var item = new AlarmItem
            {
                Time = DateTime.Now,
                Level = snapshot.AlarmCode >= 200 ? "严重" : "警告",
                Code = snapshot.AlarmCode,
                Message = AlarmMessage(snapshot.AlarmCode)
            };
            Alarms.Insert(0, item);
            while (Alarms.Count > 200) Alarms.RemoveAt(Alarms.Count - 1);
            _history.AppendAlarm(item);
            StatusCenter = $"报警：{item.Message}（代码 {item.Code}）";
        }
        else if (snapshot.AlarmCode == 0)
        {
            _lastAlarmCode = 0;
        }
    }

    private static string AlarmMessage(int code) => code switch
    {
        101 => "冷却液液位低",
        102 => "伺服过载",
        103 => "气压不足",
        104 => "激光功率异常",
        _ => "设备报警"
    };

    private void UpdatePathPreview(double progress, double x, double y)
    {
        try
        {
            PathImage = GdiPathRenderer.Render(SamplePath.Points, new PathPoint(x, y), progress, 980, 560);
            PathPointCount = SamplePath.Points.Count;
            PathInfo = $"路径点数 {SamplePath.Points.Count} · 刀具坐标（{x:F1}, {y:F1}）mm · 进度 {progress:F1}%";
        }
        catch (Exception ex)
        {
            PathInfo = "路径渲染失败：" + ex.Message;
        }
    }

    private async Task StartJobAsync()
    {
        var next = Jobs.FirstOrDefault(j => j.Status == "排队中");
        if (next is null)
        {
            StatusCenter = "没有待加工任务，请先添加示例任务";
            return;
        }

        next.Status = "加工中";
        next.Progress = 0;
        _currentJob = next;
        CurrentJobText = $"{next.Name} · {next.Material} · {next.Thickness}";
        await WriteControlAsync(1);
        StatusCenter = $"任务已下发：{next.Name}";
        AddSystemFrame("下发启动指令（FC06 → 控制寄存器 20 = 1）");
        UpdateCommandStates();
    }

    private async Task WriteControlAsync(ushort value)
    {
        if (_client is null) return;
        try
        {
            await _client.WriteRegisterAsync(20, value).ConfigureAwait(true);
            AddSystemFrame($"写控制寄存器 20 = {value}（1 启动 / 2 暂停 / 3 停止 / 4 复位）");
        }
        catch (Exception ex)
        {
            StatusCenter = "指令下发失败：" + ex.Message;
        }
        UpdateCommandStates();
    }

    private void ReadParameters()
    {
        if (_lastSnapshot is null)
        {
            ParamMessage = "尚未收到设备数据";
            return;
        }

        ParamCutSpeed = (int)_lastSnapshot.CutSpeed;
        ParamPower = (int)_lastSnapshot.Power;
        ParamPulse = _lastSnapshot.PulseFreq;
        ParamAccel = (int)_lastSnapshot.Accel;
        ParamMessage = $"已读取设备当前参数（{DateTime.Now:HH:mm:ss}）";
    }

    private async Task WriteParametersAsync()
    {
        if (_client is null) return;
        try
        {
            await _client.WriteRegisterAsync(3, (ushort)Math.Clamp(ParamCutSpeed, 0, ushort.MaxValue)).ConfigureAwait(true);
            await _client.WriteRegisterAsync(4, (ushort)Math.Clamp(ParamPower, 0, ushort.MaxValue)).ConfigureAwait(true);
            await _client.WriteRegisterAsync(8, (ushort)Math.Clamp(ParamPulse, 0, ushort.MaxValue)).ConfigureAwait(true);
            await _client.WriteRegisterAsync(9, (ushort)Math.Clamp(ParamAccel, 0, ushort.MaxValue)).ConfigureAwait(true);

            _history.AppendParameter("切割速度", ParamCutSpeed.ToString());
            _history.AppendParameter("功率", ParamPower.ToString());
            _history.AppendParameter("脉冲频率", ParamPulse.ToString());
            _history.AppendParameter("加速度", ParamAccel.ToString());

            ParamMessage = $"参数下发成功（{DateTime.Now:HH:mm:ss}）";
            AddSystemFrame("参数下发：速度 / 功率 / 脉冲频率 / 加速度");
        }
        catch (Exception ex)
        {
            ParamMessage = "参数下发失败：" + ex.Message;
        }
    }

    private void AddSampleJob(string name, string material, string thickness)
    {
        Jobs.Add(new CutJob
        {
            Name = name,
            Material = material,
            Thickness = thickness,
            Status = "排队中"
        });
        UpdateCommandStates();
    }

    private void AddSampleJob()
    {
        var index = Jobs.Count + 1;
        var names = new[] { "门板-D", "支架-E", "底板-F", "连接板-G" };
        var materials = new[] { "Q235", "SUS304", "6061-T6" };
        AddSampleJob($"{names[index % names.Length]}{index:00}", materials[index % materials.Length], $"{2 + index % 3}.0 mm");
        StatusCenter = $"已添加示例任务 {index}";
    }

    private void ClearAlarms()
    {
        Alarms.Clear();
        _lastAlarmCode = 0;
        StatusCenter = "报警记录已清空";
    }

    private void ExportAlarms()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"alarms_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Title = "导出报警记录"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _history.ExportAlarms(dialog.FileName);
            StatusCenter = "报警记录已导出：" + dialog.FileName;
            AddSystemFrame("报警记录已导出");
        }
        catch (Exception ex)
        {
            StatusCenter = "导出失败：" + ex.Message;
        }
    }

    private void OnFrameLogged(string direction, string hex, string note)
        => _dispatcher.BeginInvoke(() =>
        {
            Frames.Insert(0, new FrameLogItem
            {
                Direction = direction,
                Hex = hex,
                Note = note
            });
            while (Frames.Count > 300) Frames.RemoveAt(Frames.Count - 1);
            FrameCount++;
            StatusCenter = $"{direction}  {Shorten(hex, 90)}";
        });

    private void OnConnectionChanged(bool connected, string message)
        => _dispatcher.BeginInvoke(() =>
        {
            IsConnected = connected;
            ConnectionBrush = connected ? OnlineBrush : OfflineBrush;
            ConnectionText = connected ? "已连接" : "未连接";
            if (!connected)
            {
                StateText = "离线";
                StateBrush = OfflineBrush;
                StatusLeft = "未连接";
            }
            StatusCenter = message;
            AddSystemFrame(message);
            UpdateCommandStates();
        });

    private void AddSystemFrame(string message)
    {
        Frames.Insert(0, new FrameLogItem
        {
            Direction = "SYS",
            Hex = "—",
            Note = message
        });
        while (Frames.Count > 300) Frames.RemoveAt(Frames.Count - 1);
        FrameCount++;
    }

    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    private void UpdateCommandStates()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        StartJobCommand.RaiseCanExecuteChanged();
        PauseJobCommand.RaiseCanExecuteChanged();
        StopJobCommand.RaiseCanExecuteChanged();
        ReadParamsCommand.RaiseCanExecuteChanged();
        WriteParamsCommand.RaiseCanExecuteChanged();
    }

    private static Brush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clockTimer.Stop();
        try
        {
            if (_client is not null)
            {
                _client.SnapshotReceived -= OnSnapshotReceived;
                _client.FrameLogged -= OnFrameLogged;
                _client.ConnectionChanged -= OnConnectionChanged;
                _client.Dispose();
                _client = null;
            }
        }
        catch
        {
            // ignored
        }

        _simulator?.Dispose();
    }
}
