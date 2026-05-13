# Lessons Learned — RevitMCP Plugin

## Contexto del Proyecto

Plugin que conecta **Claude AI** con **Autodesk Revit 2021** mediante el protocolo MCP
(Model Context Protocol). Permite a un LLM consultar y modificar un modelo BIM en tiempo real.

| Capa | Tecnología |
|---|---|
| Servidor MCP | Python 3.x · FastMCP 2.3.4 · socket TCP |
| Plugin Revit | C# · .NET Framework 4.8 · Revit API 2021 |
| Protocolo | TCP localhost:5001 · newline-delimited JSON |
| Serialización | Newtonsoft.Json (bundled con Revit) |
| Instalador | Inno Setup 6 · genera el `.addin` en tiempo de instalación |

---

## Problemas Resueltos

### 1. TypeLoadException al cargar el DLL en Revit 2021

- **Síntoma:** Revit 2021 mostraba `TypeLoadException` o `BadImageFormatException` al intentar cargar el addin. El plugin no arrancaba.
- **Causa raíz:** El proyecto compilaba por defecto en `net8.0-windows`. Revit 2021 corre sobre .NET Framework 4.8 y no puede cargar ensamblados compilados para .NET 8.
- **Solución aplicada:** Configurar el `.csproj` con target framework condicional según la versión de Revit:
  ```xml
  <PropertyGroup Condition="'$(RevitVersion)' == '2021'">
    <TargetFramework>net48</TargetFramework>
    <OutputPath>build-output-2021\</OutputPath>
    <LangVersion>latest</LangVersion>
    <DefineConstants>REVIT2021</DefineConstants>
  </PropertyGroup>
  ```
  Compilar con: `dotnet build -c Release "-p:RevitVersion=2021"`
- **Tags:** `[Build]` `[Revit API]` `[.NET versioning]`

---

### 2. TypeInitializationException: System.Text.Json en Revit 2021

- **Síntoma:** El plugin cargaba pero al recibir cualquier comando lanzaba `TypeInitializationException` cuyo inner exception apuntaba al inicializador estático de `System.Text.Json.JsonSerializer`. El log mostraba el crash exactamente al primer uso de `JsonSerializer`.
- **Causa raíz:** El AppDomain de Revit 2021 ya carga sus propias versiones de `System.Memory`, `System.Buffers` y `System.Runtime.CompilerServices.Unsafe`. System.Text.Json depende de versiones específicas de estos ensamblados; cuando encuentra las versiones de Revit (incompatibles), su inicializador estático colapsa. No hay forma de resolverlo via `bindingRedirect` en un addin externo.
- **Solución aplicada:** Reemplazar **completamente** System.Text.Json por **Newtonsoft.Json**, que ya viene empaquetado con Revit y no tiene conflictos:
  ```xml
  <!-- .csproj — Private=false: no copiar la DLL, usar la de Revit -->
  <Reference Include="Newtonsoft.Json">
    <HintPath>C:\Program Files\Autodesk\Revit 2021\Newtonsoft.Json.dll</HintPath>
    <Private>false</Private>
  </Reference>
  ```
  ```csharp
  // En lugar de System.Text.Json.JsonSerializer.Serialize(obj)
  JsonConvert.SerializeObject(new { ok = true, result = data });
  JObject.Parse(jsonString)["command"]?.Value<string>();
  ```
  El instalador quedó con **un solo DLL** (38 KB) sin dependencias externas.
- **Tags:** `[Build]` `[Revit API]` `[Serialización]` `[AppDomain]`

---

### 3. ConnectionResetError inmediato (WinError 10054)

- **Síntoma:** El servidor Python se conectaba a localhost:5001 correctamente pero recibía inmediatamente un RST (Reset) de TCP antes de recibir ningún byte. `diagnostico.py` confirmaba: `conn OK → recv → ConnectionResetError`.
- **Causa raíz (A):** El servidor C# usaba `ReadToEndAsync()` sobre un `StreamReader`. En net48, `ReadToEndAsync` bloquea esperando EOF (cierre del socket). El cliente Python nunca cerraba su mitad de escritura, así que el servidor nunca avanzaba y el hilo se colgaba.
- **Causa raíz (B):** Al migrar a un protocolo newline-delimited, el error persistió porque System.Text.Json crasheaba en la primera instrucción del handler (ver problema anterior), cerrando el socket desde el servidor antes de responder nada.
- **Solución aplicada:** Protocolo **newline-delimited** (un JSON por línea) + lectura byte a byte sin `StreamReader`:
  ```csharp
  private static string ReadLineFromStream(NetworkStream stream)
  {
      var buffer = new List<byte>(256);
      var oneByte = new byte[1];
      while (true)
      {
          int n = stream.Read(oneByte, 0, 1);
          if (n == 0) break;
          if (oneByte[0] == (byte)'\n') break;
          if (oneByte[0] != (byte)'\r') buffer.Add(oneByte[0]);
      }
      return Encoding.UTF8.GetString(buffer.ToArray());
  }
  ```
  En Python:
  ```python
  payload = (json.dumps({"command": cmd, "args": args}) + "\n").encode("utf-8")
  sock.sendall(payload)
  raw = b""
  while True:
      chunk = sock.recv(4096)
      if not chunk: break
      raw += chunk
      if b"\n" in raw: break
  ```
