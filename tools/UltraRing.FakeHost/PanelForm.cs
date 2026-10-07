using System;
using System.Drawing;
using System.Numerics;
using System.Windows.Forms;

namespace UltraRing.FakeHost;

/// <summary>Side control panel (kept out of the host window so the host client area is exactly the "game" view).</summary>
public sealed class PanelForm : Form
{
    private readonly HostSim _sim;
    private readonly TextBox _log = new TextBox
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Bottom, Height = 330,
        Font = new Font("Consolas", 8.5f), BackColor = Color.FromArgb(24, 24, 28), ForeColor = Color.Gainsboro,
    };

    public PanelForm(HostSim sim)
    {
        _sim = sim;
        Text = "FakeHost panel";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ClientSize = new Size(440, 520);
        ShowInTaskbar = false;

        int y = 8;
        AddButton("Hit me (100 HP)  [H]", 8, y, () => _sim.HitMe(100f));
        AddButton("Hit me (400 HP)", 224, y, () => _sim.HitMe(400f));
        y += 34;
        AddButton("Kill plane (hostDeaths++)  [K]", 8, y, () => _sim.KillPlane());
        AddButton("Respawn at spawn  [R]", 224, y, () => _sim.Respawn());
        y += 34;
        AddButton("Reset enemies", 8, y, () => _sim.ResetEnemies());
        var melee = new CheckBox { Text = "Enemy melee (<2 m)", Checked = _sim.MeleeEnabled, Left = 224, Top = y + 4, Width = 200 };
        melee.CheckedChanged += (s, e) => _sim.MeleeEnabled = melee.Checked;
        Controls.Add(melee);
        y += 38;

        Controls.Add(new Label { Text = "Spawn x/y/z", Left = 8, Top = y + 4, Width = 80 });
        var nx = Num(92, y, _sim.Spawn.X); var ny = Num(168, y, _sim.Spawn.Y); var nz = Num(244, y, _sim.Spawn.Z);
        AddButton("Apply + respawn", 322, y - 2, () =>
        {
            _sim.SetSpawn(new Vector3((float)nx.Value, (float)ny.Value, (float)nz.Value));
            _sim.Respawn();
        }, 110);
        y += 30;
        Controls.Add(new Label
        {
            Text = "Host window keys: H hit, K kill plane, R respawn, W/S/A/D walk (when not driven), F8 = mcSwitchReq++",
            Left = 8, Top = y, Width = 424, Height = 32,
        });

        Controls.Add(_log);
        Logger.Sink += Append;
        FormClosed += (s, e) => Logger.Sink -= Append;
    }

    private NumericUpDown Num(int x, int y, float v)
    {
        var n = new NumericUpDown { Left = x, Top = y, Width = 70, DecimalPlaces = 1, Minimum = -500, Maximum = 500, Value = (decimal)v, Increment = 1 };
        Controls.Add(n);
        return n;
    }

    private void AddButton(string text, int x, int y, Action click, int width = 208)
    {
        var b = new Button { Text = text, Left = x, Top = y, Width = width, Height = 28, TabStop = false };
        b.Click += (s, e) => click();
        Controls.Add(b);
    }

    private void Append(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => Append(line))); return; }
        string t = _log.Text;
        if (t.Length > 20000) t = t.Substring(t.Length - 12000);
        _log.Text = t + line + Environment.NewLine;
        _log.SelectionStart = _log.Text.Length;
        _log.ScrollToCaret();
    }
}
