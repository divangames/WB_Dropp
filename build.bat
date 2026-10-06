@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"

where dotnet >nul 2>nul || (
  echo [ERROR] .NET 8 SDK is not installed.
  pause
  exit /b 1
)

echo [1/4] Restoring...
dotnet restore "WBDropp.sln" || goto :failed

echo [2/4] Building Release...
dotnet build "WBDropp.sln" --configuration Release --no-restore || goto :failed

if exist "Tets" (
  echo [3/4] Checking photo routing...
  set "CLOSEUP_DIR="
  if exist "promts" for /d %%D in ("promts\*") do if not defined CLOSEUP_DIR set "CLOSEUP_DIR=%%~fD"
  if defined CLOSEUP_DIR (
    dotnet run --project "tests\WBDropp.SmokeTests\WBDropp.SmokeTests.csproj" --configuration Release --no-build -- "Tets" "!CLOSEUP_DIR!" || goto :failed
  ) else (
    dotnet run --project "tests\WBDropp.SmokeTests\WBDropp.SmokeTests.csproj" --configuration Release --no-build -- "Tets" || goto :failed
  )
) else (
  echo [3/4] Sample set is not present - routing smoke test skipped.
)

echo [4/4] Build completed.
exit /b 0

:failed
echo.
echo [ERROR] Build failed.
exit /b 1
