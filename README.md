# MCP Revit — Digital Jump Peru

Servidor MCP (Model Context Protocol) para controlar **Autodesk Revit 2025** desde Claude u otro cliente MCP. Arquitectura idéntica al MCP Civil 3D: plugin C# en Revit + servidor Python FastMCP comunicados por TCP en localhost.

```
Claude Desktop  ↔  Python FastMCP (puerto 5001)  ↔  Plugin C# (Revit API)  ↔  Revit 2025
```

---

## Estructura

```
MCP REVIT/
├── server/
│   ├── main.py                          # Servidor FastMCP (Python)
│   └── requirements.txt
└── plugin/
    ├── RevitMcpPlugin.csproj            # Proyecto .NET framework 4.8
    ├── RevitMcpPlugin.sln
    ├── RevitMcpPlugin.addin             # Manifiesto de carga para Revit
    ├── Plugin.cs                        # IExternalApplication (entry point)
    ├── CommandListener.cs               # Servidor TCP en hilo background
    ├── RevitCommandHandler.cs           # IExternalEventHandler (puente de hilos)
    └── Handlers/
        ├── ICommandHandler.cs
        ├── PingHandler.cs
        ├── ProjectInfoHandler.cs
        ├── GetLevelsHandler.cs
        ├── GetRoomsHandler.cs
        ├── GetElementsByCategoryHandler.cs
        ├── CreateWallHandler.cs
        └── GetSheetsHandler.cs
```

---

## Herramientas disponibles

| Herramienta | Descripción |
|---|---|
| `ping` | Verifica que Revit y el plugin estén activos |
| `get_project_info` | Nombre, ruta, versión, autor, total de elementos |
| `get_levels` | Todos los niveles con elevación en metros y pies |
| `get_rooms` | Ambientes con área (m²), nivel, perímetro |
| `get_elements_by_category` | Elementos por categoría (muros, puertas, vigas…) |
| `get_sheets` | Láminas con número, nombre y vistas colocadas |
| `create_wall` | Crea un muro dado dos puntos en metros |

---

## Instalación

### 1. Compilar el plugin C#

```powershell
cd plugin
dotnet build -c Release
# DLL generado en: plugin\build-output\RevitMcpPlugin.dll
```

> Requiere Revit 2025 instalado en `C:\Program Files\Autodesk\Revit 2025\`.

### 2. Registrar el plugin en Revit

Copiar el archivo `.addin` a la carpeta de addins de Revit:

```powershell
Copy-Item "plugin\RevitMcpPlugin.addin" "$env:APPDATA\Autodesk\Revit\Addins\2025\"
```

O a la carpeta de todos los usuarios:
```
C:\ProgramData\Autodesk\Revit\Addins\2025\
```

> El `.addin` ya apunta al DLL en `build-output\`. Si mueves el DLL, actualiza la ruta `<Assembly>` en el archivo `.addin`.

### 3. Abrir Revit

Al iniciar Revit 2025 aparecerá un diálogo confirmando:
```
[RevitMCP] Plugin cargado. Escuchando en localhost:5001
```

### 4. Instalar dependencias Python

```powershell
cd server
pip install -r requirements.txt
```

### 5. Ejecutar el servidor MCP

```powershell
python main.py
```

### 6. Configurar Claude Desktop

Editar `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "revit": {
      "command": "python",
      "args": ["C:\\Users\\ASUS TUF F16\\Desktop\\DIGITAL JUMP PERU\\MCP REVIT\\server\\main.py"]
    }
  }
}
```

---

## Diferencias clave con MCP Civil 3D

| Aspecto | Civil 3D | Revit |
|---|---|---|
| Puerto TCP | 5001 | 5001 |
| Entry point | `IExtensionApplication` | `IExternalApplication` |
| Thread safety | `ExecuteInCommandContextAsync` | `ExternalEvent` + `IExternalEventHandler` |
| Carga del plugin | `NETLOAD` manual | `.addin` automático al iniciar Revit |
| Unidades internas | mm (Civil 3D configurable) | Pies (se convierte desde metros) |

---

## Agregar nuevos comandos

1. Crear `plugin/Handlers/MiNuevoHandler.cs` implementando `ICommandHandler`
2. Registrarlo en `RevitCommandHandler.cs` en el diccionario `_handlers`
3. Exponer la herramienta en `server/main.py` con `@mcp.tool()`
4. Recompilar el DLL y reiniciar Revit