- **Tags:** `[TCP]` `[Protocolo]` `[net48]`

---

### 4. async/await incompatible con net48

- **Síntoma:** Múltiples errores de compilación al apuntar a net48: `AcceptTcpClientAsync(CancellationToken)` no existe, `StreamReader` sin constructor `leaveOpen`, `WriteAsync(byte[])` sin overload, etc.
- **Causa raíz:** Varios métodos `async` de `System.Net.Sockets` y `System.IO` solo existen en .NET Core / .NET 5+. .NET Framework 4.8 tiene un subconjunto limitado de la API `async`.
- **Solución aplicada:** Reescribir el servidor TCP usando **`Thread` explícito** (no `Task`), y métodos síncronos únicamente:
  ```csharp
  // Accept loop — síncrono
  var client = _listener.AcceptTcpClient();   // bloquea OK en hilo dedicado
  var t = new Thread(() => HandleClient(client)) { IsBackground = true };
  t.Start();

  // Escritura síncrona
  var bytes = Encoding.UTF8.GetBytes(response + "\n");
  stream.Write(bytes, 0, bytes.Length);
  stream.Flush();
  ```
- **Tags:** `[Build]` `[net48]` `[Threading]`

---

### 5. ElementId: IntegerValue vs Value

- **Síntoma:** Código compilaba en Revit 2025 pero fallaba en Revit 2021 con `CS1061: ElementId no contiene definición para 'Value'`.
- **Causa raíz:** La propiedad `ElementId.Value` (tipo `long`) se introdujo en Revit 2024 API. Revit 2021 solo expone `ElementId.IntegerValue` (tipo `int`).
- **Solución aplicada:** Extension method con compilación condicional en `RevitApiCompat.cs`:
  ```csharp
  public static long GetId(this ElementId elementId)
  {
  #if REVIT2021
      return elementId.IntegerValue;
  #else
      return elementId.Value;
  #endif
  }
  // Uso uniforme en todos los handlers:
  id = element.Id.GetId()
  ```
- **Tags:** `[Revit API]` `[Compatibilidad]` `[Build]`

---

### 6. Dictionary.GetValueOrDefault ausente en net48

- **Síntoma:** `CS1061: Dictionary<string,string> no contiene definición para 'GetValueOrDefault'` al compilar para net48.
- **Causa raíz:** `Dictionary<TKey,TValue>.GetValueOrDefault()` se añadió en .NET Core 2.0 / .NET 5+. No existe en .NET Framework 4.8.
- **Solución aplicada:** Extension method en `RevitApiCompat.cs` bajo `#if REVIT2021`:
  ```csharp
  #if REVIT2021
  public static TValue GetValueOrDefault<TKey, TValue>(
      this Dictionary<TKey, TValue> dict, TKey key, TValue defaultValue)
  {
      return dict.TryGetValue(key, out var val) ? val : defaultValue;
  }
  #endif
  ```
- **Tags:** `[net48]` `[Build]` `[Compatibilidad]`

---

### 7. Corrupción de UTF-8 con PowerShell Set-Content

- **Síntoma:** Al escribir archivos `.cs` con PowerShell (`Set-Content -Encoding UTF8`), los caracteres especiales en español (`ñ`, `á`, `í`, `ó`, `ú`) aparecían corruptos en el archivo como `Ã±`, `Ã¡`, `Ã­`. Esto rompía nombres de campo en el JSON de respuesta y mensajes de error.
- **Causa raíz:** `Set-Content -Encoding UTF8` en PowerShell 5.1 (Windows PowerShell, no PowerShell Core) escribe UTF-8 **con BOM**, y algunos parsers/editores lo interpretan mal. Además, el pipe de terminal puede hacer una conversión de codepage adicional.
- **Solución aplicada:** Reescribir los archivos afectados con la herramienta `Write` (que garantiza UTF-8 sin BOM), y **eliminar caracteres especiales de nombres de campo JSON** donde no son estrictamente necesarios:
  ```csharp
  // ANTES (corrompido): diseñador, número, área
  // DESPUÉS: designado_por, numero, area_m2
  ```
