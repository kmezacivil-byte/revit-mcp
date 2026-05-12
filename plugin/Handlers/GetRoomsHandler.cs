using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    public class GetRoomsHandler : ICommandHandler
    {
        public string Execute(Dictionary<string, string> args, Document doc, UIApplication uiApp)
        {
            if (doc == null)
                return JsonConvert.SerializeObject(new { error = "No hay documento activo en Revit" });

            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .Cast<Room>()
                .Where(r => r.Area > 0)
                .OrderBy(r => r.Number)
                .Select(r => new
                {
                    id = r.Id.GetId(),
                    numero = r.Number,
                    nombre = r.Name,
                    area_m2 = System.Math.Round(r.Area * 0.092903, 3),
                    nivel = r.Level?.Name ?? "",
                    perimetro_m = System.Math.Round(r.Perimeter * 0.3048, 3)
                })
                .ToList();

            return JsonConvert.SerializeObject(new { total = rooms.Count, ambientes = rooms });
        }
    }
}
