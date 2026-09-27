@echo off
setlocal
if not exist "%~dp0ProcessKeeper.exe" (
    echo ProcessKeeper.exe was not found.
    echo Build from source using build.ps1 and package-single-file.ps1 in PowerShell 7.
    pause
    exit /b 1
)
start "" /D "%~dp0" "%~dp0ProcessKeeper.exe"
exit /b %errorlevel%
