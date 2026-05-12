using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RevitMcpPlugin.Handlers
{
    public class ProjectInfoHandler : ICommandHandler
    {
        public string Execute(Dictionary<string, string> args, Document doc, UIApplication uiApp)
        {
            if (doc == null)
                return JsonConvert.SerializeObject(new { error = "No hay documento activo en Revit" });

            var info = doc.ProjectInformation;

            // Nota: GetElementCount() se omite porque puede ser muy lento en modelos grandes
            // y provocar un timeout del ExternalEvent. Si se necesita, usar get_elements_by_category.

            return JsonConvert.SerializeObject(new
            {
                titulo = doc.Title,
                ruta = doc.PathName,
                version = uiApp.Application.VersionName,
                nombre_proyecto = info?.Name ?? "",
                numero_proyecto = info?.Number ?? "",
                autor = info?.Author ?? "",
                organizacion = info?.OrganizationName ?? ""
            });
        }
    }
}
