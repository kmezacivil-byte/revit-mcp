using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RevitMcpPlugin.Handlers
{
    public class PingHandler : ICommandHandler
    {
        public string Execute(Dictionary<string, string> args, Document doc, UIApplication uiApp)
        {
            var project = doc?.Title ?? "Sin documento abierto";
            var version = uiApp?.Application?.VersionName ?? "Revit";
            return JsonConvert.SerializeObject(new { status = "ok", revit = version, proyecto = project });
        }
    }
}
