@echo off
setlocal
echo Building SendProxy...
if exist .build rmdir /s /q .build
dotnet build src\YappersHQ.SendProxy\YappersHQ.SendProxy.csproj -c Release
if errorlevel 1 ( echo Build failed! & exit /b 1 )
if exist .asset xcopy /s /e /y .asset\* .build\ >nul 2>nul
echo Build complete -^> .build\
