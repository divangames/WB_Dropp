@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

set "VERSION=0.0.1"
set "TAG=v%VERSION%"
set "REMOTE=https://github.com/divangames/WB_Dropp.git"
set "MESSAGE=%~1"
if "%MESSAGE%"=="" set "MESSAGE=Release %TAG%"

where git >nul 2>nul || (
  echo [ERROR] Git is not installed.
  exit /b 1
)
where gh >nul 2>nul || (
  echo [ERROR] GitHub CLI is not installed.
  exit /b 1
)

gh auth status >nul 2>nul || (
  echo [ERROR] Sign in first: gh auth login
  exit /b 1
)

call publish.bat || exit /b 1

if not exist ".git" git init -b main . || exit /b 1

git remote get-url origin >nul 2>nul
if errorlevel 1 (
  git remote add origin "%REMOTE%" || exit /b 1
) else (
  for /f "delims=" %%R in ('git remote get-url origin') do set "CURRENT_REMOTE=%%R"
  if /i not "!CURRENT_REMOTE!"=="%REMOTE%" (
    echo [ERROR] origin points to !CURRENT_REMOTE!
    echo Expected: %REMOTE%
    exit /b 1
  )
)

git add -A || exit /b 1
git diff --cached --quiet
if errorlevel 1 git commit -m "%MESSAGE%" || exit /b 1

git branch -M main || exit /b 1
git push -u origin main || exit /b 1

git rev-parse "%TAG%" >nul 2>nul
if errorlevel 1 git tag -a "%TAG%" -m "WB Dropp %VERSION%" || exit /b 1
git push origin "%TAG%" || exit /b 1

gh release view "%TAG%" --repo divangames/WB_Dropp >nul 2>nul
if errorlevel 1 (
  gh release create "%TAG%" "dist\WBDropp-v%VERSION%-win-x64.zip" --repo divangames/WB_Dropp --title "WB Dropp %VERSION%" --notes-file CHANGELOG.md || exit /b 1
) else (
  gh release upload "%TAG%" "dist\WBDropp-v%VERSION%-win-x64.zip" --repo divangames/WB_Dropp --clobber || exit /b 1
)

echo Release %TAG% published successfully.
exit /b 0
