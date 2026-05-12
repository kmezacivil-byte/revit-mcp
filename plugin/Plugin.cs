using Autodesk.Revit.UI;

namespace RevitMcpPlugin
{
    public class Plugin : IExternalApplication
    {
        public static Plugin Instance { get; private set; }
        private CommandListener _listener;

        public Result OnStartup(UIControlledApplication application)
        {
            Instance = this;

            RevitCommandHandler.Initialize();
            _listener = new CommandListener();
            _listener.Start();

            TaskDialog.Show("RevitMCP", "[RevitMCP] Plugin cargado. Escuchando en localhost:5001");
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            _listener?.Stop();
            return Result.Succeeded;
        }
    }
}
