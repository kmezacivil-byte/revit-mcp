@echo off
setlocal enabledelayedexpansion
title RevitMCP — Servidor MCP para Revit

:: ─────────────────────────────────────────────────────────────
::  Launcher del servidor MCP de Revit
::  Digital Jump Peru
::  Doble clic para iniciar — mantener abierto mientras uses Claude
:: ─────────────────────────────────────────────────────────────

echo.
echo  ============================================================
echo    RevitMCP ^| Servidor MCP para Revit ^| Digital Jump Peru
echo  ============================================================
echo.

:: Carpeta donde está este .bat
set "ROOT=%~dp0"
set "SERVER=%ROOT%server"
set "MAIN=%SERVER%\main.py"

:: ── Verificar que existe main.py ──────────────────────────────
if not exist "%MAIN%" (
    echo  [ERROR] No se encontro el archivo:
    echo          %MAIN%
    echo.
    echo  Verifica que este .bat esta en la carpeta raiz del proyecto.
    goto :error
)

:: ── Buscar Python ─────────────────────────────────────────────
set "PYTHON="

:: 1) Venv local (si existe)
if exist "%ROOT%venv\Scripts\python.exe" (
    set "PYTHON=%ROOT%venv\Scripts\python.exe"
    echo  [INFO] Usando entorno virtual: venv\Scripts\python.exe
    goto :check_deps
)

:: 2) Python del sistema
where python >nul 2>&1
if !ERRORLEVEL! == 0 (
    set "PYTHON=python"
    for /f "tokens=*" %%v in ('python --version 2^>^&1') do echo  [INFO] Usando %%v del sistema
    goto :check_deps
)

:: 3) py launcher (Windows)
where py >nul 2>&1
if !ERRORLEVEL! == 0 (
    set "PYTHON=py"
    for /f "tokens=*" %%v in ('py --version 2^>^&1') do echo  [INFO] Usando %%v ^(py launcher^)
    goto :check_deps
)

echo  [ERROR] Python no encontrado.
echo.
echo  Instala Python 3.9 o superior desde https://www.python.org
echo  y asegurate de marcar "Add Python to PATH" durante la instalacion.
goto :error

:check_deps
:: ── Verificar / instalar dependencias ────────────────────────
echo.
echo  [INFO] Verificando dependencias...
%PYTHON% -c "import fastmcp" >nul 2>&1
if !ERRORLEVEL! NEQ 0 (
    echo  [INFO] Instalando dependencias desde requirements.txt...
    %PYTHON% -m pip install -r "%SERVER%\requirements.txt" --quiet
    if !ERRORLEVEL! NEQ 0 (
        echo  [ERROR] No se pudieron instalar las dependencias.
        echo          Ejecuta manualmente:
        echo          pip install -r server\requirements.txt
        goto :error
    )
    echo  [OK]   Dependencias instaladas correctamente.
) else (
    echo  [OK]   Dependencias OK.
)

:: ── Iniciar servidor ──────────────────────────────────────────
echo.
echo  [INFO] Iniciando servidor MCP en localhost:5001
echo  [INFO] Mantener esta ventana abierta mientras uses Claude con Revit.
echo  [INFO] Presiona Ctrl+C para detener el servidor.
echo.
echo  ============================================================
echo.

cd /d "%SERVER%"
%PYTHON% main.py

:: Si el servidor se detiene con error
echo.
echo  [INFO] El servidor se ha detenido.

:error
echo.
pause
endlocal
