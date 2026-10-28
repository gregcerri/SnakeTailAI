@echo off
rem Builds dist\SnakeTailAI (a self-contained exe that needs no .NET on the target PC) and dist\SnakeTailAI-win-x64.zip.
setlocal
cd /d "%~dp0"

dotnet test tests\SnakeTailAI.Tests || goto :error

if exist dist\SnakeTailAI\SnakeTailAI.exe del /q dist\SnakeTailAI\SnakeTailAI.exe
dotnet publish src\SnakeTailAI -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist\SnakeTailAI || goto :error
rem The terminal control package ships debug symbols that are not needed to run.
del /q dist\SnakeTailAI\*.pdb 2>nul
copy /y LEGGIMI.txt dist\SnakeTailAI\ >nul

if exist dist\SnakeTailAI-win-x64.zip del /q dist\SnakeTailAI-win-x64.zip
powershell -NoProfile -Command "Compress-Archive -Path 'dist\SnakeTailAI' -DestinationPath 'dist\SnakeTailAI-win-x64.zip'" || goto :error

echo.
echo Done: dist\SnakeTailAI  and  dist\SnakeTailAI-win-x64.zip
exit /b 0

:error
echo.
echo Build failed.
exit /b 1