- **Tags:** `[Encoding]` `[PowerShell]` `[Build]`

---

### 8. Timeout en get_project_info por FilteredElementCollector global

- **Síntoma:** La herramienta `get_project_info` causaba timeout en Claude Desktop. El log mostraba que el `ExternalEvent` completaba `Wait(30s)` sin recibir señal.
- **Causa raíz:** La llamada `new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount()` itera **todos los elementos del modelo**. En proyectos con miles de elementos esto puede tardar más de 30 segundos.
- **Solución aplicada:** Eliminar el conteo del `ProjectInfoHandler` y aumentar los timeouts como segunda línea de defensa:
  ```csharp
  // Se eliminó esta línea del handler:
  // var total = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();

  // CommandListener.cs: 30s → 60s
  bool completed = handler.ExecutionDone.Wait(TimeSpan.FromSeconds(60));
  ```
  ```python
  # main.py: 35s → 65s
  TIMEOUT = 65
  ```
- **Tags:** `[Performance]` `[Revit API]` `[Timeout]`

---

### 9. ExternalEvent rechazado cuando Revit no tiene proyecto abierto

- **Síntoma:** `ev.Raise()` retornaba `ExternalEventRequest.Denied` o `NotReady` sin lanzar excepción. El `Wait()` devolvía `false` (timeout) porque el event nunca se ejecutaba.
- **Causa raíz:** La API `ExternalEvent.Raise()` solo puede ejecutarse si Revit tiene un documento activo y no está en un estado modal (diálogos abiertos, comandos activos).
- **Solución aplicada:** Verificar el status de `Raise()` inmediatamente y retornar error descriptivo:
  ```csharp
  var status = ev.Raise();
  if (status != ExternalEventRequest.Accepted)
      return Serialize(new { ok = false,
          error = $"ExternalEvent rechazado: {status}. Abre un proyecto en Revit." });
  ```
- **Tags:** `[Revit API]` `[Threading]` `[UX]`

---

## Decisiones de Arquitectura

### A. Protocolo TCP newline-delimited en lugar de HTTP

**Elegido:** Socket TCP crudo con una línea JSON por mensaje (petición + respuesta).  
**Descartado:** HTTP (HttpListener) o named pipes.  
**Razón:** Revit 2021 + net48 tiene soporte `async` limitado. Un socket TCP síncrono con `Thread` es el enfoque más robusto y predecible. HTTP añadiría complejidad (headers, métodos, status codes) sin beneficio para un protocolo request/response local de un solo cliente.

---

### B. Newtonsoft.Json en lugar de System.Text.Json

