namespace AdbMonitor;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--selftest") return SelfTest(args.ElementAtOrDefault(1));
        if (args.FirstOrDefault() == "--scan")
        {
            foreach (var ip in Adb.Scan()) Console.WriteLine(ip);
            return 0;
        }

        bool demo = args.Contains("--demo");
        using var mutex = new Mutex(true, demo ? "AdbMonitor.Demo" : "AdbMonitor.SingleInstance", out bool first);
        if (!first) return 0;
        ApplicationConfiguration.Initialize();
        var form = new MainForm(demo);
        if (demo) form.Shown += (_, _) => { form.Apply(MainForm.Demo()); form.ShowCardFor(0); }; // 演示时直接展开一张卡片
        Application.Run(form);
        return 0;
    }

    /// <summary>解析与贴边逻辑的自检；传入 png 路径时额外把演示数据渲染成图片。</summary>
    static int SelfTest(string? png)
    {
        try
        {
            var devs = Adb.ParseDevices(
                "List of devices attached\r\n" +
                "R58M1A2B               device usb:1-4 product:a51 model:SM_A515F transport_id:1\r\n" +
                "192.168.1.5:5555       device product:x model:y transport_id:2\r\n" +
                "adb-R58M1A2B-AbCdEf._adb-tls-connect._tcp device product:x transport_id:3\r\n" +
                "emulator-5554          device\r\n" +
                "ZY22                   unauthorized usb:1-3 transport_id:4\r\n\r\n");
            Check(devs.Count == 4, "devices count");
            Check(devs[0] is { Key: "R58M1A2B", State: "device", Usb: true, Wifi: false }, "usb");
            Check(devs[1] is { Usb: false, Wifi: true }, "tcp wifi");
            Check(devs[2] is { Key: "R58M1A2B", Wifi: true }, "mdns key");
            Check(devs[3].State == "unauthorized", "unauthorized");

            var mdns = Adb.ParseMdns("List of discovered mdns services\n" +
                "adb-R58M1A2B-AbCdEf\t_adb-tls-connect._tcp\t192.168.1.7:41234\n" +
                "adb-R58M1A2B-AbCdEf\t_adb-tls-pairing._tcp\t192.168.1.7:37000\n");
            Check(mdns.Count == 1 && mdns["adb-R58M1A2B-AbCdEf"] == "192.168.1.7:41234", "mdns");
            Check(Adb.IpOf(devs[2].Serial, mdns) == "192.168.1.7", "ip mdns");
            Check(Adb.IpOf(devs[1].Serial, mdns) == "192.168.1.5", "ip tcp");
            Check(Adb.IpOf(devs[0].Serial, mdns) == null, "ip usb");

            var q = Adb.ParseShell(devs[1], "R58M1A2B\r\nSM-A515F\r\n13\r\nCurrent Battery Service state:\r\n" +
                "  AC powered: false\r\n  USB powered: true\r\n  Wireless powered: false\r\n" +
                "  status: 2\r\n  level: 85\r\n  scale: 100\r\n  temperature: 315\r\n");
            Check(q is { Key: "R58M1A2B", Model: "SM-A515F", Android: "13", Level: 85, Status: 2, Source: "USB", TempC: 31.5 }, "battery");
            Check(Adb.ParseShell(devs[0], "") == devs[0], "empty shell");

            var wa = new Rectangle(0, 0, 1920, 1040);
            Check(MainForm.Snap(new Rectangle(1855, 300, 56, 140), wa, 20).X == 1864, "snap right");
            Check(MainForm.Snap(new Rectangle(500, 10, 56, 140), wa, 20).Y == 0, "snap top");
            Check(MainForm.Snap(new Rectangle(500, 300, 56, 140), wa, 20).Location == new Point(500, 300), "no snap");
            Check(MainForm.EdgesOf(new Rectangle(1864, 0, 56, 140), wa) == (Edge.Right | Edge.Top), "edges");

            var dev = new Device { Key = "R58M1A2B", Model = "SM-A515F", Android = "13", State = "device", Usb = true, Wifi = true, Ip = "192.168.1.23", TempC = 31.5 };
            Check(dev.Rows().SequenceEqual([("连接", "USB · Wi-Fi 192.168.1.23"), ("设备", "SM-A515F · Android 13"), ("温度", "31.5 °C")]), "card rows");
            Check(dev.FullName == "SM-A515F (1A2B)" && new Device { Key = "X", Alias = "测试机" }.FullName == "测试机", "names");

            if (png != null)
            {
                ApplicationConfiguration.Initialize();
                using var f = new MainForm(demo: true);
                f.Apply(MainForm.Demo());
                f.Show();
                Application.DoEvents();
                using var bmp = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
                bmp.Save(png);

                // 逐台打开卡片并存图：充电中、正常（带别名）、低电量、未授权、已断开
                string? prev = null;
                for (int i = 0; i < 5; i++)
                {
                    f.ShowCardFor(i);
                    Application.DoEvents();
                    Check(f.Card.Visible && f.Card.Right <= f.Left, "card opens toward screen interior");
                    Check(f.Card.Title != prev, "clicking another device switches card at once");
                    prev = f.Card.Title;
                    using var cardBmp = new Bitmap(f.Card.Width, f.Card.Height);
                    f.Card.DrawToBitmap(cardBmp, new Rectangle(Point.Empty, f.Card.Size));
                    cardBmp.Save(Path.ChangeExtension(png, $".card{i}.png"));
                }
                f.ShowCardFor(4);
                Check(!f.Card.Visible, "clicking same device again closes card");
                Check(f.Right == Screen.PrimaryScreen!.WorkingArea.Right - f.LogicalToDeviceUnits(12), "docked window floats off the edge");

                // 卡片内的操作：直接向卡片发鼠标/键盘消息，走真实的命中测试和事件处理（不动全局鼠标键盘）
                f.ShowCardFor(4); // 已断开、没有别名的 Nexus 5X
                var card = f.Card;
                var box = (TextBox)card.Controls[0];
                int Px(int v) => card.LogicalToDeviceUnits(v);
                void Pencil() => Click(card, Px(272 - 16 - 16), Px(16 + 16));
                void Key(Keys k) => SendMessage(box.Handle, 0x100 /*WM_KEYDOWN*/, (IntPtr)(int)k, IntPtr.Zero);

                Pencil();
                Application.DoEvents();
                Check(box.Visible && box.Text == "Nexus 5X (0001)", "pencil opens rename prefilled with default name");
                using (var renameBmp = new Bitmap(card.Width, card.Height))
                {
                    card.DrawToBitmap(renameBmp, new Rectangle(Point.Empty, card.Size));
                    renameBmp.Save(Path.ChangeExtension(png, ".rename.png"));
                }
                Key(Keys.Enter);
                Check(!box.Visible && card.Title == "Nexus 5X (0001)", "unchanged default name is not saved as alias");
                Pencil(); box.Text = " 旧手机 "; Key(Keys.Enter);
                Check(!box.Visible && card.Title == "旧手机", "enter saves trimmed alias");
                Pencil(); box.Text = "别的"; Key(Keys.Escape);
                Check(!box.Visible && card.Title == "旧手机", "escape cancels rename");
                Pencil(); box.Text = ""; Pencil(); // 第二次点的是同一位置的对勾
                Check(!box.Visible && card.Title == "Nexus 5X (0001)", "empty name restores default");

                Click(card, card.Width / 2, card.Height - Px(16 + 18));
                Check(!card.Visible && f.Controls.Count == 4, "remove button removes disconnected device");
                f.ShowCardFor(0); // 在线设备的卡片没有移除按钮，同一位置点下去不应移除
                Click(card, card.Width / 2, card.Height - Px(16 + 18));
                Check(card.Visible && f.Controls.Count == 4, "online device cannot be removed");

                f.ToggleVisible();
                Check(!f.Visible && !card.Visible, "tray click hides window and card");
                f.ToggleVisible();
                Check(f.Visible && !card.Visible, "tray click shows window again");
            }
            Console.WriteLine("selftest OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>向控件发送一次左键单击（客户区坐标），只影响该窗口，不移动真实鼠标。</summary>
    static void Click(Control c, int x, int y)
    {
        var pos = (IntPtr)(y << 16 | x);
        SendMessage(c.Handle, 0x200 /*WM_MOUSEMOVE*/, IntPtr.Zero, pos);
        SendMessage(c.Handle, 0x201 /*WM_LBUTTONDOWN*/, 1, pos);
        SendMessage(c.Handle, 0x202 /*WM_LBUTTONUP*/, IntPtr.Zero, pos);
    }

    static void Check(bool ok, string what)
    {
        if (!ok) throw new Exception("FAIL: " + what);
    }
}
