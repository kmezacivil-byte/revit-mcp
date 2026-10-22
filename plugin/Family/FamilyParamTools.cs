#nullable disable
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpPlugin.Families
{
    public class AddFamilyParameterTool : McpTool
    {
        public override string Name => "add_family_parameter";
        public override string Description =>
            "Agrega un parámetro a una familia abierta. tipo_dato: Longitud, Angulo, Numero, Entero, Texto, Material o SiNo. " +
            "grupo: Geometria, Datos_de_identidad, Materiales, Restricciones, General, Texto, Visibilidad o Datos. " +
            "es_instancia=false (por defecto) crea un parámetro de TIPO, el habitual para dimensiones. " +
            "valor opcional en el tipo activo: longitudes en metros (o con unidad, \"125 mm\") y ángulos en grados.";
        public override bool ReadOnly => false;
        public override string Example => "{\"familia_doc_id\":1,\"nombre\":\"Diametro_Ext\",\"tipo_dato\":\"Longitud\",\"grupo\":\"Geometria\",\"valor\":\"125 mm\"}";

        public override JObject InputSchema => Schema.Object(new[] { "familia_doc_id", "nombre", "tipo_dato" },
            ("familia_doc_id", "integer", "Id devuelto por create_family"),
            ("nombre", "string", "Nombre del parámetro"),
            ("tipo_dato", "string", "Longitud, Angulo, Numero, Entero, Texto, Material o SiNo"),
            ("grupo", "string", "Grupo de parámetros (Geometria por defecto)"),
            ("es_instancia", "boolean", "true = parámetro de instancia; false = de tipo (por defecto)"),
            ("valor", "string", "Valor inicial en el tipo activo (opcional)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var s = FamilySessions.Require(args);
            var nombre = args.RequireStr("nombre");
            return ToolHelpers.InTransaction(s.Doc, "Agregar parámetro de familia", () =>
            {
                var fp = FamilyOps.AddParameter(s.Doc, nombre, args.RequireStr("tipo_dato"), args.Str("grupo", "Geometria"), args.Bool("es_instancia", false));
                if (args.Has("valor")) FamilyOps.SetValue(s.Doc, fp, args.Str("valor"));
                s.Doc.Regenerate();
                return new
                {
                    familia_doc_id = s.Id,
                    parametro = nombre,
                    tipo_dato = FamilyValues.KindName(FamilyValues.KindOf(fp)),
                    es_instancia = fp.IsInstance,
                    valor = FamilyValues.ReadValue(s.Doc, s.Doc.FamilyManager.CurrentType, fp)
                };
            });
        }
    }

    public class SetFamilyFormulaTool : McpTool
    {
        public override string Name => "set_family_formula";
        public override string Description =>
            "Asigna una fórmula a un parámetro de familia (sintaxis de Revit, p.ej. \"Diametro_Ext - 2 * Espesor\"). " +
            "Con formula vacía quita la fórmula. Los parámetros referenciados deben existir antes.";
        public override bool ReadOnly => false;
        public override string Example => "{\"familia_doc_id\":1,\"parametro\":\"Diametro_Int\",\"formula\":\"Diametro_Ext - 2 * Espesor\"}";

        public override JObject InputSchema => Schema.Object(new[] { "familia_doc_id", "parametro" },
            ("familia_doc_id", "integer", "Id devuelto por create_family"),
            ("parametro", "string", "Parámetro que recibe la fórmula"),
            ("formula", "string", "Fórmula; vacía = quitar la fórmula"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var s = FamilySessions.Require(args);
            var fm = s.Doc.FamilyManager;
            var formula = args.Str("formula");

            return ToolHelpers.InTransaction(s.Doc, "Asignar fórmula", () =>
            {
                var fp = FamilyOps.RequireParam(fm, args.RequireStr("parametro"));
                if (!fp.CanAssignFormula) throw new ToolException($"'{fp.Definition.Name}' no admite fórmulas (¿es un parámetro compartido o integrado?)");
                try { fm.SetFormula(fp, formula == "" ? null : formula); }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    throw new ToolException($"Fórmula inválida para '{fp.Definition.Name}': {ex.Message}. " +
                        "Comprueba los nombres de parámetros (distinguen mayúsculas) y que las unidades sean coherentes");
                }
                s.Doc.Regenerate();
                return new
                {
                    parametro = fp.Definition.Name,
                    formula = fp.Formula,
                    valor = FamilyValues.ReadValue(s.Doc, fm.CurrentType, fp)
                };
            });
        }
    }

    public class CreateFamilyTypeTool : McpTool
    {
        public override string Name => "create_family_type";
        public override string Description =>
            "Crea un tipo nuevo en la familia (queda como tipo activo). Hereda los valores del tipo activo anterior; " +
            "cámbialos con set_family_type_value.";
        public override bool ReadOnly => false;
        public override string Example => "{\"familia_doc_id\":1,\"nombre\":\"125x5\"}";

        public override JObject InputSchema => Schema.Object(new[] { "familia_doc_id", "nombre" },
            ("familia_doc_id", "integer", "Id devuelto por create_family"),
            ("nombre", "string", "Nombre del tipo, p.ej. 125x5"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var s = FamilySessions.Require(args);
            var nombre = args.RequireStr("nombre");
            return ToolHelpers.InTransaction(s.Doc, "Crear tipo de familia", () =>
            {
                var fm = s.Doc.FamilyManager;
                foreach (FamilyType t in fm.Types)
                    if (string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
                        throw new ToolException($"Ya existe un tipo '{nombre}'");
                var type = fm.NewType(nombre);
                fm.CurrentType = type;
                return new { tipo = type.Name, tipos = fm.Types.Cast<FamilyType>().Select(t => t.Name) };
            });
        }
    }

    public class ActivateFamilyTypeTool : McpTool
    {
        public override string Name => "activate_family_type";
        public override string Description => "Activa un tipo de la familia: los siguientes cambios de valores se aplican a ese tipo y la geometría se regenera con él.";
        public override bool ReadOnly => false;
        public override string Example => "{\"familia_doc_id\":1,\"tipo\":\"100x5\"}";

        public override JObject InputSchema => Schema.Object(new[] { "familia_doc_id", "tipo" },
            ("familia_doc_id", "integer", "Id devuelto por create_family"),
            ("tipo", "string", "Nombre del tipo"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var s = FamilySessions.Require(args);
            return ToolHelpers.InTransaction(s.Doc, "Activar tipo de familia", () =>
            {
                var fm = s.Doc.FamilyManager;
                fm.CurrentType = FamilyOps.FindType(fm, args.RequireStr("tipo"));
                s.Doc.Regenerate();
                return new { tipo_activo = fm.CurrentType.Name };
            });
        }
    }

    public class SetFamilyTypeValueTool : McpTool
    {
        public override string Name => "set_family_type_value";
        public override string Description =>
            "Asigna el valor de un parámetro en un tipo de la familia (activa ese tipo). Longitudes en metros o con unidad " +
            "(\"125 mm\"), ángulos en grados, SiNo como true/false. Regenera la geometría.";
        public override bool ReadOnly => false;
        public override string Example => "{\"familia_doc_id\":1,\"tipo\":\"125x5\",\"parametro\":\"Diametro_Ext\",\"valor\":\"125 mm\"}";

        public override JObject InputSchema => Schema.Object(new[] { "familia_doc_id", "tipo", "parametro", "valor" },
            ("familia_doc_id", "integer", "Id devuelto por create_family"),
            ("tipo", "string", "Nombre del tipo"),
            ("parametro", "string", "Nombre del parámetro"),
            ("valor", "string", "Valor (número en metros/grados, o con unidad)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var s = FamilySessions.Require(args);
            return ToolHelpers.InTransaction(s.Doc, "Asignar valor de tipo", () =>
            {
                var fm = s.Doc.FamilyManager;
                fm.CurrentType = FamilyOps.FindType(fm, args.RequireStr("tipo"));
                var fp = FamilyOps.RequireParam(fm, args.RequireStr("parametro"));
                FamilyOps.SetValue(s.Doc, fp, args.RequireStr("valor"));
                try { s.Doc.Regenerate(); }
                catch (Exception ex)
                {
                    throw new ToolException($"Con ese valor la geometría no se puede regenerar: {ex.Message}. " +
                        "Revisa que las dimensiones sean coherentes (p.ej. espesor menor que el radio)");
                }
                return new
                {
                    tipo = fm.CurrentType.Name,
                    parametro = fp.Definition.Name,
                    valor = FamilyValues.ReadValue(s.Doc, fm.CurrentType, fp)
                };
            });
        }
    }

    public class ListFamilyParametersTool : McpTool
    {
        public override string Name => "list_family_parameters";
        public override string Description =>
            "Lista los parámetros de una familia abierta con su tipo de dato, si son de instancia, su fórmula y el valor en cada tipo " +
            "(longitudes en metros, ángulos en grados).";
        public override string Example => "{\"familia_doc_id\":1}";

        public override JObject InputSchema => Schema.Object(new[] { "familia_doc_id" },
            ("familia_doc_id", "integer", "Id devuelto por create_family"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var s = FamilySessions.Require(args);
            var fm = s.Doc.FamilyManager;
            var types = fm.Types.Cast<FamilyType>().ToList();

            var rows = fm.Parameters.Cast<FamilyParameter>()
                .OrderBy(p => p.Definition.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    var values = new Dictionary<string, object>();
                    foreach (var t in types) values[t.Name] = FamilyValues.ReadValue(s.Doc, t, p);
                    return new
                    {
                        nombre = p.Definition.Name,
                        tipo_dato = FamilyValues.KindName(FamilyValues.KindOf(p)),
                        es_instancia = p.IsInstance,
                        formula = p.IsDeterminedByFormula ? p.Formula : null,
                        valores = values
                    };
                }).ToList();

            return new
            {
                familia_doc_id = s.Id,
                tipo_activo = fm.CurrentType?.Name,
                tipos = types.Select(t => t.Name),
                total = rows.Count,
                parametros = rows
            };
        }
    }
}
