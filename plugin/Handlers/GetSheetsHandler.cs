using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    public class GetSheetsHandler : ICommandHandler
    {
        public string Execute(Dictionary<string, string> args, Document doc, UIApplication uiApp)
        {
            if (doc == null)
                return JsonConvert.SerializeObject(new { error = "No hay documento activo en Revit" });

            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .OrderBy(s => s.SheetNumber)
                .Select(s => new
                {
                    id = s.Id.GetId(),
                    numero = s.SheetNumber,
                    nombre = s.Name,
                    designado_por = s.get_Parameter(BuiltInParameter.SHEET_DESIGNED_BY)?.AsString() ?? "",
                    vistas_count = s.GetAllPlacedViews().Count
                })
                .ToList();

            return JsonConvert.SerializeObject(new { total = sheets.Count, laminas = sheets });
        }
    }
}