**Elegido:** `Newtonsoft.Json` (ya instalado en `C:\Program Files\Autodesk\Revit 2021\`).  
**Descartado:** `System.Text.Json` (NuGet).  
**Razón:** System.Text.Json tiene dependencias transitivas (`System.Memory`, `System.Buffers`, `System.Text.Encodings.Web`) que colisionan con versiones que Revit 2021 ya carga en su AppDomain. El crash es en el inicializador estático — no hay forma de resolverlo con `bindingRedirect` desde un addin. Usar el Newtonsoft.Json de Revit (`Private=false`) elimina todas las dependencias externas: el addin queda en **un solo DLL de 38 KB**.

---

### C. IExternalEventHandler + ManualResetEventSlim para sincronización de hilos

**Elegido:** `ExternalEvent.Raise()` + `ManualResetEventSlim.Wait()` para bloquear el hilo TCP hasta que el hilo principal de Revit complete la operación.  
**Descartado:** Dispatcher, `SynchronizationContext`, callbacks.  
**Razón:** Es el patrón oficial de Revit API para ejecutar código desde un hilo externo. `ManualResetEventSlim` es la primitiva más liviana de .NET para "esperar hasta que ocurra X", sin spinning y sin overhead de async.

---

### D. Compilación condicional por versión en lugar de proyectos separados

**Elegido:** Un único `.csproj` con `<PropertyGroup Condition="'$(RevitVersion)' == '2021'">` y constante `#if REVIT2021` en el código.  
**Descartado:** Dos proyectos `.csproj` separados (uno para 2021, otro para 2025).  
**Razón:** Mantener un único proyecto reduce duplicación. Los cambios de API entre versiones son pocos y localizados en `RevitApiCompat.cs`.

---

### E. Generación del .addin en tiempo de instalación (Pascal en Inno Setup)

**Elegido:** El instalador genera el `.addin` dinámicamente con la ruta real de instalación.  
**Descartado:** Incluir un `.addin` precocinado en el instalador.  
**Razón:** La ruta del DLL no se conoce en tiempo de compilación (depende del directorio de instalación elegido por el usuario). Generarlo con código Pascal en Inno Setup garantiza que `<Assembly>` siempre apunta al lugar correcto.

---

## Patrones Reutilizables

### Puente hilo-TCP → hilo-principal de Revit

```csharp
// RevitCommandHandler.cs — IExternalEventHandler
public ManualResetEventSlim ExecutionDone { get; } = new ManualResetEventSlim(false);
public string LastResult { get; private set; }

public void Execute(UIApplication app)          // llamado por Revit en su hilo
{
    try   { LastResult = RunHandler(app); }
    catch (Exception ex) { LastResult = SerializeError(ex); }
    finally { ExecutionDone.Set(); }            // desbloquea el hilo TCP
}

// CommandListener.cs — hilo TCP
handler.ExecutionDone.Reset();
ev.Raise();
bool ok = handler.ExecutionDone.Wait(TimeSpan.FromSeconds(60));
```

Patrón directo de aplicar en cualquier addin Revit que necesite recibir peticiones externas (REST, sockets, pipes).

---

### Extension method de compatibilidad multi-versión Revit

```csharp
// RevitApiCompat.cs
public static long GetId(this ElementId id)
{
#if REVIT2021
    return id.IntegerValue;
#else
    return id.Value;
#endif
}
```

Centralizar todas las diferencias de API en un único archivo `RevitApiCompat.cs` con `#if REVIT20XX`. Mucho más limpio que condicionalmente comentar código en cada handler.

---

### Referencia a DLLs bundled de Revit (sin copiar)

```xml
<Reference Include="Newtonsoft.Json">
  <HintPath>C:\Program Files\Autodesk\Revit 2021\Newtonsoft.Json.dll</HintPath>
  <Private>false</Private>   <!-- NO copiar al output — Revit ya lo carga -->
</Reference>
```

`Private=false` es crítico. Si se copia la DLL al directorio del addin, puede haber conflicto de versiones con la que Revit ya tiene cargada.

---

### Lectura newline-delimited sin StreamReader en net48

```csharp
private static string ReadLineFromStream(NetworkStream stream)
{
    var buffer = new List<byte>(256);
    var oneByte = new byte[1];
    while (true)
    {
        int n = stream.Read(oneByte, 0, 1);
        if (n == 0) break;
        if (oneByte[0] == (byte)'\n') break;
        if (oneByte[0] != (byte)'\r') buffer.Add(oneByte[0]);
    }
    return Encoding.UTF8.GetString(buffer.ToArray());
}
```

Evitar `StreamReader` sobre `NetworkStream` en net48: su buffering interno puede consumir bytes que no debería, y `ReadLineAsync()` tiene comportamiento no determinista con sockets.

---

### Script de diagnóstico TCP independiente

```python
# diagnostico.py — ejecutar antes de depurar el plugin
import socket, json
HOST, PORT, TIMEOUT = "localhost", 5001, 38
try:
    with socket.create_connection((HOST, PORT), timeout=TIMEOUT) as s:
        s.sendall((json.dumps({"command": "ping", "args": {}}) + "\n").encode())
        print(json.loads(s.recv(4096).decode().strip()))
except Exception as e:
    print(f"ERROR: {e}")
```

Permite aislar si el problema está en la capa TCP, en el protocolo o en el handler de Revit, sin necesidad de arrancar Claude Desktop.

---

## Lo que NO funcionó

| Enfoque | Por qué se descartó |
|---|---|
| `System.Text.Json` vía NuGet en net48 | Crash en inicializador estático por conflicto de ensamblados en AppDomain de Revit. Irrecuperable. |
| `ReadToEndAsync` + `StreamReader` sobre NetworkStream | Bloquea esperando EOF; el cliente TCP no cierra su mitad, generando deadlock. |
| `AcceptTcpClientAsync(CancellationToken)` en net48 | Overload no existe. En net48 solo hay `AcceptTcpClientAsync()` sin parámetros. |
| `Task` / `async-await` para el servidor TCP en net48 | La superficie async de Sockets en .NET Framework 4.8 es incompleta. Un `Thread` explícito es más predecible. |
| `FilteredElementCollector.GetElementCount()` en `get_project_info` | Itera todo el modelo; en proyectos grandes supera el timeout de 60 s. Eliminado del handler. |
| `.addin` precocinado incluido en el instalador | La ruta del DLL varía según el directorio de instalación elegido. Hay que generarlo en runtime. |
| `Set-Content -Encoding UTF8` de PowerShell 5.1 | Escribe UTF-8 con BOM y puede corromper caracteres especiales en español. Usar herramientas nativas o PowerShell Core. |

---

## Referencias Clave

- [Revit API — IExternalEventHandler](https://www.revitapidocs.com/2021/d3b72823-5f69-5e86-9e3e-d5082ed0b2a7.htm) — patrón oficial para llamadas cross-thread
- [FastMCP 2.x docs](https://github.com/jlowin/fastmcp) — framework MCP para Python
- [Inno Setup — Pascal Scripting](https://jrsoftware.org/ishelp/index.php?topic=scriptintro) — para generar archivos en tiempo de instalación
- [ElementId.Value vs IntegerValue](https://www.revitapidocs.com/2024/eb16beaf-e28c-c077-f4f1-e55f0f8f0f2c.htm) — diferencia Revit 2021/2024
- [Newtonsoft.Json bundled en Revit](https://thebuildingcoder.typepad.com/blog/2021/01/revit-2021-api-whats-new.html) — referencia para saber qué versión viene con cada release de Revit

---

## Checklist para Proyectos Similares

### Antes de escribir una línea de código

- [ ] Identificar el target framework de Revit: `net48` (2021), `net8.0-windows` (2025)
- [ ] Verificar qué DLLs ya vienen con Revit (Newtonsoft.Json, etc.) para no referenciarlos como NuGet
- [ ] Definir el protocolo de comunicación plugin↔servidor desde el inicio (newline-delimited JSON sobre TCP es simple y robusto)
- [ ] Preparar `RevitApiCompat.cs` vacío para centralizar diferencias de versión de API

### Al configurar el .csproj

- [ ] `<Private>false</Private>` en **todas** las referencias a DLLs de Revit y de las librerías que Revit ya carga
- [ ] `<LangVersion>latest</LangVersion>` si se usa net48 (habilita sintaxis C# moderna)
- [ ] `<DefineConstants>REVIT20XX</DefineConstants>` para compilación condicional
- [ ] Verificar que `<OutputPath>` no incluya el nombre del framework (o usar `AppendTargetFrameworkToOutputPath=false`)

### Al implementar la comunicación TCP

- [ ] Nunca usar `ReadToEndAsync()` / `ReadLine()` de `StreamReader` sobre `NetworkStream`
- [ ] Usar protocolo newline-delimited (un JSON + `\n` por mensaje)
- [ ] Leer byte a byte con `NetworkStream.Read()` hasta encontrar `\n`
- [ ] El timeout de Python debe ser ≥ timeout de C# + 5 s de margen

### Al usar la Revit API desde un hilo externo

- [ ] Toda llamada a la Revit API **debe** ir dentro de `IExternalEventHandler.Execute()`
- [ ] Verificar el retorno de `ExternalEvent.Raise()` antes de hacer `Wait()`
- [ ] Usar `ManualResetEventSlim` (no `AutoResetEvent`, no `Task`) para el semáforo
- [ ] Poner un `finally { ExecutionDone.Set(); }` para no dejar el hilo TCP colgado si hay excepción

### Rendimiento

- [ ] Evitar `FilteredElementCollector` sin categoría ni filtro — siempre acotar por `OfCategory()` o `OfClass()`
- [ ] No llamar `GetElementCount()` sobre colecciones sin filtrar en modelos de tamaño desconocido
- [ ] Si una operación puede ser lenta, retornarla como opcional (parámetro `include_count=true`)

### Al hacer el instalador

- [ ] Generar el `.addin` desde código Pascal (no incluirlo precocinado)
- [ ] Usar `ForceDirectories()` antes de `SaveStringToFile()`
- [ ] Verificar que Inno Setup pueda encontrar el DLL en la ruta declarada en `[Files]` antes de compilar
- [ ] Hacer `<Private>false</Private>` en todas las referencias bundled → el instalador solo necesita copiar **un DLL**
