@echo off
setlocal EnableExtensions

set "ROOT=%~dp0"
set "PROJECT_DIR=%ROOT%CombatJobSettings"
set "BUILD_DIR=%ROOT%_build"
set "RELEASE_DIR=%ROOT%release\CombatJobSettings"

echo ================================================
echo V0.1.0 Combat Job Settings - Build
echo ================================================
echo Build  : %BUILD_DIR%
echo Release: %RELEASE_DIR%
echo.

if exist "%BUILD_DIR%" rmdir /s /q "%BUILD_DIR%"
if exist "%RELEASE_DIR%" rmdir /s /q "%RELEASE_DIR%"
mkdir "%BUILD_DIR%" >nul 2>&1
mkdir "%RELEASE_DIR%" >nul 2>&1

pushd "%PROJECT_DIR%"
dotnet build CombatJobSettings.csproj -o "%BUILD_DIR%"
set "BUILD_RESULT=%ERRORLEVEL%"
popd

if not "%BUILD_RESULT%"=="0" (
  echo.
  echo [ERROR] Build failed.
  pause
  exit /b %BUILD_RESULT%
)

echo.
echo [PACKAGE] Copying plugin files...
copy /y "%BUILD_DIR%\CombatJobSettings.dll" "%RELEASE_DIR%\CombatJobSettings.dll" >nul
if errorlevel 1 goto :package_error

if exist "%BUILD_DIR%\CombatJobSettings.deps.json" (
  copy /y "%BUILD_DIR%\CombatJobSettings.deps.json" "%RELEASE_DIR%\CombatJobSettings.deps.json" >nul
  if errorlevel 1 goto :package_error
)

if exist "%BUILD_DIR%\CombatJobSettings.json" (
  copy /y "%BUILD_DIR%\CombatJobSettings.json" "%RELEASE_DIR%\CombatJobSettings.json" >nul
  if errorlevel 1 goto :package_error
)

echo.
echo [OK] Build complete.
echo Package: %RELEASE_DIR%
echo.
echo Copy the CombatJobSettings folder under release to your Dalamud local plugin folder.
echo.
pause
exit /b 0

:package_error
echo.
echo [ERROR] Build succeeded, but release packaging failed.
pause
exit /b 1
