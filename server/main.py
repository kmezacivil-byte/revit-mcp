import socket
import json
from fastmcp import FastMCP

HOST = "localhost"
PORT = 5001
TIMEOUT = 65

mcp = FastMCP("revit-mcp")


def _send_command(command: str, args: dict) -> dict:
    """Envía un comando al plugin C# en Revit y retorna la respuesta."""
    # Protocolo newline-delimited: una línea JSON + \n por lado.
    # Evita problemas de half-close TCP en .NET Framework 4.8 (Revit 2021).
    payload = (json.dumps({"command": command, "args": args}) + "\n").encode("utf-8")
    try:
        with socket.create_connection((HOST, PORT), timeout=TIMEOUT) as sock:
            sock.sendall(payload)

            # Leer la respuesta línea a línea hasta recibir \n
            raw = b""
            while True:
                chunk = sock.recv(4096)
                if not chunk:
                    break
                raw += chunk
                if b"\n" in raw:
                    break

        return json.loads(raw.decode("utf-8").strip())

    except ConnectionRefusedError:
        return {"ok": False, "error": "No se puede conectar a Revit. ¿Está abierto con el plugin cargado?"}
    except TimeoutError:
        return {"ok": False, "error": f"Timeout: Revit no respondió en {TIMEOUT}s"}
    except json.JSONDecodeError as e:
        return {"ok": False, "error": f"Respuesta inválida de Revit: {e}"}


def _parse_result(response: dict) -> str:
    """Extrae el resultado o devuelve el error como texto."""
    if not response.get("ok"):
        return f"Error: {response.get('error', 'Error desconocido')}"
    result = response.get("result", "{}")
    if isinstance(result, str):
        try:
            return json.dumps(json.loads(result), ensure_ascii=False, indent=2)
        except json.JSONDecodeError:
            return result
    return json.dumps(result, ensure_ascii=False, indent=2)


# ──────────────────────────────────────────────
# Herramientas MCP
# ──────────────────────────────────────────────

@mcp.tool()
def ping() -> str:
    """
    Verifica que el plugin de Revit está activo y responde.
    Retorna la versión de Revit y el nombre del proyecto abierto.
    """
    return _parse_result(_send_command("ping", {}))


@mcp.tool()
def get_project_info() -> str:
    """
    Obtiene información general del proyecto Revit activo:
    título, ruta, versión de Revit, nombre del proyecto, número,
    autor, organización y total de elementos.
    """
    return _parse_result(_send_command("get_project_info", {}))


@mcp.tool()
def get_levels() -> str:
    """
    Lista todos los niveles (plantas) del proyecto Revit con su elevación
    en metros y en pies, ordenados de menor a mayor.
    """
    return _parse_result(_send_command("get_levels", {}))


@mcp.tool()
def get_rooms() -> str:
    """
    Lista todos los ambientes/espacios del proyecto Revit con:
    número, nombre, área (m²), nivel y perímetro (m).
    Solo incluye ambientes con área > 0.
    """
    return _parse_result(_send_command("get_rooms", {}))


@mcp.tool()
def get_elements_by_category(categoria: str) -> str:
    """
    Retorna todos los elementos de una categoría específica del proyecto.

    Categorías disponibles:
      muros / walls, puertas / doors, ventanas / windows,
      pisos / floors, techos / roofs, columnas / columns,
      vigas / beams, escaleras / stairs, mobiliario / furniture,
      tuberias / pipes, ductos / ducts

    Args:
        categoria: Nombre de la categoría (p.ej. "muros", "puertas", "vigas")
    """
    return _parse_result(_send_command("get_elements_by_category", {"categoria": categoria}))


@mcp.tool()
def get_sheets() -> str:
    """
    Lista todas las láminas (sheets) del proyecto Revit con su número,
    nombre, diseñador y cantidad de vistas colocadas.
    """
    return _parse_result(_send_command("get_sheets", {}))


@mcp.tool()
def create_wall(
    x1: float,
    y1: float,
    x2: float,
    y2: float,
    nivel: str = "",
    tipo_muro: str = "",
    altura_m: float = 3.0
) -> str:
    """
    Crea un muro recto en el proyecto Revit activo.

    Args:
        x1: Coordenada X del punto inicial (metros)
        y1: Coordenada Y del punto inicial (metros)
        x2: Coordenada X del punto final (metros)
        y2: Coordenada Y del punto final (metros)
        nivel: Nombre del nivel (p.ej. "Nivel 1"). Si está vacío, usa el nivel más bajo.
        tipo_muro: Nombre del tipo de muro. Si está vacío, usa el primer tipo disponible.
        altura_m: Altura del muro en metros (por defecto 3.0)
    """
    return _parse_result(_send_command("create_wall", {
        "x1": str(x1),
        "y1": str(y1),
        "x2": str(x2),
        "y2": str(y2),
        "nivel": nivel,
        "tipo_muro": tipo_muro,
        "altura_m": str(altura_m)
    }))


if __name__ == "__main__":
    mcp.run()
