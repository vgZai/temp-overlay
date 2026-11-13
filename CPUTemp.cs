using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TempForm());
    }
}

internal sealed class ChipColumn
{
    public readonly Panel Panel = new Panel();
    public readonly Label Name = new Label();
    public readonly Label Temp = new Label();
    public readonly Label Detail = new Label();

    public ChipColumn()
    {
        Panel.Dock = DockStyle.Fill;
        Panel.Padding = new Padding(6, 4, 6, 4);

        Name.Dock = DockStyle.Top;
        Name.Height = 22;
        Name.ForeColor = Color.FromArgb(170, 170, 175);
        Name.Font = new Font("Segoe UI", 8.5f);
        Name.TextAlign = ContentAlignment.MiddleCenter;
        Name.AutoEllipsis = false;
        Name.Text = "";

        Detail.Dock = DockStyle.Bottom;
        Detail.Height = 22;
        Detail.ForeColor = Color.FromArgb(190, 190, 195);
        Detail.Font = new Font("Segoe UI", 8.5f);
        Detail.TextAlign = ContentAlignment.MiddleCenter;
        Detail.Text = "";

        Temp.Dock = DockStyle.Fill;
        Temp.Font = new Font("Segoe UI", 28f, FontStyle.Bold);
        Temp.TextAlign = ContentAlignment.MiddleCenter;
        Temp.Text = "-- C";

        Panel.Controls.Add(Temp);
        Panel.Controls.Add(Name);
        Panel.Controls.Add(Detail);
    }
}

