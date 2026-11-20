using System.IO;
using System.Text;
using CncHmiDemo.Models;

namespace CncHmiDemo.Services;

/// <summary>报警与参数变更的本地记录（CSV），用于演示数据落盘与导出。</summary>
public sealed class HistoryStore
{
    private readonly string _directory;
    private readonly string _alarmFile;
    private readonly string _parameterFile;
    private readonly object _sync = new();

    public HistoryStore()
    {
        _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CncHmiDemo");
        Directory.CreateDirectory(_directory);
        _alarmFile = Path.Combine(_directory, "alarms.csv");
        _parameterFile = Path.Combine(_directory, "parameters.csv");
    }

    public string DirectoryPath => _directory;
    public string AlarmFile => _alarmFile;

    public void AppendAlarm(AlarmItem item)
    {
        lock (_sync)
        {
            var line = $"{item.Time:yyyy-MM-dd HH:mm:ss},{item.Level},{item.Code},{item.Message}{Environment.NewLine}";
            File.AppendAllText(_alarmFile, line, new UTF8Encoding(true));
        }
    }

    public void AppendParameter(string name, string value)
    {
        lock (_sync)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{name},{value}{Environment.NewLine}";
            File.AppendAllText(_parameterFile, line, new UTF8Encoding(true));
        }
    }

    public void ExportAlarms(string targetPath)
    {
        lock (_sync)
        {
            if (File.Exists(_alarmFile))
            {
                File.Copy(_alarmFile, targetPath, true);
            }
            else
            {
                File.WriteAllText(targetPath, "时间,级别,代码,描述" + Environment.NewLine, new UTF8Encoding(true));
            }
        }
    }
}
