#nullable disable
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RevitMcpPlugin.Families
{
    internal static class FamilyHelpers
    {
        /// <summary>Quita tildes y pasa a minúsculas, para comparar nombres de plantillas en cualquier idioma.</summary>
        public static string Normalize(string s)
        {
            var d = (s ?? "").Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var c in d)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString().ToLowerInvariant();
        }

        // Alias de categoría → categoría de Revit y pistas para elegir plantilla (normalizadas, sin tildes).
        public sealed class CategoryInfo
        {
            public BuiltInCategory Category;
            public string[] TemplateHints;
        }

        public static readonly Dictionary<string, CategoryInfo> CategoryAliases =
            new Dictionary<string, CategoryInfo>(StringComparer.OrdinalIgnoreCase)
        {
            { "modelo_generico", new CategoryInfo { Category = BuiltInCategory.OST_GenericModel,
                TemplateHints = new[] { "modelo generico metrico", "metric generic model" } } },
            { "armazon_estructural", new CategoryInfo { Category = BuiltInCategory.OST_StructuralFraming,
                TemplateHints = new[] { "armazon estructural metrico", "metric structural framing - beams and braces", "metric structural framing" } } },
            { "pilar_estructural", new CategoryInfo { Category = BuiltInCategory.OST_StructuralColumns,
                TemplateHints = new[] { "pilar estructural metrico", "metric structural column" } } },
            { "equipo_mecanico", new CategoryInfo { Category = BuiltInCategory.OST_MechanicalEquipment,
                TemplateHints = new[] { "equipo mecanico metrico", "metric mechanical equipment" } } },
            { "mobiliario", new CategoryInfo { Category = BuiltInCategory.OST_Furniture,
                TemplateHints = new[] { "mobiliario metrico", "metric furniture" } } },
            { "equipo_especializado", new CategoryInfo { Category = BuiltInCategory.OST_SpecialityEquipment,
                TemplateHints = new[] { "equipo especializado metrico", "metric specialty equipment" } } },
        };

        public static List<string> Templates(ToolContext ctx)
        {
            var root = ctx.App.Application.FamilyTemplatePath;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return new List<string>();
            return Directory.EnumerateFiles(root, "*.rft", SearchOption.AllDirectories).OrderBy(f => f).ToList();
        }

        /// <summary>Plantilla por ruta absoluta o por texto del nombre; sin indicar, la habitual de la categoría.</summary>
        public static string ResolveTemplate(ToolContext ctx, string plantilla, CategoryInfo info)
        {
            if (plantilla != "" && Path.IsPathRooted(plantilla))
            {
                if (!File.Exists(plantilla)) throw new ToolException($"No existe la plantilla: {plantilla}");
                if (!plantilla.EndsWith(".rft", StringComparison.OrdinalIgnoreCase)) throw new ToolException("La plantilla debe ser un archivo .rft");
                return plantilla;
            }

            var all = Templates(ctx);
            if (all.Count == 0)
                throw new ToolException("No se encontró la carpeta de plantillas de familia de Revit. Indica 'plantilla_rft' con una ruta absoluta a un .rft");

            var hints = plantilla != "" ? new[] { Normalize(plantilla) } : info?.TemplateHints ?? new[] { "modelo generico metrico", "metric generic model" };
            foreach (var hint in hints)
            {
                var match = all.Where(f => Normalize(Path.GetFileNameWithoutExtension(f)).Contains(hint))
                    .OrderBy(f => Path.GetFileNameWithoutExtension(f).Length).FirstOrDefault();
                if (match != null) return match;
            }

            throw new ToolException($"No se encontró una plantilla para '{(plantilla != "" ? plantilla : string.Join(" / ", hints))}'. " +
                $"Usa list_family_templates para ver las disponibles ({all.Count}) o indica la ruta absoluta en 'plantilla_rft'");
        }

        public static string DefaultFamilyPath(string name) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitMCP", "families",
                ExportHelpersSafe(name) + ".rfa");

        private static string ExportHelpersSafe(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }

    public class ListFamilyTemplatesTool : McpTool
    {
        public override string Name => "list_family_templates";
        public override string Description =>
            "Lista las plantillas de familia (.rft) instaladas con Revit, para usarlas en create_family. " +
            "Los nombres dependen del idioma de tu Revit.";
        public override string Example => "{\"buscar\":\"generico\"}";

        public override JObject InputSchema => Schema.Object(
            ("buscar", "string", "Texto que debe contener el nombre (sin distinguir tildes)"),
            ("limite", "integer", "Máximo de plantillas (100 por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var buscar = FamilyHelpers.Normalize(args.Str("buscar"));
            var all = FamilyHelpers.Templates(ctx)
                .Where(f => buscar == "" || FamilyHelpers.Normalize(Path.GetFileNameWithoutExtension(f)).Contains(buscar)).ToList();
            return new
            {
                carpeta = ctx.App.Application.FamilyTemplatePath,
                total = all.Count,
                plantillas = all.Take(args.Limit()).Select(f => new { nombre = Path.GetFileNameWithoutExtension(f), ruta = f })
            };
        }
    }

    public class CreateFamilyTool : McpTool
    {
        public override string Name => "create_family";
        public override string Description =>
            "Crea un documento de familia nuevo desde una plantilla .rft y lo deja abierto en segundo plano. " +
            "Devuelve un familia_doc_id que usan las demás tools de familia. Categorías: modelo_generico (por defecto), " +
            "armazon_estructural, pilar_estructural, equipo_mecanico, mobiliario, equipo_especializado, o el nombre exacto " +
            "de una categoría de Revit. La familia no se guarda hasta que uses save_family.";
        public override bool ReadOnly => false;
        public override string Example => "{\"nombre\":\"Tubo hueco\",\"categoria\":\"modelo_generico\"}";

        public override JObject InputSchema => Schema.Object(new[] { "nombre" },
            ("nombre", "string", "Nombre de la familia (será el nombre del archivo .rfa)"),
            ("categoria", "string", "Alias o nombre de categoría (modelo_generico por defecto)"),
            ("plantilla_rft", "string", "Ruta absoluta de un .rft, o texto del nombre de la plantilla; vacío = la habitual de la categoría"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var nombre = args.RequireStr("nombre");
            var categoria = args.Str("categoria", "modelo_generico");
            FamilyHelpers.CategoryAliases.TryGetValue(categoria, out var info);

            var template = FamilyHelpers.ResolveTemplate(ctx, args.Str("plantilla_rft"), info);

            Document famDoc;
            try { famDoc = ctx.App.Application.NewFamilyDocument(template); }
            catch (Exception ex) { throw new ToolException($"Revit no pudo crear la familia desde '{template}': {ex.Message}"); }
            if (famDoc == null) throw new ToolException($"Revit no pudo crear la familia desde '{template}'");

            var warnings = new List<string>();
            string categoryName = "";
            try
            {
                categoryName = famDoc.OwnerFamily?.FamilyCategory?.Name ?? "";
                var target = ResolveCategory(famDoc, categoria, info);
                if (target != null && target.Id != famDoc.OwnerFamily.FamilyCategory?.Id)
                {
                    categoryName = ToolHelpers.InTransaction(famDoc, "Asignar categoría", () =>
                    {
                        famDoc.OwnerFamily.FamilyCategory = target;
                        return target.Name;
                    });
                }
                else if (target == null && info == null && categoria != "modelo_generico")
                    warnings.Add($"No se reconoció la categoría '{categoria}'; la familia conserva la de la plantilla ({categoryName})");
            }
            catch (Exception ex)
            {
                warnings.Add($"No se pudo cambiar la categoría a '{categoria}' (la plantilla puede no permitirlo): {ex.Message}");
            }

            try { ToolHelpers.InTransaction(famDoc, "Crear tipo inicial", () => { FamilyOps.EnsureDefaultType(famDoc, nombre); return 0; }); }
            catch (Exception ex) { warnings.Add($"No se pudo crear el tipo inicial: {ex.Message}"); }

            var session = FamilySessions.Add(famDoc, nombre, template);
            return new
            {
                familia_doc_id = session.Id,
                nombre,
                categoria = categoryName,
                plantilla = template,
                advertencias = warnings
            };
        }

        private static Category ResolveCategory(Document famDoc, string categoria, FamilyHelpers.CategoryInfo info)
        {
            if (info != null) return famDoc.Settings.Categories.get_Item(info.Category);
            foreach (Category c in famDoc.Settings.Categories)
                if (string.Equals(c.Name, categoria.Trim(), StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }
    }

    public class SaveFamilyTool : McpTool
    {
        public override string Name => "save_family";
        public override string Description =>
            "Guarda una familia abierta como archivo .rfa. Sin ruta guarda en Documentos\\RevitMCP\\families\\<nombre>.rfa " +
            "(o, si ya tiene archivo, en el mismo). No sobrescribe un archivo existente salvo sobrescribir=true.";
        public override bool ReadOnly => false;
        public override string Example => "{\"familia_doc_id\":1,\"ruta\":\"C:\\\\Familias\\\\Tubo_hueco.rfa\"}";

        public override JObject InputSchema => Schema.Object(new[] { "familia_doc_id" },
            ("familia_doc_id", "integer", "Id devuelto por create_family"),
            ("ruta", "string", "Ruta absoluta del .rfa de destino (opcional)"),
            ("sobrescribir", "boolean", "Permitir reemplazar un archivo existente (false por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var s = FamilySessions.Require(args);
            var ruta = args.Str("ruta");
            if (ruta == "") ruta = s.Path ?? FamilyHelpers.DefaultFamilyPath(s.Name);
            if (!Path.IsPathRooted(ruta)) throw new ToolException("'ruta' debe ser una ruta absoluta");
            if (!ruta.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)) throw new ToolException("El archivo debe terminar en .rfa");

            bool overwrite = args.Bool("sobrescribir", false);
            bool sameFile = string.Equals(ruta, s.Path, StringComparison.OrdinalIgnoreCase);
            if (File.Exists(ruta) && !overwrite && !sameFile)
                throw new ToolException($"Ya existe {ruta}. Repite con sobrescribir=true si quieres reemplazarlo");

            Directory.CreateDirectory(Path.GetDirectoryName(ruta));
            try
            {
                if (sameFile && File.Exists(ruta)) s.Doc.Save();
                else s.Doc.SaveAs(ruta, new SaveAsOptions { OverwriteExistingFile = overwrite || sameFile });
            }
            catch (Exception ex)
            {
                throw new ToolException($"Revit no pudo guardar la familia en '{ruta}': {ex.Message}. " +
                    "Comprueba que la carpeta exista y no tenga el archivo abierto en otro programa");
            }

            s.Path = ruta;
            return new { familia_doc_id = s.Id, archivo = ruta, tamano_kb = (new FileInfo(ruta).Length + 1023) / 1024 };
        }
    }

    public class CloseFamilyTool : McpTool
    {
        public override string Name => "close_family";
        public override string Description =>
            "Cierra una familia abierta. Con guardar=true la guarda antes (debe tener archivo, ver save_family). " +
            "Cerrar sin guardar descarta los cambios: si hay cambios sin guardar exige confirmar=true.";
        public override bool ReadOnly => false;
        public override bool Destructive => true;
        public override string Example => "{\"familia_doc_id\":1,\"guardar\":true}";

        public override JObject InputSchema => Schema.Object(new[] { "familia_doc_id" },
            ("familia_doc_id", "integer", "Id devuelto por create_family"),
            ("guardar", "boolean", "Guardar antes de cerrar (false por defecto)"),
            ("confirmar", "boolean", "true para descartar cambios sin guardar"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var s = FamilySessions.Require(args);
            bool save = args.Bool("guardar", false);

            if (save && string.IsNullOrEmpty(s.Path))
                throw new ToolException("La familia aún no tiene archivo: usa save_family primero, o cierra sin guardar");
            if (!save && s.Doc.IsModified)
                args.RequireConfirmation($"La familia '{s.Name}' tiene cambios sin guardar y se perderían");

            try { s.Doc.Close(save); }
            catch (Exception ex) { throw new ToolException($"Revit no pudo cerrar la familia: {ex.Message}"); }

            FamilySessions.Remove(s.Id);
            return new { cerrada = true, guardada = save, archivo = s.Path };
        }
    }

    public class ListOpenFamiliesTool : McpTool
    {
        public override string Name => "list_open_families";
        public override string Description =>
            "Familias abiertas por el plugin (con su familia_doc_id), indicando si tienen cambios sin guardar. " +
            "También informa de otras familias que el usuario tenga abiertas en Revit, que no se pueden manipular desde aquí.";
        public override string Example => "{}";

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var mine = FamilySessions.All().ToList();
            var mineDocs = new HashSet<Document>(mine.Select(m => m.Doc));
            var others = new List<string>();
            foreach (Document d in ctx.App.Application.Documents)
                if (d.IsFamilyDocument && !mineDocs.Contains(d)) others.Add(d.Title);

            return new
            {
                total = mine.Count,
                familias = mine.Select(m => new
                {
                    familia_doc_id = m.Id,
                    nombre = m.Name,
                    categoria = m.Doc.OwnerFamily?.FamilyCategory?.Name ?? "",
                    archivo = m.Path,
                    cambios_sin_guardar = m.Doc.IsModified
                }),
                otras_abiertas_por_el_usuario = others
            };
        }
    }
}
