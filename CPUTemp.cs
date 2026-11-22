using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

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

internal struct SensorSnapshot
{
    public bool CpuTempOk;
    public float CpuTemp;
    public bool CcdTempOk;
    public float CcdTemp;
    public bool CpuLoadOk;
    public float CpuLoad;
    public string CpuError;

    public bool GpuOk;
    public string GpuName;
    public float GpuCoreTemp;
    public bool GpuHotOk;
    public float GpuHotTemp;
    public bool GpuMemOk;
    public float GpuMemTemp;
    public bool GpuLoadOk;
    public float GpuLoad;
    public string GpuError;
}

internal sealed class CpuInfo
{
    public readonly string DisplayName;
    public readonly bool IsSupportedAmd;
    public readonly uint CcdSmnAddress;
    public readonly float TctlOffset;

    private CpuInfo(string displayName, bool isSupportedAmd, uint ccdSmnAddress, float tctlOffset)
    {
        DisplayName = displayName;
        IsSupportedAmd = isSupportedAmd;
        CcdSmnAddress = ccdSmnAddress;
        TctlOffset = tctlOffset;
    }

    public static CpuInfo Detect()
    {
        const string key = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0";
        string rawName = Registry.GetValue(key, "ProcessorNameString", null) as string;
        string identifier = Registry.GetValue(key, "Identifier", null) as string;
        string vendor = Registry.GetValue(key, "VendorIdentifier", null) as string;

        string shortName = ShortenChipName(rawName);
        int family;
        int model;
        TryParseIntAfter(identifier, "Family ", out family);
        TryParseIntAfter(identifier, "Model ", out model);

        bool isAmd = string.Equals(vendor, "AuthenticAMD", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(rawName) && rawName.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0);

        // AMDFamily17.bin supports AMD Family 17h (23) and 19h (25) SMU/SMN reads.
        bool supported = isAmd && (family == 23 || family == 25);

        uint ccdAddr = 0u;
        if ((family == 25 && model == 0x21) || (family == 23 && model == 0x71))
            ccdAddr = 0x00059954u;
        else if (family == 25 && (model == 0x61 || model == 0x44))
            ccdAddr = 0x00059B08u;

        float offset = 0f;
        if (!string.IsNullOrEmpty(rawName))
        {
            if (rawName.IndexOf("1600X", StringComparison.OrdinalIgnoreCase) >= 0 ||
                rawName.IndexOf("1700X", StringComparison.OrdinalIgnoreCase) >= 0 ||
                rawName.IndexOf("1800X", StringComparison.OrdinalIgnoreCase) >= 0)
                offset = -20f;
            else if (rawName.IndexOf("Threadripper 19", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     rawName.IndexOf("Threadripper 29", StringComparison.OrdinalIgnoreCase) >= 0)
                offset = -27f;
            else if (rawName.IndexOf("2700X", StringComparison.OrdinalIgnoreCase) >= 0)
                offset = -10f;
        }

        return new CpuInfo(shortName, supported, ccdAddr, offset);
    }

    public static string ShortenChipName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "";
        string s = name.Replace("AMD ", "").Replace("NVIDIA ", "").Replace("GeForce ", "");
        s = Regex.Replace(s, @"\s+\d+-Core Processor\b", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+Processor\b", "", RegexOptions.IgnoreCase);
        return s.Trim();
    }

    static bool TryParseIntAfter(string text, string token, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text))
            return false;
        int idx = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return false;
        int start = idx + token.Length;
        int end = start;
        while (end < text.Length && text[end] >= '0' && text[end] <= '9')
            end++;
        if (end == start)
            return false;
        return int.TryParse(text.Substring(start, end - start), out value);
    }
}

internal sealed class PawnIoSmnReader : IDisposable
{
    const uint IoctlLoadBinary = 0xA1B22084u;
    const uint IoctlExecute = 0xA1B22104u;
    const uint SmnTctlTemp = 0x00059800u;
    const uint FlagTempOffset49 = 0x00080000u;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    readonly SafeFileHandle _handle;
    readonly Mutex _pciMutex;
    readonly byte[] _inBuf;
    readonly byte[] _outBuf;
    bool _loaded;

