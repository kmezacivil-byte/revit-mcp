#nullable disable
namespace RevitMcpPlugin.Handlers
{
    public class ProjectInfoTool : McpTool
    {
        public override string Name => "get_project_info";
        public override string Description =>
            "Información general del proyecto activo: título, ruta, versión, nombre, número, autor y organización. " +
            "Para contar elementos usa get_model_statistics.";

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var info = doc.ProjectInformation;
            return new
            {
                titulo = doc.Title,
                ruta = doc.PathName,
                version = ctx.App.Application.VersionName,
                nombre_proyecto = info?.Name ?? "",
                numero_proyecto = info?.Number ?? "",
                autor = info?.Author ?? "",
                organizacion = info?.OrganizationName ?? ""
            };
        }
    }
}
