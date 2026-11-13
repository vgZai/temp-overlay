@echo off
setlocal
cd /d "%~dp0"

net session >nul 2>&1
if %errorLevel% neq 0 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO" >nul 2>&1
if errorlevel 1 (
  reg query "HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO" >nul 2>&1
)
if errorlevel 1 (
  echo Installing PawnIO sensor driver...
  "%~dp0PawnIO_setup.exe" -install -silent
  if errorlevel 1 (
    echo PawnIO install failed.
    pause
    exit /b 1
  )
)

if exist "%~dp0CPUTemp.new.exe" (
  del "%~dp0CPUTemp.exe" >nul 2>&1
  if not exist "%~dp0CPUTemp.exe" (
    ren "%~dp0CPUTemp.new.exe" CPUTemp.exe
  ) else (
    start "" "%~dp0CPUTemp.new.exe"
    exit /b
  )
)

if not exist "%~dp0CPUTemp.exe" (
  echo Building CPUTemp.exe...
  "%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /t:winexe /platform:x64 /out:"%~dp0CPUTemp.exe" /win32manifest:"%~dp0app.manifest" /r:"%~dp0LibreHardwareMonitorLib.dll" /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0CPUTemp.cs"
  if errorlevel 1 (
    echo Compile failed.
    pause
    exit /b 1
  )
)

start "" "%~dp0CPUTemp.exe"
