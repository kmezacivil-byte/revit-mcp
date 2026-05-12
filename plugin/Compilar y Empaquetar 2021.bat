@echo off
setlocal enabledelayedexpansion
title Compilar RevitMcpPlugin para Revit 2021

echo.
echo  ============================================================
echo    RevitMCP ^| Compilar + Empaquetar para Revit 2021
echo    Digital Jump Peru
echo  ============================================================
echo.

:: Carpeta donde está este .bat (plugin\)
set "PLUGIN_DIR=%~dp0"
set "DLL_OUT=%PLUGIN_DIR%build-output-2021\RevitMcpPlugin.dll"
set "ISS=%PLUGIN_DIR%RevitMcpPlugin_2021.iss"

:: ── 1) Verificar .NET SDK ──────────────────────────────────────
where dotnet >nul 2>&1
if !ERRORLEVEL! NEQ 0 (
    echo  [ERROR] dotnet no encontrado. Instala el SDK de .NET desde:
    echo          https://dotnet.microsoft.com/download
    goto :error
)
for /f "tokens=*" %%v in ('dotnet --version') do echo  [INFO] .NET SDK %%v detectado.

:: ── 2) Verificar referencias de Revit 2021 ────────────────────
set "REVIT_API=C:\Program Files\Autodesk\Revit 2021\RevitAPI.dll"
if not exist "%REVIT_API%" (
    echo.
    echo  [ADVERTENCIA] No se encontro RevitAPI.dll en:
    echo                %REVIT_API%
    echo.
    echo  El build fallara si Revit 2021 no esta instalado en esa ruta.
    echo  Presiona cualquier tecla para intentarlo de todas formas...
    pause >nul
)

:: ── 3) Compilar el plugin (net48 / Revit 2021) ─────────────────
echo.
echo  [INFO] Compilando plugin para Revit 2021 (net48)...
echo.
cd /d "%PLUGIN_DIR%"
dotnet build RevitMcpPlugin.csproj -c Release /p:RevitVersion=2021 --nologo

if !ERRORLEVEL! NEQ 0 (
    echo.
    echo  [ERROR] La compilacion fallo. Revisa los errores arriba.
    goto :error
)

if not exist "%DLL_OUT%" (
    echo.
    echo  [ERROR] El DLL no fue generado en: %DLL_OUT%
    goto :error
)
echo.
echo  [OK] DLL generado: %DLL_OUT%

:: ── 4) Buscar Inno Setup compiler ─────────────────────────────
set "ISCC="
for %%p in (
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    "C:\Program Files\Inno Setup 6\ISCC.exe"
    "C:\Program Files (x86)\Inno Setup 5\ISCC.exe"
) do (
    if exist %%p (
        set "ISCC=%%~p"
        goto :found_iscc
    )
)

echo.
echo  [ADVERTENCIA] Inno Setup no encontrado automaticamente.
echo  El DLL ya fue compilado. Abre Inno Setup manualmente y compila:
echo    %ISS%
echo.
goto :done

:found_iscc
echo  [INFO] Inno Setup encontrado: %ISCC%

:: ── 5) Crear carpeta del instalador si no existe ──────────────
if not exist "%PLUGIN_DIR%..\installer\" (
    mkdir "%PLUGIN_DIR%..\installer\"
)

:: ── 6) Compilar el instalador ISS ─────────────────────────────
echo.
echo  [INFO] Compilando instalador con Inno Setup...
"%ISCC%" "%ISS%"

if !ERRORLEVEL! NEQ 0 (
    echo.
    echo  [ERROR] Inno Setup reporto un error al compilar el ISS.
    goto :error
)

echo.
echo  [OK] Instalador generado en:
echo       %PLUGIN_DIR%..\installer\RevitMcpPlugin_2021_Setup.exe
echo.

:done
echo  ============================================================
echo   Proceso completado.
echo  ============================================================
echo.
pause
endlocal
exit /b 0

:error
echo.
echo  ============================================================
echo   El proceso termino con errores. Revisa los mensajes arriba.
echo  ============================================================
echo.
pause
endlocal
exit /b 1
