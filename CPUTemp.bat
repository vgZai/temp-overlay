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
  if exist "%~dp0PawnIO_setup.exe" (
    echo Installing PawnIO sensor driver...
    "%~dp0PawnIO_setup.exe" -install -silent
    if errorlevel 1 (
      echo PawnIO install failed.
      pause
      exit /b 1
    )
  ) else (
    echo PawnIO driver not installed and PawnIO_setup.exe is missing.
    pause
    exit /b 1
  )
)

if exist "%~dp0CPUTemp.old.exe" (
  del "%~dp0CPUTemp.old.exe" >nul 2>&1
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

set "NEED_BUILD=0"
if not exist "%~dp0CPUTemp.exe" (
  set "NEED_BUILD=1"
) else (
  powershell -NoProfile -Command "if ((Get-Item -LiteralPath '%~dp0CPUTemp.cs').LastWriteTimeUtc -gt (Get-Item -LiteralPath '%~dp0CPUTemp.exe').LastWriteTimeUtc) { exit 1 }"
  if errorlevel 1 set "NEED_BUILD=1"
)

if "%NEED_BUILD%"=="1" (
  if exist "%~dp0CPUTemp.exe" (
    del "%~dp0CPUTemp.exe" >nul 2>&1
    if exist "%~dp0CPUTemp.exe" (
      move /y "%~dp0CPUTemp.exe" "%~dp0CPUTemp.old.exe" >nul 2>&1
    )
  )
  echo Building CPUTemp.exe...
  "%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /warn:4 /t:winexe /platform:x64 /out:"%~dp0CPUTemp.exe" /win32manifest:"%~dp0app.manifest" /resource:"%~dp0AMDFamily17.bin",AMDFamily17.bin /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0CPUTemp.cs"
  if errorlevel 1 (
    echo Compile failed.
    pause
    exit /b 1
  )
)

start "" "%~dp0CPUTemp.exe"
