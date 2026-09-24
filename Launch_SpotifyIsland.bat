@echo off
title SpotifyIsland
set "EXE=%~dp0dist\SpotifyIsland.exe"

if exist "%EXE%" (
    start "" "%EXE%"
) else (
    echo [SpotifyIsland] EXE not found. Building first...
    call "%~dp0build.bat"
    if exist "%EXE%" start "" "%EXE%"
)
