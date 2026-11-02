@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Prepare-OfflineToolchain.ps1" %*
if errorlevel 1 (
  echo.
  echo Preparation failed. Read the error above. Run again to reuse verified downloads.
) else (
  echo.
  echo Copy the output\toolchain folder next to A3.OfflineStudio.exe on the offline PC.
)
pause
