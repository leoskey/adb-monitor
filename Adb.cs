using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace AdbMonitor;

/// <summary>一次轮询看到的一条 adb 连接。Key 是用来合并同一台手机的标识（优先 ro.serialno）。</summary>
public sealed record Live(string Key, string Serial, string State, bool Usb, bool Wifi, string? Ip,
    string? Model = null, string? Android = null, int? Level = null, int Status = 0,
    string? Source = null, double? TempC = null);

public static class Adb
{
    public static string? Exe { get; } = Find();

    static string? Find()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim().Trim('"'), "adb.exe");
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Android\Sdk\platform-tools\adb.exe");
        return File.Exists(sdk) ? sdk : null;
    }

    /// <summary>先单独拉起 adb 服务（不重定向输出），避免后台服务继承管道导致读取卡住。</summary>
    public static void StartServer()
    {
        using var p = Process.Start(new ProcessStartInfo(Exe!, "start-server") { UseShellExecute = false, CreateNoWindow = true });
        p?.WaitForExit(15000);
    }

    static string Run(int timeoutMs, params string[] args)
    {
        var psi = new ProcessStartInfo(Exe!)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            return "";
        }
        return stdout.Wait(1000) ? stdout.Result : "";
    }

    public static List<Live> Poll()
    {
        var devices = ParseDevices(Run(5000, "devices", "-l"));
        var mdns = ParseMdns(Run(5000, "mdns", "services"));

        // 局域网里已广播无线调试、但还没连上的设备，补一次 adb connect（未配对的会直接失败）
        // ponytail: 每轮都会重试未配对设备，若拖慢轮询再加退避
        bool connected = false;
        foreach (var (name, addr) in mdns)
            if (!devices.Any(d => d.Serial == addr || d.Serial.StartsWith(name + "._", StringComparison.Ordinal)))
                connected |= Run(5000, "connect", addr).Contains("connected to");
        // 旧式无线调试（PICO 等，固定 5555 端口、不广播 mDNS）：定期探测本网段并自动连接
        if (DateTime.Now >= _nextScan)
        {
            _nextScan = DateTime.Now.AddSeconds(ScanSeconds);
            foreach (var ip in Scan())
                if (!devices.Any(d => d.Serial.StartsWith(ip + ":", StringComparison.Ordinal)))
                    connected |= Run(5000, "connect", $"{ip}:5555").Contains("connected to");
        }
        if (connected) devices = ParseDevices(Run(5000, "devices", "-l"));

        var tasks = devices.Select(d => Task.Run(() => Query(d with { Ip = IpOf(d.Serial, mdns) }))).ToArray();
        Task.WaitAll(tasks);
        return tasks.Select(t => t.Result).ToList();
    }

    const int ScanSeconds = 30;
    static DateTime _nextScan;

    /// <summary>
    /// 探测本机所在私有网段里所有监听 5555 端口的 adbd。不能只看端口是否开放：
    /// 代理/加速器会代答所有 TCP 连接，所以发一个 ADB CNXN 包，只认回 CNXN/AUTH/STLS 的主机。
    /// 不发送公钥，不会在设备上弹出授权框。
    /// </summary>
    public static List<IPAddress> Scan()
    {
        var found = new System.Collections.Concurrent.ConcurrentBag<IPAddress>();
        Parallel.ForEachAsync(Candidates(), new ParallelOptions { MaxDegreeOfParallelism = 128 }, async (ip, _) =>
        {
            if (await IsAdb(ip)) found.Add(ip);
        }).Wait();
        return found.ToList();
    }

    /// <summary>有 IPv4 网关的已启用网卡上、前缀 22~30 位的私有网段（排除 Hyper-V 虚拟网卡、Tailscale 等）。</summary>
    static IEnumerable<IPAddress> Candidates()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            var props = ni.GetIPProperties();
            if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.PrefixLength is < 22 or > 30) continue;
                var b = ua.Address.GetAddressBytes();
                if (!(b[0] == 10 || b[0] == 172 && (b[1] & 0xF0) == 16 || b[0] == 192 && b[1] == 168)) continue;
                uint self = BinaryPrimitives.ReadUInt32BigEndian(b), mask = uint.MaxValue << (32 - ua.PrefixLength);
                for (uint h = (self & mask) + 1; h < (self | ~mask); h++)
                    if (h != self) yield return new IPAddress(BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(h) : h);
            }
        }
    }

    static readonly byte[] Cnxn = BuildCnxn();

    static byte[] BuildCnxn()
    {
        var payload = Encoding.ASCII.GetBytes("host::\0");
        var p = new byte[24 + payload.Length];
        uint sum = 0;
        foreach (var x in payload) sum += x;
        uint[] header = [0x4e584e43 /*CNXN*/, 0x01000001, 256 * 1024, (uint)payload.Length, sum, 0x4e584e43 ^ 0xffffffff];
        for (int i = 0; i < header.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(i * 4), header[i]);
        payload.CopyTo(p, 24);
        return p;
    }

    static async Task<bool> IsAdb(IPAddress ip)
    {
        using var c = new TcpClient();
        using var cts = new CancellationTokenSource(1000);
        try
        {
            await c.ConnectAsync(ip, 5555, cts.Token);
            var s = c.GetStream();
            await s.WriteAsync(Cnxn, cts.Token);
            var buf = new byte[4];
            await s.ReadExactlyAsync(buf, cts.Token);
            return Encoding.ASCII.GetString(buf) is "CNXN" or "AUTH" or "STLS";
        }
        catch { return false; }
    }

    static Live Query(Live d) => d.State != "device" ? d : ParseShell(d, Run(5000, "-s", d.Serial, "shell",
        "getprop ro.serialno;getprop ro.product.model;getprop ro.build.version.release;dumpsys battery"));

    public static List<Live> ParseDevices(string text)
    {
        var list = new List<Live>();
        foreach (var line in text.Split('\n'))
        {
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2 || line.StartsWith("List of") || line.StartsWith('*')) continue;
            var serial = p[0];
            if (serial.StartsWith("emulator-")) continue; // ponytail: 模拟器没有真实电池，直接跳过
            bool wifi = serial.Contains(':') || serial.Contains("._adb");
            list.Add(new Live(KeyOf(serial), serial, p[1], !wifi, wifi, null));
        }
        return list;
    }

    /// <summary>mDNS 连接的序列号形如 adb-&lt;serialno&gt;-&lt;随机6位&gt;._adb-tls-connect._tcp，取出 serialno 便于和 USB 合并。</summary>
    public static string KeyOf(string serial)
    {
        int i = serial.IndexOf("._adb", StringComparison.Ordinal);
        if (i < 0) return serial;
        var name = serial[..i];
        int j = name.LastIndexOf('-');
        return name.StartsWith("adb-") && j > 4 ? name[4..j] : name;
    }

    /// <summary>实例名 → ip:port，只保留无线调试的连接服务（不含配对服务）。</summary>
    public static Dictionary<string, string> ParseMdns(string text)
    {
        var map = new Dictionary<string, string>();
        foreach (var line in text.Split('\n'))
        {
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 3 && p[1].StartsWith("_adb-tls-connect")) map[p[0]] = p[2];
        }
        return map;
    }

    public static string? IpOf(string serial, Dictionary<string, string> mdns)
    {
        int i = serial.IndexOf("._adb", StringComparison.Ordinal);
        if (i >= 0) return mdns.TryGetValue(serial[..i], out var a) ? a[..a.LastIndexOf(':')] : null;
        int c = serial.LastIndexOf(':');
        return c > 0 ? serial[..c] : null;
    }

    /// <summary>解析 shell 输出：前三行是 serialno / model / Android 版本，其余是 dumpsys battery。</summary>
    public static Live ParseShell(Live d, string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        if (lines.Length < 4) return d;
        string? Line(int i) => string.IsNullOrWhiteSpace(lines[i]) ? null : lines[i].Trim();
        var kv = new Dictionary<string, string>();
        foreach (var l in lines.Skip(3))
        {
            var p = l.Split(':', 2);
            if (p.Length == 2) kv.TryAdd(p[0].Trim(), p[1].Trim());
        }
        int? Int(string k) => kv.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : null;

        int? level = Int("level");
        int scale = Int("scale") ?? 100;
        if (level != null && scale > 0) level = level * 100 / scale;
        string? source = kv.GetValueOrDefault("AC powered") == "true" ? "AC"
            : kv.GetValueOrDefault("USB powered") == "true" ? "USB"
            : kv.GetValueOrDefault("Wireless powered") == "true" ? "无线充电" : null;
        var sn = Line(0);
        return d with
        {
            Key = sn is null or "unknown" ? d.Key : sn,
            Model = Line(1), Android = Line(2), Level = level,
            Status = Int("status") ?? 0, Source = source, TempC = Int("temperature") / 10.0,
        };
    }
}
