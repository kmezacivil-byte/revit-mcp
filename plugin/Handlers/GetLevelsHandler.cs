#nullable disable
using Autodesk.Revit.DB;
using System;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    public class GetLevelsTool : McpTool
    {
        public override string Name => "get_levels";
        public override string Description =>
            "Lista los niveles del proyecto con su elevación en metros y pies, de menor a mayor.";

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => new
                {
                    id = l.Id.GetId(),
                    nombre = l.Name,
                    elevacion_m = Math.Round(l.Elevation * Units.FeetToMeters, 4),
                    elevacion_ft = Math.Round(l.Elevation, 4)
                })
                .ToList();

            return new { total = levels.Count, niveles = levels };
        }
    }
}
