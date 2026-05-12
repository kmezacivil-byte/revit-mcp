using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;

namespace RevitMcpPlugin
{
    public class CommandListener
    {
        private const int Port = 5001;
        private TcpListener _listener;
        private volatile bool _running;
        private readonly object _dispatchLock = new object();

        // ── Logger a archivo ────────────────────────────────────────────────────
        private static readonly string LogFile = @"C:\Temp\RevitMCP_debug.log";

        private static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(@"C:\Temp");
                File.AppendAllText(LogFile,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
            }
            catch { /* nunca lanzar desde el logger */ }
        }

        // ── Inicio ──────────────────────────────────────────────────────────────
        public void Start()
        {
            _running = true;
            try { File.Delete(LogFile); } catch { }

            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();

            var t = new Thread(AcceptLoop) { IsBackground = true, Name = "RevitMCP-Accept" };
            t.Start();

            Log("CommandListener iniciado en localhost:" + Port);
            Console.WriteLine($"[RevitMCP] Escuchando en localhost:{Port}");
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
        }

        // ── Accept loop ─────────────────────────────────────────────────────────
        private void AcceptLoop()
        {
            Log("AcceptLoop arrancó");
            while (_running)
            {
                try
                {
                    Log("Esperando conexión...");
                    var client = _listener.AcceptTcpClient();
                    Log($"Cliente aceptado: {client.Client.RemoteEndPoint}");

                    var t = new Thread(() => HandleClient(client))
                    { IsBackground = true, Name = "RevitMCP-Handler" };
                    t.Start();
                }
                catch (SocketException) { break; }
                catch (Exception ex) { Log($"AcceptLoop error: {ex}"); }
            }
            Log("AcceptLoop terminado");
        }

        // ── Manejo de cliente ───────────────────────────────────────────────────
        private void HandleClient(TcpClient client)
        {
            Log("HandleClient: inicio");
            string response = null;

            try
            {
                Log("HandleClient: obteniendo stream");
                var stream = client.GetStream();
                Log("HandleClient: stream OK");

                stream.ReadTimeout  = 15_000;
                stream.WriteTimeout = 15_000;
                Log("HandleClient: timeouts configurados");

                // ── Leer línea sin StreamReader (evita problemas de buffer en net48)
                Log("HandleClient: leyendo línea...");
                string json = ReadLineFromStream(stream);
                Log($"HandleClient: línea leída = [{json}]");

                if (string.IsNullOrWhiteSpace(json))
                {
                    Log("HandleClient: json vacío → respuesta de error");
                    response = Serialize(new { ok = false, error = "Comando vacío" });
                }
                else
                {
                    Log("HandleClient: parseando JSON");
                    var request = JObject.Parse(json);
                    var command = request["command"]?.Value<string>() ?? "";
                    var args    = new Dictionary<string, string>();

                    if (request["args"] is JObject argsObj)
                        foreach (var p in argsObj.Properties())
                            args[p.Name] = p.Value.ToString();

                    Log($"HandleClient: comando='{command}', args={args.Count}");
                    response = DispatchToRevit(command, args);
                    Log($"HandleClient: respuesta obtenida ({response?.Length} chars)");
                }
            }
            catch (Exception ex)
            {
                Log($"HandleClient: EXCEPCIÓN en lectura/parse: {ex.GetType().Name}: {ex.Message}");
                response = Serialize(new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}" });
            }

            // ── Escribir respuesta ─────────────────────────────────────────────
            try
            {
                Log("HandleClient: escribiendo respuesta...");
                var stream = client.GetStream();
                var bytes  = Encoding.UTF8.GetBytes((response ?? "") + "\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                Log("HandleClient: respuesta enviada OK");
            }
            catch (Exception ex)
            {
                Log($"HandleClient: EXCEPCIÓN al escribir: {ex.GetType().Name}: {ex.Message}");
            }

            try { client.Close(); } catch { }
            Log("HandleClient: fin");
        }

        // ── Leer hasta '\n' sin StreamReader ───────────────────────────────────
        private static string ReadLineFromStream(NetworkStream stream)
        {
            var buffer = new List<byte>(256);
            var oneByte = new byte[1];
            while (true)
            {
                int n = stream.Read(oneByte, 0, 1);
                if (n == 0) break;                      // EOF
                if (oneByte[0] == (byte)'\n') break;   // fin de línea
                if (oneByte[0] != (byte)'\r')           // ignorar CR
                    buffer.Add(oneByte[0]);
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        // ── Dispatch al hilo principal de Revit ────────────────────────────────
        private string DispatchToRevit(string command, Dictionary<string, string> args)
        {
            Log($"DispatchToRevit: entrando (comando='{command}')");
            lock (_dispatchLock)
            {
                Log("DispatchToRevit: lock adquirido");
                try
                {
                    var handler = RevitCommandHandler.Instance;
                    if (handler == null)
                    {
                        Log("DispatchToRevit: handler es null");
                        return Serialize(new { ok = false, error = "RevitCommandHandler no inicializado" });
                    }

                    var ev = RevitCommandHandler.Event;
                    if (ev == null)
                    {
                        Log("DispatchToRevit: ExternalEvent es null");
                        return Serialize(new { ok = false, error = "ExternalEvent no inicializado" });
                    }

                    handler.PendingCommand = command;
                    handler.PendingArgs    = args;
                    handler.ExecutionDone.Reset();

                    Log("DispatchToRevit: llamando Event.Raise()");
                    var status = ev.Raise();
                    Log($"DispatchToRevit: Event.Raise() → {status}");

                    if (status != Autodesk.Revit.UI.ExternalEventRequest.Accepted)
                        return Serialize(new { ok = false, error = $"ExternalEvent rechazado: {status}. Abre un proyecto en Revit." });

                    Log("DispatchToRevit: esperando ExecutionDone (60s)...");
                    bool completed = handler.ExecutionDone.Wait(TimeSpan.FromSeconds(60));
                    Log($"DispatchToRevit: Wait completado = {completed}");

                    if (!completed)
                        return Serialize(new { ok = false, error = "Timeout 60s: el ExternalEvent no se ejecutó. Verifica que Revit tenga un proyecto abierto." });

                    Log($"DispatchToRevit: resultado = {handler.LastResult}");
                    return handler.LastResult;
                }
                catch (Exception ex)
                {
                    Log($"DispatchToRevit: EXCEPCIÓN: {ex.GetType().Name}: {ex.Message}");
                    return Serialize(new { ok = false, error = $"DispatchError: {ex.GetType().Name}: {ex.Message}" });
                }
            }
        }

        private static string Serialize(object obj) =>
            JsonConvert.SerializeObject(obj);
    }
}
