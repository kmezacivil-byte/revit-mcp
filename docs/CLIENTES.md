# Conectar RevitMCP con tu IA

RevitMCP es un servidor **MCP estándar**: no depende de ningún modelo concreto. Cualquier cliente que hable MCP puede usarlo. Hay dos formas de conectarse:

| Vía | Cómo funciona | Para qué clientes |
|---|---|---|
| **Local (stdio)** | El cliente lanza `RevitMcpBridge.exe`, que habla con Revit por HTTP local con token | Claude Desktop, Gemini CLI, Codex CLI, Cursor, VS Code, LM Studio, SDK de OpenAI/Gemini en Python… |
| **Local (HTTP)** | El cliente se conecta a `http://127.0.0.1:5002/mcp` con `Authorization: Bearer <token>` | Claude Code y cualquier cliente MCP con HTTP y cabeceras |
| **Remota (HTTPS por túnel)** | Una dirección secreta `/mcp/<token>` expuesta con un túnel | ChatGPT y servicios web que solo aceptan servidores públicos |

El token local está en `%LOCALAPPDATA%\RevitMCP\token.txt`. La ruta del puente tras instalar es `C:\Program Files\Digital Jump Peru\RevitMcpPlugin\<año>\RevitMcpBridge.exe`.

> **Estado de las pruebas.** El servidor, el puente y el registro automático están probados con scripts y con Claude Desktop. Las configuraciones de los demás clientes se han verificado contra archivos de prueba, pero **no contra cada producto real**: sus formatos cambian con frecuencia, así que contrasta con la documentación oficial de cada cliente si algo no carga.

## Registro automático

El instalador registra el servidor `revit` en los clientes que detecta (Claude Desktop, Gemini CLI, Cursor, VS Code, Codex CLI). También puedes hacerlo a mano:

```bat
RevitMcpBridge.exe --list                 :: clientes soportados y si están instalados
RevitMcpBridge.exe --install gemini-cli   :: o claude-desktop | cursor | vscode | codex | all
RevitMcpBridge.exe --uninstall all
```

Cierra y vuelve a abrir el cliente tras registrar. Una vez conectado, cambiar herramientas desde Revit (pestaña **RevitMCP ▸ Configuración MCP**) se refleja solo: el puente avisa con `notifications/tools/list_changed`.

## Claude Desktop / Claude Code

- **Claude Desktop**: lo registra el instalador.
- **Claude Code**: `claude mcp add revit -- "C:\Program Files\Digital Jump Peru\RevitMcpPlugin\2025\RevitMcpBridge.exe"`, o por HTTP:
  `claude mcp add --transport http revit http://127.0.0.1:5002/mcp --header "Authorization: Bearer <token>"`

## Gemini (Google)

- **Gemini CLI** (recomendado): lo registra el instalador en `%USERPROFILE%\.gemini\settings.json`:
  ```json
  { "mcpServers": { "revit": { "command": "C:\\Program Files\\Digital Jump Peru\\RevitMcpPlugin\\2025\\RevitMcpBridge.exe", "args": [] } } }
  ```
  Por HTTP: `"revit": { "httpUrl": "http://127.0.0.1:5002/mcp", "headers": { "Authorization": "Bearer <token>" } }`.
- **App web de Gemini**: no admite servidores MCP propios; usa Gemini CLI o la API.
- **API de Gemini (Python)**: el SDK `google-genai` puede recibir una sesión MCP como herramienta (función experimental, revisa la documentación vigente):
  ```python
  from google import genai
  from mcp import ClientSession, StdioServerParameters
  from mcp.client.stdio import stdio_client

  server = StdioServerParameters(command=r"C:\Program Files\Digital Jump Peru\RevitMcpPlugin\2025\RevitMcpBridge.exe", args=[])
  async with stdio_client(server) as (read, write):
      async with ClientSession(read, write) as session:
          await session.initialize()
          client = genai.Client()
          resp = await client.aio.models.generate_content(
              model="<modelo>", contents="¿Cuántos muros tiene el proyecto?",
              config=genai.types.GenerateContentConfig(tools=[session]))
  ```
