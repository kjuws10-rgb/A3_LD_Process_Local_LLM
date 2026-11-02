@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Prepare-OfflineBundle.ps1" -ModelRuntimeOnly %*
if errorlevel 1 (
  echo.
  echo Preparation failed. Running again resumes downloads.
) else (
  echo.
  echo Complete. Copy the generated ZIP from the output folder to USB.
)
pause
