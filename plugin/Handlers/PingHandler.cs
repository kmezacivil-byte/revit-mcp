#nullable disable
namespace RevitMcpPlugin.Handlers
{
    public class PingTool : McpTool
    {
        public override string Name => "ping";
        public override string Description =>
            "Verifica que el plugin de Revit responde. Retorna la versión de Revit y el proyecto abierto.";

        public override object Run(ToolArgs args, ToolContext ctx) => new
        {
            status = "ok",
            revit = ctx.App.Application.VersionName ?? "Revit",
            proyecto = ctx.Doc?.Title ?? "Sin documento abierto"
        };
    }
}