    public string Error { get; private set; }
    public bool IsReady { get { return _loaded && _handle != null && !_handle.IsInvalid && !_handle.IsClosed; } }

    public PawnIoSmnReader()
    {
        _inBuf = new byte[40];
        _outBuf = new byte[8];
        Encoding.ASCII.GetBytes("ioctl_read_smn", 0, 14, _inBuf, 0);

        _handle = CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", 3u, 3u, IntPtr.Zero, 3u, 0x80u, IntPtr.Zero);
        if (_handle == null || _handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 5)
                Error = "no admin";
            else if (err == 2 || err == 3)
                Error = "no PawnIO";
            else
                Error = "PawnIO err " + err;
            return;
        }

        byte[] blob = LoadEmbeddedBlob("AMDFamily17.bin");
        if (blob == null || blob.Length == 0)
        {
            Error = "missing AMDFamily17.bin";
            return;
        }

        int ignored;
        if (!DeviceIoControl(_handle, IoctlLoadBinary, blob, blob.Length, null, 0, out ignored, IntPtr.Zero))
        {
            Error = "PawnIO load " + Marshal.GetLastWin32Error();
            return;
        }

        _pciMutex = OpenPciMutex();
        _loaded = true;
    }

    static byte[] LoadEmbeddedBlob(string resourceName)
    {
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
        {
            if (s == null)
                return null;
            byte[] data = new byte[s.Length];
            int offset = 0;
            while (offset < data.Length)
            {
                int read = s.Read(data, offset, data.Length - offset);
                if (read <= 0)
                    break;
                offset += read;
            }
            return data;
        }
    }

    static Mutex OpenPciMutex()
    {
        try
        {
            return new Mutex(false, @"Global\Access_PCI");
        }
        catch
        {
            try
            {
                return Mutex.OpenExisting(@"Global\Access_PCI");
            }
            catch
            {
                return null;
            }
        }
    }

    bool TryReadSmn(uint address, out uint value)
    {
        value = 0u;
        if (!IsReady)
            return false;

        _inBuf[32] = (byte)address;
        _inBuf[33] = (byte)(address >> 8);
        _inBuf[34] = (byte)(address >> 16);
        _inBuf[35] = (byte)(address >> 24);
        _inBuf[36] = 0;
        _inBuf[37] = 0;
        _inBuf[38] = 0;
        _inBuf[39] = 0;

        int bytesReturned;
        if (!DeviceIoControl(_handle, IoctlExecute, _inBuf, _inBuf.Length, _outBuf, _outBuf.Length, out bytesReturned, IntPtr.Zero))
            return false;
        if (bytesReturned < 4)
            return false;

        value = (uint)(_outBuf[0] | (_outBuf[1] << 8) | (_outBuf[2] << 16) | (_outBuf[3] << 24));
        return true;
    }

    public bool ReadTemps(uint ccdSmnAddr, float tctlOffset, out bool tctlOk, out float tctl, out bool ccdOk, out float ccd)
    {
        tctlOk = false;
        tctl = 0f;
        ccdOk = false;
        ccd = 0f;

        if (!IsReady)
            return false;

        bool locked = false;
        if (_pciMutex != null)
        {
            try
            {
                locked = _pciMutex.WaitOne(10);
            }
            catch (AbandonedMutexException)
            {
                locked = true;
            }
            if (!locked)
                return false;
        }

        try
        {
            uint raw;
            if (!TryReadSmn(SmnTctlTemp, out raw))
                return false;

            bool rangeOffset = (raw & FlagTempOffset49) != 0u;
            float val = ((raw >> 21) * 125u) * 0.001f;
            if (rangeOffset)
                val -= 49.0f;
            val += tctlOffset;

            tctl = val;
            tctlOk = true;

            if (ccdSmnAddr != 0u)
            {
                uint ccdReg;
                if (TryReadSmn(ccdSmnAddr, out ccdReg))
                {
                    uint rawCcd = ccdReg & 0xFFFu;
                    if (rawCcd > 0u)
                    {
                        float v = ((int)(rawCcd * 125u) - 305000) * 0.001f;
                        if (v > 0f && v < 125f)
                        {
                            ccd = v;
                            ccdOk = true;
                        }
                    }
                }
            }

            return true;
        }
        finally
        {
            if (locked)
            {
                try { _pciMutex.ReleaseMutex(); }
                catch { }
            }
        }
    }

    public void Dispose()
    {
        _loaded = false;
        if (_handle != null)
            _handle.Dispose();
        if (_pciMutex != null)
            _pciMutex.Dispose();
    }
}

