@echo off
title SpotifyIsland — Publisher
echo.
echo  ==========================================
echo   SpotifyIsland — Build ^& Publish
echo  ==========================================
echo.

where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo [ERROR] .NET SDK not found. Please install it from:
    echo         https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

echo [1/2] Building self-contained EXE...
dotnet publish SpotifyIsland.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=none ^
    -p:DebugSymbols=false ^
    -o ./dist

if %errorlevel% neq 0 (
    echo.
    echo [ERROR] Build failed. See output above.
    pause
    exit /b 1
)

echo.
echo [2/2] Done!
echo.
echo  Output: dist\SpotifyIsland.exe
echo.
echo  You can now:
echo   - Run dist\SpotifyIsland.exe directly
echo   - Copy it anywhere and run it — no installer needed
echo   - Upload dist\SpotifyIsland.exe to GitHub Releases
echo.
pause
