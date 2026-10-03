@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo === DriftHub: споты на гит ===
echo.
powershell -ExecutionPolicy Bypass -NoProfile -File "tools\Publish-Release.ps1" -SpotsOnly
if errorlevel 1 (
  echo.
  echo ОШИБКА генерации фида.
  pause
  exit /b 1
)
echo.
git add spots feed
git diff --cached --quiet
if not errorlevel 1 (
  echo Нечего пушить: spots и feed без изменений.
  pause
  exit /b 0
)
git status --short
echo.
set /p MSG="Сообщение коммита [spots update]: "
if "%MSG%"=="" set MSG=spots update
git commit -m "%MSG%"
if errorlevel 1 (
  echo ОШИБКА коммита.
  pause
  exit /b 1
)
git push origin main
if errorlevel 1 (
  echo ОШИБКА пуша.
  pause
  exit /b 1
)
echo.
echo ГОТОВО: споты улетят пользователям в течение ~30 минут.
pause
