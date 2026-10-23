@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo ========================================================
echo   [+] AbyssCore C# .NET 8 High-Throughput Engine
echo ========================================================
echo.
dotnet run --project AbyssCore.csproj
if %errorlevel% neq 0 (
    echo.
    echo [!] Unable to start C# core directly.
    echo [!] Please make sure .NET 8 SDK is installed.
    echo [!] Download: https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
)
