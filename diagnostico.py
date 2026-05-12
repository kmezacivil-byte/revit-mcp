"""
Diagnóstico de conexión RevitMCP
Ejecutar: python diagnostico.py
"""
import socket, json, time, subprocess, sys

HOST, PORT = "localhost", 5001

def check_port():
    result = subprocess.run(
        ["netstat", "-ano"],
        capture_output=True, text=True
    )
    for line in result.stdout.splitlines():
        if f":{PORT}" in line and "LISTENING" in line:
            pid = line.split()[-1]
            print(f"  [OK] Puerto {PORT} ESCUCHANDO — PID {pid}")
            return True
    print(f"  [FAIL] Puerto {PORT} NO está escuchando")
    print("         → El plugin de Revit NO está cargado o no arrancó el listener")
    return False

def check_connect():
    try:
        s = socket.create_connection((HOST, PORT), timeout=3)
        s.close()
        print(f"  [OK] Conexión TCP exitosa a localhost:{PORT}")
        return True
    except ConnectionRefusedError:
        print(f"  [FAIL] Conexión rechazada")
        return False
    except Exception as e:
        print(f"  [FAIL] Error: {e}")
        return False

def check_response(wait_secs=38):
    payload = (json.dumps({"command": "ping", "args": {}}) + "\n").encode()
    print(f"  Enviando: {payload.decode().strip()}")
    print(f"  Timeout de espera: {wait_secs}s (C# espera 30s al ExternalEvent)")
    try:
        with socket.create_connection((HOST, PORT), timeout=wait_secs) as s:
            s.settimeout(wait_secs)
            s.sendall(payload)
            print("  Datos enviados. Esperando respuesta...", flush=True)
            raw = b""
            t0 = time.time()
            while True:
                try:
                    chunk = s.recv(4096)
                    elapsed = time.time() - t0
                    if not chunk:
                        print(f"  [{elapsed:.1f}s] Conexión cerrada por Revit sin datos")
                        break
                    raw += chunk
                    print(f"  [{elapsed:.1f}s] Chunk recibido ({len(chunk)} bytes): {chunk[:120]}")
                    if b"\n" in raw:
                        break
                except socket.timeout:
                    print(f"  [{time.time()-t0:.1f}s] TIMEOUT — Revit no respondió en {wait_secs}s")
                    print()
                    print("  DIAGNÓSTICO:")
                    print("  - ExternalEvent nunca disparó en 30+ segundos")
                    print("  - ¿Tienes un PROYECTO ABIERTO en Revit (no solo Revit en blanco)?")
                    print("  - Si yes: el ExternalEvent puede estar bloqueado por algún diálogo modal")
                    return False

            if raw:
                text = raw.decode("utf-8", errors="replace").strip()
                elapsed = time.time() - t0
                print(f"\n  [{elapsed:.1f}s] Respuesta completa recibida:")
                print(f"  {text}")
                try:
                    data = json.loads(text)
                    print(f"\n  ok = {data.get('ok')}")
                    if data.get("ok"):
                        result = json.loads(data.get("result", "{}"))
                        print(f"  Resultado: {result}")
                        print("\n  ✓ TODO FUNCIONA CORRECTAMENTE")
                    else:
                        print(f"  Error de Revit: {data.get('error')}")
                except json.JSONDecodeError:
                    print(f"  Respuesta no es JSON válido")
                return True
            else:
                print("  Sin datos en la respuesta")
                return False
    except Exception as e:
        print(f"  Error: {type(e).__name__}: {e}")
        return False

print("=" * 55)
print("  RevitMCP — Diagnóstico de conexión")
print("=" * 55)
print()

print("1) Verificando puerto 5001...")
port_ok = check_port()
print()

if port_ok:
    print("2) Verificando conexión TCP...")
    conn_ok = check_connect()
    print()

    if conn_ok:
        print("3) Verificando respuesta de Revit (ping)...")
        check_response()
else:
    print("   Saltando pasos 2 y 3 — plugin no activo")

print()
print("=" * 55)
input("Presiona Enter para cerrar...")