internal sealed class TempForm : Form
{
    readonly Computer _computer = new Computer
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true
    };
    readonly ChipColumn _cpuCol = new ChipColumn();
    readonly ChipColumn _gpuCol = new ChipColumn();
    readonly Timer _timer = new Timer();
    IHardware _cpu;
    IHardware _gpu;
    string _openError;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0)
            DwmSetWindowAttribute(Handle, 19, ref on, 4);
    }

    public TempForm()
    {
        Text = "Temp";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        MaximizeBox = false;
        TopMost = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(400, 148);
        BackColor = Color.FromArgb(20, 20, 22);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9f);

        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - 18, wa.Bottom - Height - 18);

        var table = new TableLayoutPanel();
        table.Dock = DockStyle.Fill;
        table.ColumnCount = 2;
        table.RowCount = 1;
        table.Padding = new Padding(2);
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        table.Controls.Add(_cpuCol.Panel, 0, 0);
        table.Controls.Add(_gpuCol.Panel, 1, 0);
        Controls.Add(table);

        try { _computer.Open(); }
        catch (Exception ex) { _openError = ex.Message; }

        foreach (var hw in _computer.Hardware)
        {
            if (_cpu == null && hw.HardwareType == HardwareType.Cpu)
                _cpu = hw;
            if (hw.HardwareType == HardwareType.GpuAmd ||
                hw.HardwareType == HardwareType.GpuNvidia ||
                hw.HardwareType == HardwareType.GpuIntel)
            {
                if (_gpu == null || hw.HardwareType == HardwareType.GpuAmd)
                    _gpu = hw;
            }
        }

        _timer.Interval = 1000;
        _timer.Tick += delegate { Tick(); };
        Shown += delegate { Tick(); _timer.Start(); };
        FormClosed += delegate
        {
            _timer.Stop();
            try { _computer.Close(); } catch { }
        };
    }

    static void RefreshHardware(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware)
            RefreshHardware(sub);
    }

    static void Collect(IHardware hw, List<ISensor> into)
    {
        into.AddRange(hw.Sensors);
        foreach (var sub in hw.SubHardware)
            Collect(sub, into);
    }

    static float? Named(IList<ISensor> sensors, SensorType type, params string[] needles)
    {
        foreach (var s in sensors)
        {
            if (s.SensorType != type || !s.Value.HasValue)
                continue;
            foreach (var n in needles)
            {
                if (s.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                    return s.Value;
            }
        }
        return null;
    }

    static float? FirstTemp(IList<ISensor> sensors)
    {
        foreach (var s in sensors)
        {
            if (s.SensorType == SensorType.Temperature && s.Value.HasValue)
                return s.Value;
        }
        return null;
    }

    static void PaintTemp(Label label, float v)
    {
        label.Text = string.Format("{0:0} C", v);
        if (v >= 89) label.ForeColor = Color.FromArgb(255, 72, 72);
        else if (v >= 80) label.ForeColor = Color.FromArgb(255, 160, 60);
        else if (v >= 70) label.ForeColor = Color.FromArgb(240, 210, 70);
        else label.ForeColor = Color.FromArgb(90, 210, 120);
    }

    static string ShortName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        name = name.Replace("AMD ", "").Replace("NVIDIA ", "").Replace("GeForce ", "");
        return name;
    }

    void PaintCpu(ChipColumn col)
    {
        if (_cpu == null)
        {
            col.Name.Text = "";
            col.Temp.Text = "-- C";
            col.Temp.ForeColor = Color.Silver;
            col.Detail.Text = "not found";
            return;
        }

        try { RefreshHardware(_cpu); }
        catch (Exception ex)
        {
            col.Detail.Text = ex.Message;
            return;
        }

        col.Name.Text = ShortName(_cpu.Name);
        var sensors = new List<ISensor>();
        Collect(_cpu, sensors);

        float? pkg = Named(sensors, SensorType.Temperature, "Package");
        float? tctl = Named(sensors, SensorType.Temperature, "Tctl", "Tdie");
        float? ccd = Named(sensors, SensorType.Temperature, "CCD");
        float? load = Named(sensors, SensorType.Load, "Total");
        float? any = pkg ?? tctl ?? ccd ?? FirstTemp(sensors);

        if (!any.HasValue)
        {
            col.Temp.Text = "-- C";
            col.Temp.ForeColor = Color.Silver;
            col.Detail.Text = "no sensors";
            return;
        }

        PaintTemp(col.Temp, any.Value);

        var bits = new List<string>();
        if (tctl.HasValue && tctl != any) bits.Add("tctl " + tctl.Value.ToString("0.0"));
        else if (pkg.HasValue && pkg != any) bits.Add("pkg " + pkg.Value.ToString("0.0"));
        if (load.HasValue) bits.Add("load " + load.Value.ToString("0") + "%");
        col.Detail.Text = string.Join("  ", bits.ToArray());
    }

    void PaintGpu(ChipColumn col)
    {
        if (_gpu == null)
        {
            col.Name.Text = "";
            col.Temp.Text = "-- C";
            col.Temp.ForeColor = Color.Silver;
            col.Detail.Text = "not found";
            return;
        }

        try { RefreshHardware(_gpu); }
        catch (Exception ex)
        {
            col.Detail.Text = ex.Message;
            return;
        }

        col.Name.Text = ShortName(_gpu.Name);
        var sensors = new List<ISensor>();
        Collect(_gpu, sensors);

        float? core = Named(sensors, SensorType.Temperature, "GPU Core", "Core");
        if (!core.HasValue) core = Named(sensors, SensorType.Temperature, "Temperature");
        float? hot = Named(sensors, SensorType.Temperature, "Hot Spot", "Hotspot", "Junction");
        float? mem = Named(sensors, SensorType.Temperature, "Memory", "VRAM");
        float? load = Named(sensors, SensorType.Load, "GPU Core", "Core", "D3D");
        if (!load.HasValue) load = Named(sensors, SensorType.Load, "Total");
        float? any = core ?? hot ?? FirstTemp(sensors);

        if (!any.HasValue)
        {
            col.Temp.Text = "-- C";
            col.Temp.ForeColor = Color.Silver;
            col.Detail.Text = "no sensors";
            return;
        }

        PaintTemp(col.Temp, any.Value);

        var bits = new List<string>();
        if (hot.HasValue && hot != any) bits.Add("hotspot " + hot.Value.ToString("0"));
        if (mem.HasValue && mem != any && mem != hot) bits.Add("mem " + mem.Value.ToString("0"));
        if (load.HasValue) bits.Add("load " + load.Value.ToString("0") + "%");
        col.Detail.Text = string.Join("  ", bits.ToArray());
    }

    void Tick()
    {
        if (_openError != null)
        {
            _cpuCol.Temp.Text = "err";
            _cpuCol.Temp.ForeColor = Color.FromArgb(255, 90, 90);
            _cpuCol.Detail.Text = _openError;
            _gpuCol.Temp.Text = "err";
            _gpuCol.Temp.ForeColor = Color.FromArgb(255, 90, 90);
            _gpuCol.Detail.Text = "";
            return;
        }

        PaintCpu(_cpuCol);
        PaintGpu(_gpuCol);
    }
}

