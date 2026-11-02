@echo off
setlocal
cd /d "%~dp0"
powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File "%~dp0Prepare-CustomModelUpdate.ps1" %*
if errorlevel 1 (
  echo.
  echo Preparation failed. Read the error above and run again.
) else (
  echo.
  echo Copy the generated custom ZIP from the output folder to USB.
)
pause
