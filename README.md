# Temp

A tiny always-on-top Windows overlay that shows live **CPU and GPU temperatures** side by side, updated once a second.

Built for a Ryzen 7 5700X3D + AMD GPU desktop, but it works with any CPU and AMD / NVIDIA / Intel GPU that [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) supports.

- Sits in the bottom-right corner, dark title bar, no tray icon or settings
- Big color-coded temperature per chip: green under 70 °C, yellow 70, orange 80, red 89+
- Small detail line: CPU Tctl/package and load, GPU hotspot, VRAM temp, and load

## Run it

1. Download the zip from [Releases](../../releases), extract it anywhere.
2. Double-click `CPUTemp.bat`. Windows asks for admin once.
3. Close the overlay with the X.

Admin is required because Ryzen package temperature isn't readable from a normal user process. On first run the batch file silently installs **PawnIO**, the small signed sensor driver that LibreHardwareMonitor and Fan Control also use.

## Build from source

The repo holds only the source. To build it yourself, put these next to `CPUTemp.cs`:

- `LibreHardwareMonitorLib.dll` and its dependency DLLs from a [LibreHardwareMonitor release](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases)
- `PawnIO_setup.exe` from [PawnIO](https://pawnio.eu/)

Then run `CPUTemp.bat`. If `CPUTemp.exe` doesn't exist it compiles one with the .NET Framework 4 compiler that ships with Windows (`csc.exe`), so no Visual Studio needed.

## Files

| File | What it does |
| --- | --- |
| `CPUTemp.cs` | The whole app (WinForms) |
| `CPUTemp.bat` | Elevates, installs PawnIO if missing, builds if needed, launches |
| `app.manifest` | Requests admin, DPI-aware |
| `CPUTemp.exe.config` | .NET runtime binding redirects |

## License

MIT. LibreHardwareMonitor is MPL-2.0; PawnIO has its own license.