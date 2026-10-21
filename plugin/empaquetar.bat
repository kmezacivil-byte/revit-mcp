@echo off
setlocal
:: Uso: "Compilar y Empaquetar.bat" 2021   (2021 a 2025; por defecto 2021)
set "YEAR=%~1"
if "%YEAR%"=="" set "YEAR=2021"
set "ROOT=%~dp0.."

where dotnet >nul 2>&1 || (echo [ERROR] dotnet no encontrado & exit /b 1)

echo [INFO] Compilando puente...
dotnet build "%ROOT%\bridge\RevitMcpBridge.csproj" -c Release -o "%ROOT%\bridge\build-output" --nologo || exit /b 1

echo [INFO] Compilando plugin para Revit %YEAR%...
dotnet build "%~dp0RevitMcpPlugin.csproj" -c Release /p:RevitVersion=%YEAR% --nologo || exit /b 1

set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=C:\Program Files\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (echo [ERROR] Inno Setup 6 no encontrado & exit /b 1)

"%ISCC%" "/DMyRevitYear=%YEAR%" "%~dp0RevitMcpPlugin.iss" || exit /b 1
echo [OK] Instalador en %ROOT%\installer\RevitMcpPlugin_%YEAR%_Setup.exe
endlocal
