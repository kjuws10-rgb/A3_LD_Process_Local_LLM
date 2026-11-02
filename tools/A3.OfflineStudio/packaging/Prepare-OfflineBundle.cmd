@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Prepare-OfflineBundle.ps1" %*
if errorlevel 1 (
  echo.
  echo Preparation failed. Read the error above. Running again resumes downloads.
) else (
  echo.
  echo Complete. Copy the generated ZIP from the output folder to USB.
)
pause
