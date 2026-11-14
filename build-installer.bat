@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo === Island: сборка программы ===
if not exist ffmpeg.exe echo [!] ffmpeg.exe рядом с Island.csproj не найден: запись экрана в установщик не попадёт.

if exist publish rmdir /s /q publish
dotnet publish Island.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o publish
if errorlevel 1 goto fail

set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
if not "%ISCC%"=="" goto found
for %%R in ("HKLM\SOFTWARE\WOW6432Node" "HKLM\SOFTWARE" "HKCU\SOFTWARE") do for /f "tokens=2,*" %%A in ('reg query "%%~R\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1" /v InstallLocation 2^>nul') do if exist "%%B\ISCC.exe" set "ISCC=%%B\ISCC.exe"
if not "%ISCC%"=="" goto found
for /f "delims=" %%P in ('where ISCC.exe 2^>nul') do set "ISCC=%%P"
if "%ISCC%"=="" goto noinno
:found

echo === Island: сборка установщика ===
"%ISCC%" installer\Island.iss
if errorlevel 1 goto fail

echo.
echo Готово: installer-output\Island-Setup-....exe
explorer installer-output
goto end

:noinno
echo.
echo Inno Setup 6 не найден. Установи его (бесплатно): https://jrsoftware.org/isdl.php
echo и запусти этот файл ещё раз. Папка publish уже собрана.
echo Или открой installer\Island.iss в Inno Setup и нажми Build - Compile.
goto end

:fail
echo.
echo Сборка не удалась, смотри сообщения выше.

:end
pause
