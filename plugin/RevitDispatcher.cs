using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMcpPlugin
{
    /// <summary>
    /// Pasa un comando desde cualquier hilo al hilo principal de Revit (ExternalEvent)
    /// y espera el resultado. Serializa las llamadas: Revit ejecuta una a la vez.
    /// </summary>
    public static class RevitDispatcher
    {
        private static readonly object Gate = new object();

        public static string Dispatch(string command, JObject args)
        {
            lock (Gate)
            {
                try
                {
                    var handler = RevitCommandHandler.Instance;
                    var ev = RevitCommandHandler.Event;
                    if (handler == null || ev == null)
                        return Error("El plugin de Revit no está inicializado");

                    handler.PendingCommand = command;
                    handler.PendingArgs = args;
                    handler.ExecutionDone.Reset();

                    var status = ev.Raise();
                    if (status != Autodesk.Revit.UI.ExternalEventRequest.Accepted)
                        return Error($"ExternalEvent rechazado: {status}. Abre un proyecto en Revit.");

                    if (!handler.ExecutionDone.Wait(TimeSpan.FromSeconds(60)))
                        return Error("Timeout 60s: Revit no ejecutó el comando. Verifica que haya un proyecto abierto.");

                    return handler.LastResult;
                }
                catch (Exception ex)
                {
                    return Error($"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static string Error(string msg) =>
            JsonConvert.SerializeObject(new { ok = false, error = msg });
    }
}
