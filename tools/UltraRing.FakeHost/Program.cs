using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows.Forms;
using UltraRing.Link;

namespace UltraRing.FakeHost;

public static class Logger
{
    public static event Action<string> Sink;
    private static string _file;

    public static void Line(string s)
    {
        string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + s;
        try
        {
            _file ??= BridgePaths.File("fakehost.log");
            Directory.CreateDirectory(Path.GetDirectoryName(_file));
            File.AppendAllText(_file, line + Environment.NewLine);
        }
        catch { }
        Sink?.Invoke(line);
    }
}

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string dir = null;
        double exitAfter = 0;
        Vector3 spawn = Vector3.Zero;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dir":
                    if (i + 1 < args.Length) dir = args[++i];
                    break;
                case "--exit-after":
                    if (i + 1 < args.Length) double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out exitAfter);
                    break;
                case "--spawn":
                    if (i + 1 < args.Length)
                    {
                        string[] p = args[++i].Split(',');
                        if (p.Length == 3 &&
                            float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                            float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                            float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                            spawn = new Vector3(x, y, z);
                    }
                    break;
                case "-h":
                case "--help":
                    MessageBox.Show("UltraRing.FakeHost [--dir <bridge dir>] [--spawn x,y,z] [--exit-after <seconds>]", "FakeHost");
                    return 0;
            }
        }
        if (!string.IsNullOrEmpty(dir)) Environment.SetEnvironmentVariable("ERMC_DIR", Path.GetFullPath(dir));

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var link = new HostLink();
        if (!link.Open())
        {
            string msg = "Cannot open bridge.shm in " + BridgePaths.Dir + ": " + link.LastError;
            Logger.Line(msg);
            MessageBox.Show(msg, "FakeHost", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        Logger.Line($"opened {link.Path} (pid {Environment.ProcessId}, coreStatus=1)");
        Application.Run(new FakeHostForm(link, exitAfter, spawn));
        return 0;
    }
}