internal sealed class CpuLoadReader
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemTimes(out ulong lpIdleTime, out ulong lpKernelTime, out ulong lpUserTime);

    ulong _prevIdle;
    ulong _prevKernel;
    ulong _prevUser;
    bool _primed;

    public CpuLoadReader()
    {
        _primed = GetSystemTimes(out _prevIdle, out _prevKernel, out _prevUser);
    }

    public bool TryRead(out float load)
    {
        load = 0f;
        ulong idle;
        ulong kernel;
        ulong user;
        if (!GetSystemTimes(out idle, out kernel, out user))
            return false;

        if (!_primed)
        {
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            _primed = true;
            return false;
        }

        ulong dIdle = idle - _prevIdle;
        ulong dKernel = kernel - _prevKernel;
        ulong dUser = user - _prevUser;
        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;

        // GetSystemTimes reports kernel time inclusive of idle time.
        ulong total = dKernel + dUser;
        if (total == 0ul)
            return false;

        double busy = total >= dIdle ? (double)(total - dIdle) : 0.0;
        double pct = (busy * 100.0) / total;
        if (pct < 0.0) pct = 0.0;
        else if (pct > 100.0) pct = 100.0;

        load = (float)pct;
        return true;
    }
}

internal sealed class AdlGpuReader : IDisposable
{
    const int AdlOk = 0;
    const int PmSensorEdgeTemp = 8;
    const int PmSensorMemTemp = 9;
    const int PmSensorGfxLoad = 19;
    const int PmSensorHotspotTemp = 27;
    const int PmSensorSlots = 256;
    const int PmBufSize = 4 + PmSensorSlots * 8;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr AdlMainMemoryAlloc(int size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct AdlAdapterInfo
    {
        public int Size;
        public int AdapterIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string UDID;
        public int BusNumber;
        public int DeviceNumber;
        public int FunctionNumber;
        public int VendorID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AdapterName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DisplayName;
        public int Present;
        public int Exist;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPathExt;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string PNPString;
        public int OSDisplayIndex;
    }

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int ADL2_Main_Control_Create(AdlMainMemoryAlloc callback, int enumConnectedAdapters, out IntPtr context);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int ADL2_Main_Control_Destroy(IntPtr context);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, out int numAdapters);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int ADL2_Adapter_AdapterInfo_Get(IntPtr context, IntPtr info, int inputSize);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int ADL2_New_QueryPMLogData_Get(IntPtr context, int adapterIndex, IntPtr data);

    // Keep delegate reference alive for the lifetime of the ADL context.
    readonly AdlMainMemoryAlloc _allocCallback;
    readonly List<IntPtr> _adlAllocs = new List<IntPtr>();
    readonly object _allocLock = new object();

    IntPtr _ctx;
    IntPtr _pmBuf;
    int _adapterIndex = -1;

    public string GpuName { get; private set; }
    public string Error { get; private set; }
    public bool IsReady { get { return _ctx != IntPtr.Zero && _adapterIndex >= 0 && _pmBuf != IntPtr.Zero; } }

