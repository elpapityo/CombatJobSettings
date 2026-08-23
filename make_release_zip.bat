@echo off
setlocal
cd /d "%~dp0"

set "SOURCE=source\release\CombatJobSettings"
set "OUT=CombatJobSettings_v0.1.0.zip"

if not exist "%SOURCE%\CombatJobSettings.dll" (
    echo.
    echo [ERROR] %SOURCE%\CombatJobSettings.dll がありません。
    echo 先に source\build.bat を実行してください。
    echo.
    pause
    exit /b 1
)

if exist "%OUT%" del /q "%OUT%"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Compress-Archive -Path '%SOURCE%\*' -DestinationPath '%OUT%' -Force"

if errorlevel 1 (
    echo.
    echo [ERROR] ZIP作成に失敗しました。
    pause
    exit /b 1
)

echo.
echo [OK] %OUT% を作成しました。
echo GitHub Release v0.1.0 にこのZIPを添付してください。
echo.
pause
