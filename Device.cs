using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdbMonitor;

[Flags]
public enum Edge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

/// <summary>设备的显示状态：正常、低电量、充电中、需要处理（未授权/离线）、已断开。</summary>
public enum Tone { Ok, Low, Charging, Warn, Off }

public sealed class Device
{
    // 属性会写入 state.json
    public string Key { get; set; } = "";
    public string? Alias { get; set; }
    public string? Model { get; set; }
    public string? Android { get; set; }
    public int? Level { get; set; }
    public DateTime LastSeen { get; set; }

    // 字段只在运行时使用（System.Text.Json 默认不序列化字段）
    public string? State;   // null = 已断开；否则是 adb 状态：device / unauthorized / offline ...
    public bool Usb, Wifi;
    public string? Ip, Source;
    public int Status;      // dumpsys battery 的 status：2 充电中，5 已充满
    public double? TempC;

    [JsonIgnore] public bool Online => State == "device";
    [JsonIgnore] public bool Charging => Online && Status == 2;
    [JsonIgnore] public string Last4 => Key.Length > 4 ? Key[^4..] : Key;
    [JsonIgnore] public string ShortName => Alias ?? Last4;
    [JsonIgnore] public string DefaultName => $"{Model ?? "未知设备"} ({Last4})";
    [JsonIgnore] public string FullName => Alias ?? DefaultName;

    [JsonIgnore]
    public Tone Tone => State == null ? Tone.Off : !Online ? Tone.Warn : Charging ? Tone.Charging : Level < 20 ? Tone.Low : Tone.Ok;

    /// <summary>卡片的大号文字和旁边的状态说明。</summary>
    public (string Big, string Sub) Headline(DateTime now) => State switch
    {
        null => (Level is int l ? $"{l}%" : "--", $"{Ago(now - LastSeen)}断开"),
        "device" => ($"{Level?.ToString() ?? "--"}%",
            Status == 2 ? (Source != null ? $"{Source} 充电中" : "充电中") : Status == 5 ? "已充满" : "未充电"),
        "unauthorized" => ("未授权", "在设备上允许 USB 调试"),
        _ => ("离线", $"adb 状态：{State}"),
    };

    /// <summary>卡片下半部分的「标签 / 值」（序列号由卡片单独画，带复制按钮）。</summary>
    public List<(string, string)> Rows()
    {
        var rows = new List<(string, string)>();
        if (State != null)
        {
            var conn = new List<string>();
            if (Usb) conn.Add("USB");
            if (Wifi) conn.Add(Ip != null ? $"Wi-Fi {Ip}" : "Wi-Fi");
            rows.Add(("连接", string.Join(" · ", conn)));
        }
        if (Model != null) rows.Add(("设备", Android != null ? $"{Model} · Android {Android}" : Model));
        if (Online && TempC != null) rows.Add(("温度", $"{TempC:0.0} °C"));
        return rows;
    }

    static string Ago(TimeSpan t) =>
        t.TotalMinutes < 1 ? "刚刚" :
        t.TotalHours < 1 ? $"{(int)t.TotalMinutes} 分钟前" :
        t.TotalDays < 1 ? $"{(int)t.TotalHours} 小时前" : $"{(int)t.TotalDays} 天前";
}

public sealed class AppState
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public Edge Edge { get; set; }
    public List<Device> Devices { get; set; } = new();

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdbMonitor", "state.json");

    public static AppState Load()
    {
        try { return JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