    public AdlGpuReader()
    {
        GpuName = "";
        _allocCallback = AllocAdlMemory;
        _pmBuf = Marshal.AllocHGlobal(PmBufSize);

        try
        {
            int rc = ADL2_Main_Control_Create(_allocCallback, 1, out _ctx);
            if (rc != AdlOk || _ctx == IntPtr.Zero)
            {
                _ctx = IntPtr.Zero;
                Error = "ADL err " + rc;
                return;
            }

            SelectAdapter();
        }
        catch (DllNotFoundException)
        {
            Error = "no AMD ADL";
        }
        catch (EntryPointNotFoundException)
        {
            Error = "old AMD ADL";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    IntPtr AllocAdlMemory(int size)
    {
        IntPtr ptr = Marshal.AllocHGlobal(size);
        lock (_allocLock)
            _adlAllocs.Add(ptr);
        return ptr;
    }

    void SelectAdapter()
    {
        int count;
        int rc = ADL2_Adapter_NumberOfAdapters_Get(_ctx, out count);
        if (rc != AdlOk || count <= 0)
        {
            Error = "no AMD GPU";
            return;
        }

        int infoSize = Marshal.SizeOf(typeof(AdlAdapterInfo));
        int totalBytes = infoSize * count;
        IntPtr buf = Marshal.AllocHGlobal(totalBytes);
        try
        {
            rc = ADL2_Adapter_AdapterInfo_Get(_ctx, buf, totalBytes);
            if (rc != AdlOk)
            {
                Error = "ADL info " + rc;
                return;
            }

            var seenBuses = new HashSet<int>();
            int fallbackIndex = -1;
            string fallbackName = "";

            for (int i = 0; i < count; i++)
            {
                AdlAdapterInfo info = (AdlAdapterInfo)Marshal.PtrToStructure(IntPtr.Add(buf, i * infoSize), typeof(AdlAdapterInfo));
                if (!seenBuses.Add(info.BusNumber))
                    continue;

                Marshal.WriteInt32(_pmBuf, 0, PmBufSize);
                if (ADL2_New_QueryPMLogData_Get(_ctx, info.AdapterIndex, _pmBuf) != AdlOk)
                    continue;

                string shortName = CpuInfo.ShortenChipName(info.AdapterName);
                if (info.Present != 0)
                {
                    _adapterIndex = info.AdapterIndex;
                    GpuName = shortName;
                    return;
                }

                if (fallbackIndex < 0)
                {
                    fallbackIndex = info.AdapterIndex;
                    fallbackName = shortName;
                }
            }

            if (fallbackIndex >= 0)
            {
                _adapterIndex = fallbackIndex;
                GpuName = fallbackName;
            }
            else
            {
                Error = "no AMD GPU";
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    bool ReadSlot(int sensorId, out float value)
    {
        int offset = 4 + sensorId * 8;
        int supported = Marshal.ReadInt32(_pmBuf, offset);
        if (supported == 0)
        {
            value = 0f;
            return false;
        }
        value = Marshal.ReadInt32(_pmBuf, offset + 4);
        return true;
    }

    public bool TryRead(
        out bool coreOk, out float coreTemp,
        out bool hotOk, out float hotTemp,
        out bool memOk, out float memTemp,
        out bool loadOk, out float load)
    {
        coreOk = false; coreTemp = 0f;
        hotOk = false; hotTemp = 0f;
        memOk = false; memTemp = 0f;
        loadOk = false; load = 0f;

        if (!IsReady)
            return false;

        Marshal.WriteInt32(_pmBuf, 0, PmBufSize);
        int rc = ADL2_New_QueryPMLogData_Get(_ctx, _adapterIndex, _pmBuf);
        if (rc != AdlOk)
        {
            Error = "ADL err " + rc;
            return false;
        }

        coreOk = ReadSlot(PmSensorEdgeTemp, out coreTemp);
        hotOk = ReadSlot(PmSensorHotspotTemp, out hotTemp);
        memOk = ReadSlot(PmSensorMemTemp, out memTemp);
        loadOk = ReadSlot(PmSensorGfxLoad, out load);
        return coreOk || hotOk;
    }

    public void Dispose()
    {
        _adapterIndex = -1;

        if (_ctx != IntPtr.Zero)
        {
            try { ADL2_Main_Control_Destroy(_ctx); }
            catch { }
            _ctx = IntPtr.Zero;
        }

        if (_pmBuf != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_pmBuf);
            _pmBuf = IntPtr.Zero;
        }

        lock (_allocLock)
        {
            for (int i = 0; i < _adlAllocs.Count; i++)
                Marshal.FreeHGlobal(_adlAllocs[i]);
            _adlAllocs.Clear();
        }
    }
}

internal sealed class CompactLabel : Control
{
    const TextFormatFlags DrawFlags =
        TextFormatFlags.HorizontalCenter |
        TextFormatFlags.VerticalCenter |
        TextFormatFlags.SingleLine |
        TextFormatFlags.NoPrefix |
        TextFormatFlags.NoPadding;

    public CompactLabel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        TabStop = false;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        string text = Text;
        if (!string.IsNullOrEmpty(text))
            TextRenderer.DrawText(e.Graphics, text, Font, ClientRectangle, ForeColor, BackColor, DrawFlags);
    }
}

internal sealed class ChipColumn
{
    static readonly Color PanelBack = Color.FromArgb(20, 20, 22);

    public readonly Panel Panel = new Panel();
    public readonly CompactLabel Name = new CompactLabel();
    public readonly CompactLabel Temp = new CompactLabel();
    public readonly CompactLabel Detail = new CompactLabel();

    public ChipColumn(Font smallFont, Font tempFont, float dpiScale)
    {
        Panel.Dock = DockStyle.Fill;
        Panel.Margin = Padding.Empty;
        Panel.BackColor = PanelBack;
        Panel.Padding = new Padding(Scale(2, dpiScale), Scale(3, dpiScale), Scale(2, dpiScale), Scale(3, dpiScale));

        Name.Dock = DockStyle.Top;
        Name.Height = Scale(16, dpiScale);
        Name.BackColor = PanelBack;
        Name.ForeColor = Color.FromArgb(170, 170, 175);
        Name.Font = smallFont;
        Name.Text = "";

        Detail.Dock = DockStyle.Bottom;
        Detail.Height = Scale(16, dpiScale);
        Detail.BackColor = PanelBack;
        Detail.ForeColor = Color.FromArgb(190, 190, 195);
        Detail.Font = smallFont;
        Detail.Text = "";

        Temp.Dock = DockStyle.Fill;
        Temp.BackColor = PanelBack;
        Temp.Font = tempFont;
        Temp.ForeColor = Color.Silver;
        Temp.Text = "-- C";

        Panel.Controls.Add(Temp);
        Panel.Controls.Add(Name);
        Panel.Controls.Add(Detail);
    }

    public static int Scale(int px, float dpiScale)
    {
        return (int)Math.Round(px * dpiScale);
    }
}

internal sealed class TempForm : Form
{
    readonly Font _smallFont;
    readonly Font _tempFont;
    readonly ChipColumn _cpuCol;
    readonly ChipColumn _gpuCol;
    readonly int _screenMargin;

    readonly CpuInfo _cpuInfo;
    readonly CpuLoadReader _cpuLoad;
    PawnIoSmnReader _pawn;
    AdlGpuReader _adl;
    bool _readersInitialized;

    readonly System.Threading.Timer _timer;
    readonly MethodInvoker _applyUiDelegate;
    readonly object _readerLock = new object();
    readonly object _sync = new object();
    SensorSnapshot _latest;
    bool _hasSnapshot;
    int _polling;
    bool _shown;
    bool _sessionLocked;
    volatile bool _disposed;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        try
        {
            if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0)
                DwmSetWindowAttribute(Handle, 19, ref on, 4);
        }
        catch { }
    }

    public TempForm()
    {
        float dpiScale;
        using (Graphics g = CreateGraphics())
            dpiScale = g.DpiX / 96f;
        if (dpiScale < 1f)
            dpiScale = 1f;

        _smallFont = new Font("Segoe UI", 8.5f);
        _tempFont = new Font("Segoe UI", 28f, FontStyle.Bold);
        _screenMargin = ChipColumn.Scale(12, dpiScale);

        Text = "Temp";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        MaximizeBox = false;
        TopMost = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(ChipColumn.Scale(258, dpiScale), ChipColumn.Scale(82, dpiScale));
        BackColor = Color.FromArgb(20, 20, 22);
        ForeColor = Color.White;
        Font = _smallFont;

        _cpuCol = new ChipColumn(_smallFont, _tempFont, dpiScale);
        _gpuCol = new ChipColumn(_smallFont, _tempFont, dpiScale);

        TableLayoutPanel table = new TableLayoutPanel();
        table.Dock = DockStyle.Fill;
        table.BackColor = BackColor;
        table.ColumnCount = 2;
        table.RowCount = 1;
        table.Padding = new Padding(ChipColumn.Scale(2, dpiScale));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52f));
        table.Controls.Add(_cpuCol.Panel, 0, 0);
        table.Controls.Add(_gpuCol.Panel, 1, 0);
        Controls.Add(table);

