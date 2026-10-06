@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul || (
  echo [ERROR] .NET 8 SDK is not installed.
  pause
  exit /b 1
)

dotnet run --project "src\WBDropp\WBDropp.csproj" --configuration Debug
if errorlevel 1 pause
