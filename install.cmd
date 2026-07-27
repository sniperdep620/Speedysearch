@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
set "SPEEDYSEARCH_EXIT_CODE=%ERRORLEVEL%"
if not "%SPEEDYSEARCH_EXIT_CODE%"=="0" (
  echo.
  echo Speedysearch installation failed. See the error above.
  pause
)
exit /b %SPEEDYSEARCH_EXIT_CODE%
