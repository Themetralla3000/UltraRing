@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Stop.ps1" %*
if errorlevel 1 (
    echo Stop failed. See the message above.
    pause
)
