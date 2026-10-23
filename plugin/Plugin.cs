#nullable disable
using Autodesk.Revit.UI;

namespace RevitMcpPlugin
{
    public class Plugin : IExternalApplication
    {
        public static Plugin Instance { get; private set; }
        private McpHttpServer _listener;

        public Result OnStartup(UIControlledApplication application)
        {
            Instance = this;

            RevitCommandHandler.Initialize();
            try { Ui.RibbonSetup.Create(application); }
            catch (System.Exception ex) { TaskDialog.Show("RevitMCP", "No se pudo crear la pestaña RevitMCP: " + ex.Message); }
            _listener = new McpHttpServer();
            try
            {
                _listener.Start();
            }
            catch (System.Exception ex)
            {
                TaskDialog.Show("RevitMCP", $"No se pudo iniciar el servidor MCP en el puerto {McpHttpServer.Port}:\n{ex.Message}");
                return Result.Failed;
            }

            TaskDialog.Show("RevitMCP", $"Plugin cargado. Servidor MCP en http://127.0.0.1:{McpHttpServer.Port}/mcp\nToken: {McpHttpServer.TokenFile}");

            // Sin sesión DJP se ofrece iniciar sesión una vez, cuando Revit ya está listo (en OnStartup aún no hay ventana principal).
            if (McpConfig.AuthRequired) application.Idling += PromptLoginOnce;
            return Result.Succeeded;
        }

        private void PromptLoginOnce(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            var uiApp = sender as UIApplication;
            if (uiApp == null) return;
            uiApp.Idling -= PromptLoginOnce;
            try
            {
                if (Auth.AuthManager.IsAuthenticated()) return;
                var window = new Ui.AccountWindow();
                new System.Windows.Interop.WindowInteropHelper(window).Owner = uiApp.MainWindowHandle;
                window.ShowDialog();
            }
            catch { /* el aviso de inicio de sesión nunca debe romper Revit */ }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            _listener?.Stop();
            Families.FamilySessions.CloseAll();
            TunnelManager.Stop();
            return Result.Succeeded;
        }
    }
}
