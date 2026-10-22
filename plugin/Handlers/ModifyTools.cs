#nullable disable
using Autodesk.Revit.DB;
using RevitMcpPlugin.Families;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    internal static class ModifyHelpers
    {
        public const int MaxIds = 500;

        public static List<ElementId> RequireElements(Document doc, ToolArgs args, string key = "ids")
        {
            var ids = args.RequireLongList(key);
            if (ids.Count > MaxIds) throw new ToolException($"Máximo {MaxIds} elementos por llamada");
            var missing = ids.Where(i => doc.GetElement(ToolHelpers.ToElementId(i)) == null).ToList();
            if (missing.Count > 0) throw new ToolException("No existen elementos con id: " + string.Join(", ", missing.Take(10)));
            return ids.Select(ToolHelpers.ToElementId).ToList();
        }

        /// <summary>
        /// Asigna un valor de texto a un parámetro. Longitudes: número = metros (o con unidad, "125 mm"); ángulos: número = grados.
        /// Otras magnitudes con unidades (área, volumen...) se interpretan en las unidades del proyecto.
        /// </summary>
        public static void ApplyParameter(Parameter p, string valor, string nombre)
        {
            bool ok;
            switch (p.StorageType)
            {
                case StorageType.String:
                    ok = p.Set(valor);
                    break;
                case StorageType.Integer:
                    if (int.TryParse(valor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) ok = p.Set(i);
                    else if (FamilyValues.KindOf(p.Definition) == ParamKind.YesNo)
                    {
                        var t = FamilyHelpers.Normalize(valor);
                        ok = p.Set(t == "true" || t == "si" || t == "yes" ? 1 : 0);
                    }
                    else ok = p.SetValueString(valor);
                    break;
                case StorageType.Double:
                    var kind = FamilyValues.KindOf(p.Definition);
                    if (kind == ParamKind.Length && FamilyValues.IsNumeric(valor)) ok = p.Set(FamilyValues.LengthFeet(valor));
                    else if (kind == ParamKind.Angle && FamilyValues.IsNumeric(valor)) ok = p.Set(FamilyValues.AngleRadians(valor));
                    else ok = p.SetValueString(valor) ||
                              (double.TryParse(valor, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) && p.Set(d));
                    break;
                case StorageType.ElementId:
                    if (!long.TryParse(valor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                        throw new ToolException($"El parámetro '{nombre}' espera el id de otro elemento");
                    ok = p.Set(ToolHelpers.ToElementId(id));
                    break;
                default:
                    throw new ToolException($"El tipo de dato del parámetro '{nombre}' no es modificable");
            }
            if (!ok) throw new ToolException($"Revit rechazó el valor '{valor}' para '{nombre}'");
        }

        public static Color ParseColor(string text)
        {
            text = text.Trim();
            try
            {
                if (text.StartsWith("#") && text.Length == 7)
                    return new Color(Convert.ToByte(text.Substring(1, 2), 16), Convert.ToByte(text.Substring(3, 2), 16), Convert.ToByte(text.Substring(5, 2), 16));
                var parts = text.Split(',');
                if (parts.Length == 3)
                    return new Color(byte.Parse(parts[0].Trim()), byte.Parse(parts[1].Trim()), byte.Parse(parts[2].Trim()));
            }
            catch { }
            throw new ToolException("'color' debe ser \"R,G,B\" (0-255) o \"#RRGGBB\"");
        }
    }

    public class SetParameterTool : McpTool
    {
        public override string Name => "set_parameter";
        public override string Description =>
            "Cambia el valor de un parámetro de un elemento. Longitudes: un número son metros, o añade la unidad " +
            "(\"125 mm\"); ángulos en grados. Con en_tipo=true modifica el parámetro del tipo, lo que afecta a todos " +
            "los elementos de ese tipo.";
        public override bool ReadOnly => false;
        public override bool Destructive => true;

        public override JObject InputSchema => Schema.Object(new[] { "id", "parametro", "valor" },
            ("id", "integer", "Id del elemento"),
            ("parametro", "string", "Nombre del parámetro (ver get_element_properties)"),
            ("valor", "string", "Nuevo valor"),
            ("en_tipo", "boolean", "Modificar el parámetro del tipo en vez de la instancia (false por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var e = ToolHelpers.RequireElement(doc, args.RequireLong("id"));
            var nombre = args.RequireStr("parametro");
            var valor = args.Str("valor", null);
            if (valor == null) throw new ToolException("Falta el parámetro obligatorio 'valor'");

            var target = args.Bool("en_tipo", false) ? doc.GetElement(e.GetTypeId()) : e;
            var p = target?.LookupParameter(nombre);
            if (p == null) throw new ToolException($"El elemento no tiene un parámetro '{nombre}'" +
                (args.Bool("en_tipo", false) ? " en su tipo" : " (prueba con en_tipo=true si es del tipo)"));
            if (p.IsReadOnly) throw new ToolException($"El parámetro '{nombre}' es de solo lectura");

            var before = ToolHelpers.ParamValue(doc, p);
            return ToolHelpers.InTransaction(doc, "Cambiar parámetro", () =>
            {
                ModifyHelpers.ApplyParameter(p, valor, nombre);
                return new { id = e.Id.GetId(), parametro = nombre, valor_anterior = before, valor_nuevo = ToolHelpers.ParamValue(doc, p) };
            });
        }
    }

    public class DeleteElementsTool : McpTool
    {
        public override string Name => "delete_elements";
        public override string Description =>
            "Borra elementos por id. IRREVERSIBLE salvo con Ctrl+Z en Revit. Revit puede borrar también elementos dependientes " +
            "(p.ej. puertas de un muro). Sin confirmar=true solo devuelve qué se borraría.";
        public override bool ReadOnly => false;
        public override bool Destructive => true;

        public override JObject InputSchema => Schema.Object(new[] { "ids" },
            ("ids", "integer[]", "Ids de los elementos a borrar (máximo 500)"),
            ("confirmar", "boolean", "true para borrar de verdad; sin esto solo se muestra una vista previa"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var ids = ModifyHelpers.RequireElements(doc, args);

            if (!args.Bool("confirmar", false))
            {
                var preview = ids.Take(5).Select(id =>
                {
                    var e = doc.GetElement(id);
                    return $"{e.Category?.Name} '{ToolHelpers.SafeName(e)}' (id {id.GetId()})";
                });
                args.RequireConfirmation($"Se borrarían {ids.Count} elementos: " +
                    string.Join("; ", preview) + (ids.Count > 5 ? "; ..." : ""));
            }

            return ToolHelpers.InTransaction(doc, "Borrar elementos", () =>
            {
                var deleted = doc.Delete(ids);
                return new { solicitados = ids.Count, borrados_en_total = deleted.Count };
            });
        }
    }

    public class MoveElementsTool : McpTool
    {
        public override string Name => "move_elements";
        public override string Description => "Desplaza elementos un vector (dx, dy, dz) en metros.";
        public override bool ReadOnly => false;
        public override bool Destructive => true;

        public override JObject InputSchema => Schema.Object(new[] { "ids" },
            ("ids", "integer[]", "Ids de los elementos (máximo 500)"),
            ("dx", "number", "Desplazamiento en X (m)"), ("dy", "number", "Desplazamiento en Y (m)"),
            ("dz", "number", "Desplazamiento en Z (m)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var ids = ModifyHelpers.RequireElements(doc, args);
            var v = new XYZ(args.Dbl("dx", 0), args.Dbl("dy", 0), args.Dbl("dz", 0)) / Units.FeetToMeters;
            if (v.GetLength() < 1e-9) throw new ToolException("Indica un desplazamiento distinto de cero (dx, dy o dz)");

            return ToolHelpers.InTransaction(doc, "Mover elementos", () =>
            {
                ElementTransformUtils.MoveElements(doc, ids, v);
                return new { movidos = ids.Count, dx_m = args.Dbl("dx", 0), dy_m = args.Dbl("dy", 0), dz_m = args.Dbl("dz", 0) };
            });
        }
    }

    public class RotateElementsTool : McpTool
    {
        public override string Name => "rotate_elements";
        public override string Description =>
            "Gira elementos alrededor de un eje vertical. Sin centro, cada elemento gira sobre su propio centro.";
        public override bool ReadOnly => false;
        public override bool Destructive => true;

        public override JObject InputSchema => Schema.Object(new[] { "ids", "angulo_grados" },
            ("ids", "integer[]", "Ids de los elementos (máximo 500)"),
            ("angulo_grados", "number", "Ángulo en grados, positivo = antihorario"),
            ("centro_x", "number", "X del centro de giro (m); vacío = centro de cada elemento"),
            ("centro_y", "number", "Y del centro de giro (m)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var ids = ModifyHelpers.RequireElements(doc, args);
            var angle = args.RequireDbl("angulo_grados") * Math.PI / 180.0;
            bool shared = args.Has("centro_x") && args.Has("centro_y");
            var pivot = shared ? new XYZ(args.Dbl("centro_x", 0) / Units.FeetToMeters, args.Dbl("centro_y", 0) / Units.FeetToMeters, 0) : null;

            return ToolHelpers.InTransaction(doc, "Girar elementos", () =>
            {
                if (shared)
                    ElementTransformUtils.RotateElements(doc, ids, Line.CreateBound(pivot, pivot + XYZ.BasisZ), angle);
                else
                    foreach (var id in ids)
                    {
                        var bb = doc.GetElement(id).get_BoundingBox(null);
                        if (bb == null) throw new ToolException($"El elemento {id.GetId()} no tiene geometría para calcular su centro");
                        var c = (bb.Min + bb.Max) / 2.0;
                        ElementTransformUtils.RotateElement(doc, id, Line.CreateBound(c, c + XYZ.BasisZ), angle);
                    }
                return new { girados = ids.Count, angulo_grados = args.RequireDbl("angulo_grados") };
            });
        }
    }

    public class ColorElementsTool : McpTool
    {
        public override string Name => "color_elements";
        public override string Description =>
            "Colorea elementos en una vista (sobrescritura gráfica de línea y superficie). No modifica el modelo, solo la vista. " +
            "Sin view_id usa la vista activa. Se quita con clear_color_overrides.";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(new[] { "ids", "color" },
            ("ids", "integer[]", "Ids de los elementos (máximo 500)"),
            ("color", "string", "\"R,G,B\" (0-255) o \"#RRGGBB\""),
            ("view_id", "integer", "Id de la vista; vacío = vista activa"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var ids = ModifyHelpers.RequireElements(doc, args);
            var color = ModifyHelpers.ParseColor(args.RequireStr("color"));
            var view = ViewHelpers.Resolve(args, doc);

            var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);

            return ToolHelpers.InTransaction(doc, "Colorear elementos", () =>
            {
                var ogs = new OverrideGraphicSettings();
                ogs.SetProjectionLineColor(color);
                if (solid != null)
                {
                    ogs.SetSurfaceForegroundPatternId(solid.Id);
                    ogs.SetSurfaceForegroundPatternColor(color);
                }
                foreach (var id in ids) view.SetElementOverrides(id, ogs);
                return new { coloreados = ids.Count, vista = view.Name };
            });
        }
    }

    public class ClearColorOverridesTool : McpTool
    {
        public override string Name => "clear_color_overrides";
        public override string Description =>
            "Quita las sobrescrituras gráficas de elementos en una vista. Sin ids limpia los elementos visibles de la vista.";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(
            ("ids", "integer[]", "Ids de los elementos; vacío = todos los visibles en la vista"),
            ("view_id", "integer", "Id de la vista; vacío = vista activa"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var view = ViewHelpers.Resolve(args, doc);
            var ids = args.Has("ids")
                ? ModifyHelpers.RequireElements(doc, args)
                : new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds().ToList();

            return ToolHelpers.InTransaction(doc, "Quitar colores", () =>
            {
                var empty = new OverrideGraphicSettings();
                foreach (var id in ids) view.SetElementOverrides(id, empty);
                return new { limpiados = ids.Count, vista = view.Name };
            });
        }
    }

    public class TagElementsTool : McpTool
    {
        public override string Name => "tag_elements";
        public override string Description =>
            "Etiqueta elementos en una vista con la etiqueta por defecto de su categoría (debe estar cargada en el proyecto). " +
            "Sin view_id usa la vista activa.";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(new[] { "ids" },
            ("ids", "integer[]", "Ids de los elementos (máximo 500)"),
            ("view_id", "integer", "Id de la vista; vacío = vista activa"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var ids = ModifyHelpers.RequireElements(doc, args);
            var view = ViewHelpers.Resolve(args, doc);

            return ToolHelpers.InTransaction(doc, "Etiquetar elementos", () =>
            {
                int tagged = 0;
                var errors = new List<string>();
                foreach (var id in ids)
                {
                    var e = doc.GetElement(id);
                    try
                    {
                        var bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
                        var point = bb != null ? (bb.Min + bb.Max) / 2.0 : XYZ.Zero;
                        IndependentTag.Create(doc, view.Id, new Reference(e), false, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, point);
                        tagged++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{id.GetId()}: {ex.Message}");
                    }
                }
                return new { etiquetados = tagged, errores = errors.Take(10) };
            });
        }
    }

    public class SetActiveViewTool : McpTool
    {
        public override string Name => "set_active_view";
        public override string Description => "Cambia la vista activa en Revit a la vista indicada.";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(new[] { "view_id" },
            ("view_id", "integer", "Id de la vista (ver get_views)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var view = ViewHelpers.Resolve(args, doc);
            if (view.ViewType == ViewType.Schedule || view.IsTemplate)
                throw new ToolException("Esa vista no se puede activar");
            ctx.App.ActiveUIDocument.RequestViewChange(view);
            return new { vista = view.Name, view_id = view.Id.GetId() };
        }
    }
}
