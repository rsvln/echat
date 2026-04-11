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
:: Clean: bin + obj for all projects + version lock
:: -------------------------------------------
echo Cleaning bin and obj...
for %%P in (EChat.Core EChat.UI EChat.Web EChat.MAUI) do (
    if exist "src\%%P\bin" rd /s /q "src\%%P\bin"
    if exist "src\%%P\obj" rd /s /q "src\%%P\obj"
    if exist "src\%%P\version.lock" del /q "src\%%P\version.lock"
)
echo   OK: clean done
echo.

echo Cleaning android, distr, win...
if exist "pub\android" rd /s /q "pub\android"
if exist "pub\distr" rd /s /q "pub\distr"
if exist "pub\win" rd /s /q "pub\win"
echo   OK: clean done
echo.

:: Ensure output folders exist
if not exist "%PUB%\win"     mkdir "%PUB%\win"
if not exist "%PUB%\android" mkdir "%PUB%\android"
if not exist "%PUB%\distr"   mkdir "%PUB%\distr"

echo.
echo ===========================================
echo   EChat - Full Publish
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
        echo   OK: renamed EChat.Maui.exe -^> echat.exe
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

:: -------------------------------------------
:: 2. Windows Installer (Inno Setup)
:: -------------------------------------------
echo.
echo [2/5] Windows Installer...
where iscc >nul 2>&1
if not errorlevel 1 goto :iscc_found
if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" goto :iscc_pf86
echo   SKIPPED: Inno Setup not found (install from https://jrsoftware.org/isinfo.php)
goto :android

:iscc_pf86
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
goto :iscc_run

:iscc_found
set "ISCC=iscc"

:iscc_run
"%ISCC%" "%~dp0installer\echat.iss"
if errorlevel 1 (
    echo   FAILED: Inno Setup
    set /a ERRORS+=1
) else (
    echo   OK: pub\distr\EChat-Setup-0.1.0.exe
)

:: -------------------------------------------
:android
:: 3. Android APK
:: -------------------------------------------
echo.
echo [3/5] Android APK...
dotnet build %MAUI_PROJ% -f net10.0-android -c Release -t:SignAndroidPackage -p:AndroidPackageFormat=apk
if errorlevel 1 (
    echo   FAILED: Android APK
    set /a ERRORS+=1
) else (
    copy /y "src\EChat.MAUI\bin\Release\net10.0-android\com.echat.app-Signed.apk" "%PUB%\android\" >nul
    copy /y "src\EChat.MAUI\bin\Release\net10.0-android\com.echat.app-Signed.apk" "%PUB%\distr\EChat.apk" >nul
    echo   OK: pub\android\com.echat.app-Signed.apk
    echo   OK: pub\distr\EChat.apk
)

:: -------------------------------------------
:: 4. iOS - requires Mac build host
:: -------------------------------------------
echo.
echo [4/5] iOS - SKIPPED (requires Mac build host)

:: -------------------------------------------
:: 5. Docker Web
:: -------------------------------------------
echo.
echo [5/5] Docker Web...

:: Ensure Docker Desktop is running
docker info >nul 2>&1
if errorlevel 1 (
    echo   Docker Desktop is not running. Starting...
    start "" "C:\Program Files\Docker\Docker\Docker Desktop.exe"
    echo   Waiting for Docker to start...
    :wait_docker
    timeout /t 3 /nobreak >nul
    docker info >nul 2>&1
    if errorlevel 1 goto :wait_docker
    echo   Docker Desktop is ready.
)

:: Build image
docker build -f src/EChat.Web/Dockerfile -t %IMAGE_NAME%:latest .
if errorlevel 1 (
    echo   FAILED: Docker build
    set /a ERRORS+=1
    goto :compose
)

:: Tag and push to local registry
docker tag %IMAGE_NAME%:latest %REGISTRY_LOCAL%/%IMAGE_NAME%:latest
docker push %REGISTRY_LOCAL%/%IMAGE_NAME%:latest
if errorlevel 1 (
    echo   FAILED: push to %REGISTRY_LOCAL%
    set /a ERRORS+=1
) else (
    echo   OK: %REGISTRY_LOCAL%/%IMAGE_NAME%:latest
)

:: Tag and push to Docker Hub
docker tag %IMAGE_NAME%:latest %REGISTRY_HUB%/%IMAGE_NAME%:latest
docker push %REGISTRY_HUB%/%IMAGE_NAME%:latest
if errorlevel 1 (
    echo   FAILED: push to Docker Hub ^(%REGISTRY_HUB%/%IMAGE_NAME%^)
    set /a ERRORS+=1
) else (
    echo   OK: %REGISTRY_HUB%/%IMAGE_NAME%:latest
)

:: -------------------------------------------
:: Write docker-compose to distr
:: -------------------------------------------
:compose
echo.
echo Writing docker-compose.yml to distr...
(
    echo version: '3.8'
    echo services:
    echo.
    echo   echat-web:
    echo     restart: unless-stopped
    echo     image: %REGISTRY_HUB%/%IMAGE_NAME%:latest
    echo     container_name: echat
    echo     privileged: true
    echo     volumes:
    echo       - /srv/md0/echat/db:/app/data/
    echo     ports:
    echo       - 9999:8080
) > "%PUB%\distr\docker-compose.yml"
copy /y "%PUB%\distr\docker-compose.yml" "%~dp0docker-compose.yml" >nul
echo   OK: pub\distr\docker-compose.yml

:: -------------------------------------------
:: Copy distr to YandexDisk share folder
:: -------------------------------------------
echo.
echo Copying distr to e:\YandexDisk\share\echat\...
if not exist "e:\YandexDisk\share\echat\" mkdir "e:\YandexDisk\share\echat\"
xcopy /y /q "%PUB%\distr\*" "e:\YandexDisk\share\echat\" >nul
if errorlevel 1 (
    echo   FAILED: copy to share
    set /a ERRORS+=1
) else (
    echo   OK: e:\YandexDisk\share\echat\
)

:: -------------------------------------------
:summary
echo.
echo ===========================================
if %ERRORS%==0 (
    echo   All done. No errors.
) else (
    echo   Done with %ERRORS% errors. See output above.
)
echo ===========================================
echo.
