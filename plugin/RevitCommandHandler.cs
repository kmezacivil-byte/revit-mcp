using Autodesk.Revit.UI;
using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;

namespace RevitMcpPlugin
{
    /// <summary>
    /// Puente entre el hilo HTTP (background) y el hilo principal de Revit.
    /// Se ejecuta en el contexto de la API de Revit mediante ExternalEvent.
    /// </summary>
    public class RevitCommandHandler : IExternalEventHandler
    {
        private static RevitCommandHandler _instance;
        private static ExternalEvent _externalEvent;

        // Estado compartido con RevitDispatcher (que serializa las llamadas con un lock)
        public string PendingCommand { get; set; }
        public JObject PendingArgs { get; set; }
        public string LastResult { get; private set; }
        public ManualResetEventSlim ExecutionDone { get; } = new ManualResetEventSlim(false);

        public static RevitCommandHandler Instance => _instance;
        public static ExternalEvent Event => _externalEvent;

        public static void Initialize()
        {
            _instance = new RevitCommandHandler();
            _externalEvent = ExternalEvent.Create(_instance);
        }

        private RevitCommandHandler() { }

        public void Execute(UIApplication app)
        {
            try
            {
                var tool = ToolRegistry.Find(PendingCommand);
                if (tool == null)
                {
                    LastResult = JsonConvert.SerializeObject(new
                    {
                        ok = false,
                        error = $"Herramienta desconocida: '{PendingCommand}'"
                    });
                    return;
                }

                var result = tool.Run(new ToolArgs(PendingArgs), new ToolContext(app));
                LastResult = JsonConvert.SerializeObject(new { ok = true, result });
            }
            catch (ToolException ex)
            {
                LastResult = JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
            }
            catch (Exception ex)
            {
                LastResult = JsonConvert.SerializeObject(new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}" });
            }
            finally
            {
                ExecutionDone.Set();
            }
        }

        public string GetName() => "RevitMcpHandler";
    }
}
