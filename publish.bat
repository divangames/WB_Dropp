@echo off
setlocal
cd /d "%~dp0"

call build.bat || exit /b 1

set "PUBLISH_DIR=%~dp0dist\win-x64-v0.1.1"
set "ARCHIVE=%~dp0dist\WBDropp-v0.1.1-win-x64.zip"

if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%ARCHIVE%" del /q "%ARCHIVE%"

echo Publishing self-contained win-x64...
dotnet publish "src\WBDropp\WBDropp.csproj" --configuration Release --runtime win-x64 --self-contained true --no-restore --output "%PUBLISH_DIR%" || exit /b 1

del /q "%PUBLISH_DIR%\*.pdb" 2>nul
copy /y "README.md" "%PUBLISH_DIR%\README.md" >nul
copy /y "LICENSE" "%PUBLISH_DIR%\LICENSE" >nul

powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%PUBLISH_DIR%\*' -DestinationPath '%ARCHIVE%' -CompressionLevel Optimal -Force" || exit /b 1

echo Ready: %ARCHIVE%
exit /b 0
