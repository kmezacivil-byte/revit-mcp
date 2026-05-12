using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    public class GetLevelsHandler : ICommandHandler
    {
        public string Execute(Dictionary<string, string> args, Document doc, UIApplication uiApp)
        {
            if (doc == null)
                return JsonConvert.SerializeObject(new { error = "No hay documento activo en Revit" });

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => new
                {
                    id = l.Id.GetId(),
                    nombre = l.Name,
                    elevacion_m = System.Math.Round(l.Elevation * 0.3048, 4),
                    elevacion_ft = System.Math.Round(l.Elevation, 4)
                })
                .ToList();

            return JsonConvert.SerializeObject(new { total = levels.Count, niveles = levels });
        }
    }
}
