@echo off
chcp 65001 >nul
title Сборка ArgonSpriteForge.exe (.NET 8 Single-File)
echo ====================================================================
echo   Сборка одиночного ArgonSpriteForge.exe (win-x64, Self-Contained)
echo ====================================================================
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ОШИБКА] .NET 8 SDK не найден. Скачайте с https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

dotnet publish ArgonSpriteForge.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none ^
  -p:DebugSymbols=false ^
  -o publish\win-x64

if errorlevel 1 (
    echo [ОШИБКА] Сборка завершилась с ошибкой.
    pause
    exit /b 1
)

echo.
echo [ГОТОВО] Файл собран: publish\win-x64\ArgonSpriteForge.exe
explorer publish\win-x64
pause
