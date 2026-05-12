using Autodesk.Revit.UI;
using RevitMcpPlugin.Handlers;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Threading;

namespace RevitMcpPlugin
{
    /// <summary>
    /// Puente entre el hilo TCP (background) y el hilo principal de Revit.
    /// Se ejecuta en el contexto de la API de Revit mediante ExternalEvent.
    /// </summary>
    public class RevitCommandHandler : IExternalEventHandler
    {
        private static RevitCommandHandler _instance;
        private static ExternalEvent _externalEvent;

        private readonly Dictionary<string, ICommandHandler> _handlers;

        // Estado compartido con CommandListener (protegido por SemaphoreSlim en CommandListener)
        public string PendingCommand { get; set; }
        public Dictionary<string, string> PendingArgs { get; set; }
        public string LastResult { get; private set; }
        public ManualResetEventSlim ExecutionDone { get; } = new ManualResetEventSlim(false);

        public static RevitCommandHandler Instance => _instance;
        public static ExternalEvent Event => _externalEvent;

        public static void Initialize()
        {
            _instance = new RevitCommandHandler();
            _externalEvent = ExternalEvent.Create(_instance);
        }

        private RevitCommandHandler()
        {
            _handlers = new Dictionary<string, ICommandHandler>(StringComparer.OrdinalIgnoreCase)
            {
                { "ping",                    new PingHandler() },
                { "get_project_info",        new ProjectInfoHandler() },
                { "get_levels",              new GetLevelsHandler() },
                { "get_rooms",               new GetRoomsHandler() },
                { "get_elements_by_category",new GetElementsByCategoryHandler() },
                { "create_wall",             new CreateWallHandler() },
                { "get_sheets",              new GetSheetsHandler() },
            };
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;

                if (_handlers.TryGetValue(PendingCommand, out var handler))
                {
                    var raw = handler.Execute(PendingArgs, doc, app);
                    LastResult = JsonConvert.SerializeObject(new { ok = true, result = raw });
                }
                else
                {
                    var available = string.Join(", ", _handlers.Keys);
                    LastResult = JsonConvert.SerializeObject(new
                    {
                        ok = false,
                        error = $"Comando desconocido: '{PendingCommand}'. Disponibles: {available}"
                    });
                }
            }
            catch (Exception ex)
            {
                LastResult = JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
            }
            finally
            {
                ExecutionDone.Set();
            }
        }

        public string GetName() => "RevitMcpHandler";
    }
}
