#nullable disable
using Autodesk.Revit.DB;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    public class GetSheetsTool : McpTool
    {
        public override string Name => "get_sheets";
        public override string Description =>
            "Lista las láminas con número, nombre, diseñador y cantidad de vistas colocadas.";

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
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

            return new { total = sheets.Count, laminas = sheets };
        }
    }
}
