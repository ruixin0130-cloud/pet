@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist "bin\Tamago.exe" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
  if errorlevel 1 (
    echo 构建失败，请查看上方提示。
    pause
    exit /b 1
  )
)
start "" "%~dp0bin\Tamago.exe"
