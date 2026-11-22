# Temp

A compact always-on-top Windows overlay that shows live **CPU and GPU temperatures** side by side, updated once a second.

Built for an AMD Ryzen (Family 17h/19h) + AMD Radeon desktop using direct hardware APIs with zero external DLL dependencies:

- **CPU temperature:** direct `PawnIO` SMN reads via embedded `AMDFamily17.bin` (no polling of per-core MSRs or unused SMU sensors)
- **CPU load:** Win32 `GetSystemTimes`
- **GPU edge, hotspot, VRAM temp & load:** direct AMD ADL2 (`atiadlxx.dll` `ADL2_New_QueryPMLogData_Get`)
- Sits compactly in the bottom-right corner, dark title bar, pauses automatically when minimized or workstation-locked
- Big color-coded temperature per chip: green under 70 °C, yellow 70, orange 80, red 89+
- Small detail line: CPU CCD1 temp and load, GPU hotspot, VRAM temp, and load

## Run it

1. Download the zip from [Releases](../../releases), extract it anywhere.
2. Double-click `CPUTemp.bat` (or launch via an elevated shortcut / Task Scheduler task). Windows asks for admin once, and silently installs **PawnIO** on first run if missing.
3. Close the overlay with the X.

Admin is required because Ryzen CPU SMN temperature registers aren't readable from a normal user process.

## Build from source

`CPUTemp.exe` compiles directly from `CPUTemp.cs` + `AMDFamily17.bin` (included in the repo) using the .NET Framework 4 compiler that ships with Windows (`csc.exe`), so no Visual Studio or third-party DLLs are needed. Run `CPUTemp.bat` to build and launch automatically (place `PawnIO_setup.exe` from [PawnIO](https://pawnio.eu/) alongside it if deploying to a machine without PawnIO installed).

## Files

| File | What it does |
| --- | --- |
| `CPUTemp.cs` | The whole app (WinForms + direct PawnIO / ADL2 / `GetSystemTimes` interop) |
| `AMDFamily17.bin` | Signed PawnIO module embedded into `CPUTemp.exe` at compile time |
| `CPUTemp.bat` | Elevates, installs PawnIO if missing, builds if needed, launches |
| `app.manifest` | Requests admin, DPI-aware |
| `CPUTemp.exe.config` | .NET Framework 4.7.2 runtime config |

## License

MIT. PawnIO and the signed `AMDFamily17.bin` module have their own respective licenses.
