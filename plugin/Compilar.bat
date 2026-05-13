@echo off
setlocal enabledelayedexpansion
title Compilar RevitMcpPlugin

echo.
echo  ============================================================
echo    RevitMCP ^| Compilar + Empaquetar
echo    Digital Jump Peru
echo  ============================================================
echo.

:: ── Versiones soportadas ─────────────────────────────────────────
set "VERSIONES_SOPORTADAS=2021 2022 2023 2024 2025"

:: ── Leer version desde argumento o preguntar ────────────────────
set "REVIT_VER=%~1"

if "%REVIT_VER%"=="" (
    echo  Versiones disponibles: %VERSIONES_SOPORTADAS%
    echo.
    set /p "REVIT_VER=  Ingresa la version de Revit a compilar (ej: 2024): "
)

:: ── Validar version ─────────────────────────────────────────────
set "VALID=0"
for %%v in (%VERSIONES_SOPORTADAS%) do (
    if "!REVIT_VER!"=="%%v" set "VALID=1"
)
if "!VALID!"=="0" (
    echo.
    echo  [ERROR] Version '!REVIT_VER!' no soportada.
    echo          Usa una de: %VERSIONES_SOPORTADAS%
    goto :error
)

:: ── Configurar rutas ────────────────────────────────────────────
set "PLUGIN_DIR=%~dp0"
set "DLL_OUT=%PLUGIN_DIR%build-output-!REVIT_VER!\RevitMcpPlugin.dll"
set "ISS=%PLUGIN_DIR%RevitMcpPlugin_!REVIT_VER!.iss"
set "REVIT_API=C:\Program Files\Autodesk\Revit !REVIT_VER!\RevitAPI.dll"

echo.
echo  [INFO] Compilando para Revit !REVIT_VER!...

:: ── Detectar framework objetivo ─────────────────────────────────
if "!REVIT_VER!"=="2025" (
    set "FW_DESC=net8.0-windows"
) else (
    set "FW_DESC=net48 ^(.NET Framework 4.8^)"
)
echo  [INFO] Framework objetivo: !FW_DESC!
echo.

:: ── 1) Verificar .NET SDK ───────────────────────────────────────
where dotnet >nul 2>&1
if !ERRORLEVEL! NEQ 0 (
    echo  [ERROR] dotnet no encontrado. Instala el SDK de .NET desde:
    echo          https://dotnet.microsoft.com/download
    goto :error
)
for /f "tokens=*" %%v in ('dotnet --version') do echo  [INFO] .NET SDK %%v detectado.

:: ── 2) Verificar que Revit este instalado ───────────────────────
if not exist "%REVIT_API%" (
    echo.
    echo  [ADVERTENCIA] No se encontro RevitAPI.dll en:
    echo                %REVIT_API%
    echo.
    echo  El build puede fallar si Revit !REVIT_VER! no esta instalado.
    echo  Presiona cualquier tecla para continuar de todas formas...
    pause >nul
)

:: ── 3) Compilar ─────────────────────────────────────────────────
echo.
echo  [INFO] Ejecutando: dotnet build -c Release -p:RevitVersion=!REVIT_VER!
echo.
cd /d "%PLUGIN_DIR%"
dotnet build RevitMcpPlugin.csproj -c Release "-p:RevitVersion=!REVIT_VER!" --nologo

if !ERRORLEVEL! NEQ 0 (
    echo.
    echo  [ERROR] La compilacion fallo. Revisa los errores arriba.
    goto :error
)

if not exist "!DLL_OUT!" (
    echo.
    echo  [ERROR] El DLL no fue generado en: !DLL_OUT!
    goto :error
)
echo.
echo  [OK] DLL generado: !DLL_OUT!

:: ── 4) Compilar instalador Inno Setup (si existe el .iss) ───────
if not exist "!ISS!" (
    echo.
    echo  [INFO] No existe ISS para Revit !REVIT_VER! en:
    echo         !ISS!
    echo         Omitiendo paso del instalador.
    goto :done
)

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
echo  [ADVERTENCIA] Inno Setup no encontrado. El DLL ya fue compilado.
echo  Para generar el instalador, abre Inno Setup y compila:
echo    !ISS!
goto :done

:found_iscc
echo.
echo  [INFO] Inno Setup: !ISCC!

if not exist "%PLUGIN_DIR%..\installer\" mkdir "%PLUGIN_DIR%..\installer\"

echo  [INFO] Compilando instalador...
"!ISCC!" "!ISS!"

if !ERRORLEVEL! NEQ 0 (
    echo.
    echo  [ERROR] Inno Setup reporto un error.
    goto :error
)

echo.
echo  [OK] Instalador generado en:
echo       %PLUGIN_DIR%..\installer\RevitMcpPlugin_!REVIT_VER!_Setup.exe

:done
echo.
echo  ============================================================
echo   Proceso completado correctamente ^| Revit !REVIT_VER!
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