        _cpuInfo = CpuInfo.Detect();
        _cpuLoad = new CpuLoadReader();
        _cpuCol.Name.Text = _cpuInfo.DisplayName;

        _applyUiDelegate = ApplyUi;
        _timer = new System.Threading.Timer(OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);

        Resize += OnFormResize;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Rectangle wa = Screen.FromControl(this).WorkingArea;
        Location = new Point(wa.Right - Width - _screenMargin, wa.Bottom - Height - _screenMargin);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _shown = true;
        UpdateTimerState();
    }

    void OnFormResize(object sender, EventArgs e)
    {
        UpdateTimerState();
    }

    void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
            _sessionLocked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
            _sessionLocked = false;
        else
            return;

        UpdateTimerState();
    }

    void UpdateTimerState()
    {
        if (_disposed || !_shown)
            return;

        bool pause = _sessionLocked || WindowState == FormWindowState.Minimized;
        try
        {
            _timer.Change(pause ? Timeout.Infinite : 0, pause ? Timeout.Infinite : 1000);
        }
        catch (ObjectDisposedException) { }
    }

    void OnTimerTick(object state)
    {
        if (_disposed)
            return;
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
            return;

        try
        {
            SensorSnapshot snap = ReadAllSensors();
            if (_disposed)
                return;

            lock (_sync)
            {
                _latest = snap;
                _hasSnapshot = true;
            }

            if (!_disposed && IsHandleCreated && !IsDisposed)
            {
                try { BeginInvoke(_applyUiDelegate); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    SensorSnapshot ReadAllSensors()
    {
        SensorSnapshot snap = new SensorSnapshot();

        lock (_readerLock)
        {
            if (_disposed)
                return snap;

            if (!_readersInitialized)
            {
                if (_cpuInfo.IsSupportedAmd)
                    _pawn = new PawnIoSmnReader();
                _adl = new AdlGpuReader();
                _readersInitialized = true;
            }

            if (!_cpuInfo.IsSupportedAmd)
            {
                snap.CpuError = "unsupported CPU";
            }
            else if (_pawn == null || !_pawn.IsReady)
            {
                snap.CpuError = _pawn != null ? _pawn.Error : "no PawnIO";
            }
            else
            {
                bool tctlOk;
                float tctl;
                bool ccdOk;
                float ccd;
                if (_pawn.ReadTemps(_cpuInfo.CcdSmnAddress, _cpuInfo.TctlOffset, out tctlOk, out tctl, out ccdOk, out ccd))
                {
                    snap.CpuTempOk = tctlOk;
                    snap.CpuTemp = tctl;
                    snap.CcdTempOk = ccdOk;
                    snap.CcdTemp = ccd;
                }
                else
                {
                    snap.CpuError = "SMN read err";
                }
            }

            float cpuLoad;
            if (_cpuLoad.TryRead(out cpuLoad))
            {
                snap.CpuLoadOk = true;
                snap.CpuLoad = cpuLoad;
            }

            if (_adl == null || !_adl.IsReady)
            {
                snap.GpuName = "";
                snap.GpuError = _adl != null && !string.IsNullOrEmpty(_adl.Error) ? _adl.Error : "no AMD GPU";
            }
            else
            {
                snap.GpuName = _adl.GpuName;
                bool coreOk;
                float coreTemp;
                bool hotOk;
                float hotTemp;
                bool memOk;
                float memTemp;
                bool gpuLoadOk;
                float gpuLoad;
                if (_adl.TryRead(out coreOk, out coreTemp, out hotOk, out hotTemp, out memOk, out memTemp, out gpuLoadOk, out gpuLoad))
                {
                    snap.GpuOk = true;
                    snap.GpuCoreTemp = coreOk ? coreTemp : hotTemp;
                    snap.GpuHotOk = hotOk;
                    snap.GpuHotTemp = hotTemp;
                    snap.GpuMemOk = memOk;
                    snap.GpuMemTemp = memTemp;
                    snap.GpuLoadOk = gpuLoadOk;
                    snap.GpuLoad = gpuLoad;
                }
                else
                {
                    snap.GpuError = !string.IsNullOrEmpty(_adl.Error) ? _adl.Error : "no sensors";
                }
            }
        }

        return snap;
    }

    void ApplyUi()
    {
        if (_disposed || IsDisposed)
            return;

        SensorSnapshot snap;
        lock (_sync)
        {
            if (!_hasSnapshot)
                return;
            snap = _latest;
        }

        ApplyCpuUi(ref snap);
        ApplyGpuUi(ref snap);
    }

    void ApplyCpuUi(ref SensorSnapshot snap)
    {
        if (snap.CpuTempOk)
            PaintTemp(_cpuCol.Temp, snap.CpuTemp);
        else
            SetLabel(_cpuCol.Temp, "-- C", Color.Silver);

        string detail = "";
        if (snap.CpuTempOk)
        {
            if (snap.CcdTempOk && snap.CpuLoadOk)
                detail = string.Format("ccd {0:0}  load {1:0}%", snap.CcdTemp, snap.CpuLoad);
            else if (snap.CcdTempOk)
                detail = string.Format("ccd {0:0}", snap.CcdTemp);
            else if (snap.CpuLoadOk)
                detail = string.Format("load {0:0}%", snap.CpuLoad);
        }
        else
        {
            string err = snap.CpuError ?? "";
            if (snap.CpuLoadOk && err.Length > 0)
                detail = string.Format("{0}  load {1:0}%", err, snap.CpuLoad);
            else if (snap.CpuLoadOk)
                detail = string.Format("load {0:0}%", snap.CpuLoad);
            else
                detail = err;
        }

        SetText(_cpuCol.Detail, detail);
    }

    void ApplyGpuUi(ref SensorSnapshot snap)
    {
        SetText(_gpuCol.Name, snap.GpuName ?? "");

        if (!snap.GpuOk)
        {
            SetLabel(_gpuCol.Temp, "-- C", Color.Silver);
            SetText(_gpuCol.Detail, snap.GpuError ?? "not found");
            return;
        }

        PaintTemp(_gpuCol.Temp, snap.GpuCoreTemp);

        string detail = "";
        if (snap.GpuHotOk)
            detail = AppendBit(detail, string.Format("hotspot {0:0}", snap.GpuHotTemp));
        if (snap.GpuMemOk)
            detail = AppendBit(detail, string.Format("mem {0:0}", snap.GpuMemTemp));
        if (snap.GpuLoadOk)
            detail = AppendBit(detail, string.Format("load {0:0}%", snap.GpuLoad));

        SetText(_gpuCol.Detail, detail);
    }

    static string AppendBit(string existing, string bit)
    {
        return existing.Length == 0 ? bit : existing + "  " + bit;
    }

    static void PaintTemp(CompactLabel label, float v)
    {
        Color color;
        if (v >= 89f) color = Color.FromArgb(255, 72, 72);
        else if (v >= 80f) color = Color.FromArgb(255, 160, 60);
        else if (v >= 70f) color = Color.FromArgb(240, 210, 70);
        else color = Color.FromArgb(90, 210, 120);

        SetLabel(label, string.Format("{0:0} C", v), color);
    }

    static void SetText(CompactLabel label, string text)
    {
        if (!string.Equals(label.Text, text, StringComparison.Ordinal))
            label.Text = text;
    }

    static void SetLabel(CompactLabel label, string text, Color color)
    {
        if (!string.Equals(label.Text, text, StringComparison.Ordinal))
            label.Text = text;
        if (label.ForeColor.ToArgb() != color.ToArgb())
            label.ForeColor = color;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try { SystemEvents.SessionSwitch -= OnSessionSwitch; }
            catch { }

            try { _timer.Dispose(); }
            catch { }

            lock (_readerLock)
            {
                if (_pawn != null)
                {
                    _pawn.Dispose();
                    _pawn = null;
                }
                if (_adl != null)
                {
                    _adl.Dispose();
                    _adl = null;
                }
            }

            _smallFont.Dispose();
            _tempFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
