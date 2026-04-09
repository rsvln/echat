@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul

set REGISTRY_LOCAL=192.168.11.44:5000
set REGISTRY_HUB=rsvln
set IMAGE_NAME=echatweb
set MAUI_PROJ=src\EChat.MAUI\EChat.Maui.csproj
set PUB=%~dp0pub

set ERRORS=0

:: -------------------------------------------
:: Clean: bin + obj for all projects
:: -------------------------------------------
echo Cleaning bin and obj...
for %%P in (EChat.Core EChat.UI EChat.Web EChat.MAUI) do (
    if exist "src\%%P\bin" rd /s /q "src\%%P\bin"
    if exist "src\%%P\obj" rd /s /q "src\%%P\obj"
)
echo   OK: clean done
echo.

echo Cleaning win...
if exist "pub\win" rd /s /q "pub\win"
echo   OK: clean done
echo.

:: Ensure output folders exist
if not exist "%PUB%\win"     mkdir "%PUB%\win"

echo.
echo ===========================================
echo   EChat - Win Publish
echo ===========================================
echo.

:: -------------------------------------------
:: 1. Windows Desktop
:: -------------------------------------------
echo [1/5] Windows Desktop...
dotnet publish %MAUI_PROJ% -f net10.0-windows10.0.19041.0 -c Release -p:WindowsPackageType=None -p:SelfContained=false -o "%PUB%\win"
if errorlevel 1 (
    echo   FAILED: Windows Desktop
    set /a ERRORS+=1
) else (
    echo   OK: pub\win\
    :: Rename exe to echat.exe (assembly name stays EChat.Maui to avoid MAUI resource issues)
    if exist "%PUB%\win\EChat.Maui.exe" (
        if exist "%PUB%\win\echat.exe" del /q "%PUB%\win\echat.exe"
        rename "%PUB%\win\EChat.Maui.exe" echat.exe
        echo   OK: renamed EChat.Maui.exe -> echat.exe
    )
    :: Pack pub\win into distr\EChat-win.zip with inner folder named "echat"
    if exist "%PUB%\distr\EChat-win.zip" del /q "%PUB%\distr\EChat-win.zip"
    powershell -NoProfile -Command ^
        "$src = '%PUB%\win'; $dst = '%PUB%\distr\EChat-win.zip';" ^
        "Add-Type -Assembly System.IO.Compression.FileSystem;" ^
        "$zip = [System.IO.Compression.ZipFile]::Open($dst, 'Create');" ^
        "Get-ChildItem $src -Recurse -File | ForEach-Object {" ^
        "  $rel = $_.FullName.Substring($src.Length).TrimStart('\');" ^
        "  [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, \"echat\$rel\") | Out-Null" ^
        "}; $zip.Dispose()"
    echo   OK: pub\distr\EChat-win.zip
)
