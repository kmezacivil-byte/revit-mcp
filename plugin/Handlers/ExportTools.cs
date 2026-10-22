#nullable disable
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    internal static class ExportHelpers
    {
        /// <summary>Carpeta de salida: la indicada (ruta absoluta) o Documentos\RevitMCP\exports.</summary>
        public static string OutputDir(ToolArgs args)
        {
            var dir = args.Str("carpeta");
            if (dir == "")
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitMCP", "exports");
            else if (!Path.IsPathRooted(dir))
                throw new ToolException("'carpeta' debe ser una ruta absoluta, p.ej. C:\\Proyectos\\Salida");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string RequireFile(ToolArgs args, string key, string extension)
        {
            var path = args.RequireStr(key);
            if (!Path.IsPathRooted(path)) throw new ToolException($"'{key}' debe ser una ruta absoluta");
            if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new ToolException($"El archivo debe ser {extension}");
            if (!File.Exists(path)) throw new ToolException($"No existe el archivo: {path}");
            return path;
        }

        public static string SafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }

    public class ExportPdfTool : McpTool
    {
        public override string Name => "export_pdf";
        public override bool ReadOnly => false;   // escribe un archivo en disco
        public override string Description =>
            "Exporta vistas y/o láminas a un PDF (combinado). Requiere Revit 2022 o superior. " +
            "Sin carpeta guarda en Documentos\\RevitMCP\\exports.";

        public override JObject InputSchema => Schema.Object(new[] { "ids" },
            ("ids", "integer[]", "Ids de vistas o láminas a exportar"),
            ("nombre", "string", "Nombre del archivo sin extensión (\"export\" por defecto)"),
            ("carpeta", "string", "Carpeta de salida (ruta absoluta)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
#if REVIT2021
            throw new ToolException("La exportación a PDF de Revit requiere la versión 2022 o superior");
#else
            var doc = ctx.RequireDoc();
            var ids = ModifyHelpers.RequireElements(doc, args);
            foreach (var id in ids)
                if (!(doc.GetElement(id) is View))
                    throw new ToolException($"El elemento {id.GetId()} no es una vista ni una lámina");

            var dir = ExportHelpers.OutputDir(args);
            var name = ExportHelpers.SafeFileName(args.Str("nombre", "export"));
            var options = new PDFExportOptions { FileName = name, Combine = true };
            if (!doc.Export(dir, ids, options)) throw new ToolException("Revit no pudo exportar el PDF");
            return new { archivo = Path.Combine(dir, name + ".pdf"), vistas = ids.Count };
#endif
        }
    }

    public class ExportImageTool : McpTool
    {
        public override string Name => "export_image";
        public override bool ReadOnly => false;   // escribe un archivo en disco
        public override string Description =>
            "Exporta una vista a imagen PNG. Sin view_id usa la vista activa. Sin carpeta guarda en Documentos\\RevitMCP\\exports.";

        public override JObject InputSchema => Schema.Object(
            ("view_id", "integer", "Id de la vista; vacío = vista activa"),
            ("pixeles", "integer", "Tamaño en píxeles del lado mayor (1920 por defecto)"),
            ("carpeta", "string", "Carpeta de salida (ruta absoluta)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var view = ViewHelpers.Resolve(args, doc);
            var dir = ExportHelpers.OutputDir(args);
            var prefix = "mcp_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

            var options = new ImageExportOptions
            {
                FilePath = Path.Combine(dir, prefix),
                FitDirection = FitDirectionType.Horizontal,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ExportRange = ExportRange.SetOfViews,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = (int)Math.Max(256, Math.Min(args.Long("pixeles", 1920), 8000))
            };
            options.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(options);

            var files = Directory.GetFiles(dir, prefix + "*");
            if (files.Length == 0) throw new ToolException("Revit no generó la imagen (¿la vista admite exportación?)");
            return new { archivos = files, vista = view.Name };
        }
    }

    public class ExportIfcTool : McpTool
    {
        public override string Name => "export_ifc";
        public override bool ReadOnly => false;   // escribe un archivo en disco
        public override string Description =>
            "Exporta el modelo a IFC con la configuración por defecto de Revit. Puede tardar en modelos grandes. " +
            "Sin carpeta guarda en Documentos\\RevitMCP\\exports.";

        public override JObject InputSchema => Schema.Object(
            ("nombre", "string", "Nombre del archivo sin extensión (el título del proyecto por defecto)"),
            ("carpeta", "string", "Carpeta de salida (ruta absoluta)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var dir = ExportHelpers.OutputDir(args);
            var name = ExportHelpers.SafeFileName(args.Str("nombre", doc.Title));
            if (!doc.Export(dir, name, new IFCExportOptions())) throw new ToolException("Revit no pudo exportar el IFC");
            return new { archivo = Path.Combine(dir, name + ".ifc") };
        }
    }

    public class LinkRevitFileTool : McpTool
    {
        public override string Name => "link_revit_file";
        public override string Description =>
            "Vincula otro modelo .rvt al proyecto actual (posición: origen a origen). Se puede deshacer con Ctrl+Z.";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(new[] { "ruta" },
            ("ruta", "string", "Ruta absoluta del archivo .rvt"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var path = ExportHelpers.RequireFile(args, "ruta", ".rvt");
            var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);

            return ToolHelpers.InTransaction(doc, "Vincular modelo", () =>
            {
                var result = RevitLinkType.Create(doc, modelPath, new RevitLinkOptions(false));
                var instance = RevitLinkInstance.Create(doc, result.ElementId);
                return new { id = instance.Id.GetId(), nombre = ToolHelpers.SafeName(instance), archivo = path };
            });
        }
    }

    public class LoadFamilyTool : McpTool
    {
        public override string Name => "load_family";
        public override string Description =>
            "Carga un archivo de familia (.rfa) en el proyecto. Si ya estaba cargada, lo indica y no la sobrescribe.";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(new[] { "ruta" },
            ("ruta", "string", "Ruta absoluta del archivo .rfa"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var path = ExportHelpers.RequireFile(args, "ruta", ".rfa");

            return ToolHelpers.InTransaction(doc, "Cargar familia", () =>
            {
                Family family;
                bool loaded = doc.LoadFamily(path, out family);
                return new
                {
                    cargada = loaded,
                    familia = family == null ? Path.GetFileNameWithoutExtension(path) : family.Name,
                    aviso = loaded ? null : "La familia ya existe en el proyecto; no se sobrescribió"
                };
            });
        }
    }

    public class SaveDocumentTool : McpTool
    {
        public override string Name => "save_document";
        public override string Description =>
            "Guarda el proyecto activo en su archivo actual. Sobrescribe el archivo, por eso exige confirmar=true.";
        public override bool ReadOnly => false;
        public override bool Destructive => true;

        public override JObject InputSchema => Schema.Object(
            ("confirmar", "boolean", "true para guardar de verdad"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            if (string.IsNullOrEmpty(doc.PathName))
                throw new ToolException("El proyecto aún no se ha guardado: usa Guardar como en Revit la primera vez");
            args.RequireConfirmation($"Se guardará el proyecto '{doc.Title}' sobrescribiendo {doc.PathName}");
            doc.Save();
            return new { guardado = true, archivo = doc.PathName };
        }
    }
}