- Las funciones de Gemini aceptan un subconjunto de JSON Schema: por eso los esquemas de RevitMCP usan solo `type`, `properties`, `required`, `items` y `description`. Si un modelo rinde peor con muchas herramientas, elige el perfil **solo lectura**, **modelado** o **familias** en la ventana de Configuración MCP.

## ChatGPT / OpenAI

ChatGPT **no puede conectarse a `localhost`**: solo llega a servidores públicos por HTTPS, y su autenticación es OAuth, sin autenticación o mixta (no admite cabeceras con token). Por eso RevitMCP ofrece el **acceso remoto**, desactivado por defecto:

1. Descarga `cloudflared-windows-amd64.exe` de [github.com/cloudflare/cloudflared/releases/latest](https://github.com/cloudflare/cloudflared/releases/latest). **No se instala ni se abre con doble clic**: es un programa de línea de comandos; déjalo en tu carpeta de Descargas.
2. En Revit: pestaña **RevitMCP ▸ Configuración MCP ▸ Acceso remoto ▸ Permitir acceso remoto** (empieza en **solo lectura**).
3. Pulsa **Iniciar túnel**: la ventana busca `cloudflared.exe` (PATH, Descargas, Program Files), lo ejecuta y rellena la dirección pública. Si ya lo tienes corriendo (por ejemplo `cloudflared tunnel --url http://127.0.0.1:5002` en PowerShell), pulsa **Detectar túnel**.
4. La ventana muestra la **dirección final** (`https://<algo>.trycloudflare.com/mcp/<token>`): pulsa **Copiar dirección para ChatGPT**.
5. En ChatGPT (Complementos ▸ Añadir servidor MCP personalizado, con el modo desarrollador habilitado según tu plan), pega esa dirección en **URL del servidor** y deja **Sin autenticación**.

Los túneles rápidos cambian de dirección cada vez que se reinician: tras reiniciar el túnel, copia la dirección nueva y actualízala en ChatGPT.

Seguridad: la dirección con el token **es la credencial**. Quien la tenga puede usar el servidor (solo leer, o también modificar si desactivas "solo lectura"). Regenera el token desde la ventana si se filtra, y cierra el túnel cuando no lo uses. El acceso remoto nunca expone `execute_code` ni las herramientas que escriben archivos mientras esté en solo lectura. No lo he probado contra el ChatGPT real.

- **Codex CLI** (OpenAI): lo registra el instalador en `%USERPROFILE%\.codex\config.toml` (`[mcp_servers.revit]`).
- **SDK de agentes de OpenAI (Python)**: `MCPServerStdio(params={"command": r"...\RevitMcpBridge.exe", "args": []})` y pásalo en `mcp_servers=[...]` del agente. La herramienta MCP remota de la API de Respuestas necesita una URL pública (usa el túnel anterior).

## Cursor, VS Code, LM Studio y otros

- **Cursor**: `%USERPROFILE%\.cursor\mcp.json` con `{"mcpServers": {"revit": {"command": "...RevitMcpBridge.exe", "args": []}}}`.
- **VS Code (Copilot)**: `%APPDATA%\Code\User\mcp.json` con `{"servers": {"revit": {"type": "stdio", "command": "...RevitMcpBridge.exe", "args": []}}}`.
- **LM Studio, Ollama con un host MCP, Open WebUI (vía un proxy MCP→OpenAPI) y otros**: cualquier host MCP que lance un comando por stdio sirve; usa `RevitMcpBridge.exe` como comando.

## Qué ve el modelo

Tras conectar, el modelo recibe el catálogo de herramientas (79 en el perfil completo). Las descripciones están en español y llevan un ejemplo al final. Con modelos pequeños conviene un perfil reducido y empezar por `get_project_info` o `get_model_statistics`.
