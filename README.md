# RevitMCP — Digital Jump Peru

Servidor **MCP (Model Context Protocol)** que vive dentro de Autodesk Revit (2021–2025) y permite a Claude u otro cliente MCP consultar y modificar modelos, crear familias paramétricas y exportar documentación.

> Versión 2.x: el servidor MCP está **dentro del add-in C#**. Ya no hay servidor Python ni puerto TCP propio.

```
Claude Desktop ──stdio──▶ RevitMcpBridge.exe ──HTTP + token──▶ Plugin C# (servidor MCP :5002) ──▶ API de Revit
Claude Code    ──────────────── HTTP + token ───────────────▶
```

- **Plugin** (`plugin/`): implementa MCP (Streamable HTTP sin estado, spec 2026-07-28 con compatibilidad hacia atrás) en `http://127.0.0.1:5002/mcp`. Solo escucha en local y exige `Authorization: Bearer <token>`; el token se genera en `%LOCALAPPDATA%\RevitMCP\token.txt`.
- **Puente** (`bridge/`): programa .NET Framework 4.8 que traduce stdio ⇄ HTTP para clientes que solo lanzan procesos locales (Claude Desktop). Avisa con `notifications/tools/list_changed` cuando cambia la lista de herramientas.
- **Instalador** (`plugin/RevitMcpPlugin.iss`, Inno Setup): instala el plugin para un año de Revit y registra el servidor `revit` en `claude_desktop_config.json`.

## Compilar e instalar

Requisitos: .NET SDK, Inno Setup 6 y Revit instalado (las DLL de Revit se referencian desde `C:\Program Files\Autodesk\Revit <año>`).

```bat
plugin\empaquetar.bat 2025      :: genera installer\RevitMcpPlugin_2025_Setup.exe  (2021–2025)
```

Cierra Revit y Claude Desktop, ejecuta el instalador, abre Revit con un proyecto y reinicia Claude Desktop una vez.

## Herramientas (73)

| Grupo | Herramientas |
|---|---|
| Consulta | `ping`, `get_project_info`, `get_levels`, `get_rooms`, `get_sheets`, `get_views`, `get_view_info`, `get_view_elements`, `get_selection`, `get_categories`, `get_families`, `get_system_types`, `get_elements_by_category`, `filter_elements`, `get_element_properties`, `get_model_statistics`, `get_material_quantities`, `get_mep_types`, `get_family_geometry` |
| Crear | `create_level`, `create_grid`, `create_floor`, `create_roof`, `create_room`, `create_sheet`, `create_view` (planta, 3D, sección), `create_wall`, `create_door`, `create_window`, `create_duct`, `create_pipe`, `place_family_instance`, `duplicate_type` |
| Modificar | `set_parameter`, `change_element_type`, `move_elements`, `rotate_elements`, `color_elements`, `clear_color_overrides`, `tag_elements`, `select_elements`, `set_active_view`, `delete_elements` |
| Análisis y salida | `detect_clashes`, `export_pdf`, `export_image`, `export_ifc`, `link_revit_file`, `load_family`, `save_document` |
| Familias | `create_family`, `save_family`, `close_family`, `list_open_families`, `list_family_templates`, `add_family_parameter`, `set_family_formula`, `create_family_type`, `activate_family_type`, `set_family_type_value`, `list_family_parameters`, `create_reference_plane`, `create_family_dimension`, `create_extrusion`, `create_sweep`, `create_revolution`, `align_to_plane`, `associate_family_material`, `list_family_elements`, `nest_family`, `associate_nested_parameter`, `build_family_from_spec` |
| Código a medida | `execute_code` (opcional, solo Revit 2025) |

Convenciones: nombres de tools en inglés, descripciones y parámetros en español; longitudes en **metros** (o con unidad, `"125 mm"`), ángulos en **grados**; todo cambio va en una `Transaction` con nombre (Ctrl+Z lo deshace). Las acciones que borran o modifican en masa exigen `confirmar=true`. Cada tool nueva es una clase que hereda de `McpTool` (`plugin/Core/ToolBase.cs`) y se registra sola por reflexión.

### Familias paramétricas

`create_extrusion` dibuja perfiles predefinidos (`rectangulo`, `circulo`, `anillo`, `cajon`, `perfil_I`, `perfil_C`, `perfil_L`, `poligono`) cuyas medidas siguen a parámetros de la familia: crea planos de referencia, cotas etiquetadas y alineaciones bloqueadas. `centro:[u,v]` desplaza el perfil (valor fijo o parámetro). `build_family_from_spec` construye una familia completa desde un JSON; hay ejemplos en [`ejemplos/familias/`](ejemplos/familias) (tubo hueco, cajón, IPE120, placa, cajón de trasvase, pescante giratorio).

Notas de la API de Revit descubiertas en el camino:
- `GenericForm.Name` no se puede asignar (lanza `InvalidOperationException` con un mensaje engañoso): los nombres de pieza viven en el registro de la sesión.
- `NewSweep` solo acepta el perfil en el plano XY con el camino saliendo hacia +Z; los barridos horizontales o inclinados se construyen así y luego se giran.

## execute_code (opcional)

Permite a Claude ejecutar C# dentro de Revit (Roslyn) cuando ninguna tool sirve. **Desactivada por defecto**, solo Revit 2025. Se activa desde la pestaña **RevitMCP ▸ Configuración MCP** (guarda `%LOCALAPPDATA%\RevitMCP\config.json`; ninguna tool MCP puede modificarlo). Modos: `solo_leer` (transacción que siempre se revierte) y `modificar` (permiso aparte + `confirmar=true`). Bloquea archivos, red, procesos, reflexión, `dynamic` y guardar/cerrar/exportar documentos; cada ejecución se registra en `execute_code.log`. Es una red de seguridad, **no un sandbox**.

## Limitaciones conocidas

- El giro parametrizado del brazo del pescante (±90°), pernos, topes y compuerta abatible no están resueltos.
- Los barridos y revoluciones y el perfil `poligono` tienen dimensiones fijas.
- Un script de `execute_code` que ignore `Cancel` no se puede interrumpir.
- En el equipo de desarrollo, fijar `ContentLength64` en `HttpListener` producía respuestas vacías; el servidor responde siempre en *chunked*.

Más contexto histórico en [`LESSONS_LEARNED.md`](LESSONS_LEARNED.md) (parte describe la arquitectura 1.x con Python).
