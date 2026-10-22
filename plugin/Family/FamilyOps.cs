#nullable disable
using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitMcpPlugin.Families
{
    /// <summary>
    /// Operaciones sobre parámetros y tipos de una familia. Todas asumen que el llamador ya abrió la transacción
    /// del documento de familia (las tools y la spec declarativa la abren).
    /// </summary>
    internal static class FamilyOps
    {
        public static FamilyParameter RequireParam(FamilyManager fm, string name)
        {
            var fp = fm.get_Parameter(name);
            if (fp == null)
            {
                var names = fm.Parameters.Cast<FamilyParameter>().Select(p => p.Definition.Name).OrderBy(n => n).Take(25);
                throw new ToolException($"La familia no tiene un parámetro '{name}'. Parámetros: {string.Join(", ", names)}. Créalo con add_family_parameter");
            }
            return fp;
        }

        public static FamilyType FindType(FamilyManager fm, string name)
        {
            foreach (FamilyType t in fm.Types)
                if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
            var names = fm.Types.Cast<FamilyType>().Select(t => t.Name);
            throw new ToolException($"La familia no tiene un tipo '{name}'. Tipos: {string.Join(", ", names)}. Créalo con create_family_type");
        }

        /// <summary>Las familias nuevas pueden no tener ningún tipo; sin tipo no se pueden asignar valores.</summary>
        public static void EnsureDefaultType(Document famDoc, string name)
        {
            var fm = famDoc.FamilyManager;
            if (fm.Types.Size == 0 || fm.CurrentType == null) fm.CurrentType = fm.NewType(name);
        }

        public static FamilyParameter AddParameter(Document famDoc, string name, string tipoDato, string grupo, bool isInstance)
        {
            var fm = famDoc.FamilyManager;
            if (fm.get_Parameter(name) != null) throw new ToolException($"Ya existe un parámetro llamado '{name}'");
            var kind = FamilyValues.CanonicalType(tipoDato);
            var group = FamilyHelpers.Normalize(grupo ?? "").Replace(" ", "_");

            try
            {
#if REVIT2021
                ParameterType pt;
                switch (kind)
                {
                    case "Length": pt = ParameterType.Length; break;
                    case "Angle": pt = ParameterType.Angle; break;
                    case "Number": pt = ParameterType.Number; break;
                    case "Integer": pt = ParameterType.Integer; break;
                    case "Text": pt = ParameterType.Text; break;
                    case "Material": pt = ParameterType.Material; break;
                    default: pt = ParameterType.YesNo; break;
                }
                BuiltInParameterGroup pg;
                switch (group)
                {
                    case "geometria": case "dimensiones": pg = BuiltInParameterGroup.PG_GEOMETRY; break;
                    case "datos_de_identidad": case "identidad": pg = BuiltInParameterGroup.PG_IDENTITY_DATA; break;
                    case "materiales_y_acabados": case "materiales": pg = BuiltInParameterGroup.PG_MATERIALS; break;
                    case "restricciones": pg = BuiltInParameterGroup.PG_CONSTRAINTS; break;
                    case "general": pg = BuiltInParameterGroup.PG_GENERAL; break;
                    case "texto": pg = BuiltInParameterGroup.PG_TEXT; break;
                    case "visibilidad": pg = BuiltInParameterGroup.PG_VISIBILITY; break;
                    default: pg = BuiltInParameterGroup.PG_DATA; break;
                }
                return fm.AddParameter(name, pg, pt, isInstance);
#else
                ForgeTypeId spec;
                switch (kind)
                {
                    case "Length": spec = SpecTypeId.Length; break;
                    case "Angle": spec = SpecTypeId.Angle; break;
                    case "Number": spec = SpecTypeId.Number; break;
                    case "Integer": spec = SpecTypeId.Int.Integer; break;
                    case "Text": spec = SpecTypeId.String.Text; break;
                    case "Material": spec = SpecTypeId.Reference.Material; break;
                    default: spec = SpecTypeId.Boolean.YesNo; break;
                }
                ForgeTypeId pg;
                switch (group)
                {
                    case "geometria": case "dimensiones": pg = GroupTypeId.Geometry; break;
                    case "datos_de_identidad": case "identidad": pg = GroupTypeId.IdentityData; break;
                    case "materiales_y_acabados": case "materiales": pg = GroupTypeId.Materials; break;
                    case "restricciones": pg = GroupTypeId.Constraints; break;
                    case "general": pg = GroupTypeId.General; break;
                    case "texto": pg = GroupTypeId.Text; break;
                    case "visibilidad": pg = GroupTypeId.Visibility; break;
                    default: pg = GroupTypeId.Data; break;
                }
                return fm.AddParameter(name, pg, spec, isInstance);
#endif
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new ToolException($"Revit rechazó el parámetro '{name}': {ex.Message}. Revisa que el nombre sea válido y no esté repetido");
            }
        }

        /// <summary>Asigna un valor al parámetro en el tipo actual, interpretando el texto según el tipo de dato.</summary>
        public static void SetValue(Document famDoc, FamilyParameter fp, string text)
        {
            var fm = famDoc.FamilyManager;
            if (fp.IsDeterminedByFormula)
                throw new ToolException($"El parámetro '{fp.Definition.Name}' está calculado por la fórmula '{fp.Formula}'. " +
                    "Quita la fórmula con set_family_formula (formula vacía) si quieres asignarle un valor");
            if (fm.CurrentType == null)
                throw new ToolException("La familia no tiene un tipo activo. Crea uno con create_family_type");

            try
            {
                switch (FamilyValues.KindOf(fp))
                {
                    case ParamKind.Length: fm.Set(fp, FamilyValues.LengthFeet(text)); break;
                    case ParamKind.Angle: fm.Set(fp, FamilyValues.AngleRadians(text)); break;
                    case ParamKind.Number:
                        if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                            throw new ToolException($"'{text}' no es un número");
                        fm.Set(fp, d); break;
                    case ParamKind.Integer:
                        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                            throw new ToolException($"'{text}' no es un entero");
                        fm.Set(fp, i); break;
                    case ParamKind.YesNo:
                        var t = FamilyHelpers.Normalize(text);
                        fm.Set(fp, t == "true" || t == "si" || t == "1" || t == "yes" ? 1 : 0); break;
                    case ParamKind.Text: fm.Set(fp, text); break;
                    case ParamKind.Material: fm.Set(fp, FindOrCreateMaterial(famDoc, text)); break;
                    default: throw new ToolException($"El tipo de dato del parámetro '{fp.Definition.Name}' no se puede asignar desde aquí");
                }
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                throw new ToolException($"No se pudo asignar '{text}' a '{fp.Definition.Name}': {ex.Message}");
            }
        }

        public static ElementId FindOrCreateMaterial(Document famDoc, string name)
        {
            var existing = new FilteredElementCollector(famDoc).OfClass(typeof(Material)).Cast<Material>()
                .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            return existing != null ? existing.Id : Material.Create(famDoc, name);
        }

        /// <summary>Valor numérico actual (unidades internas) de un parámetro de longitud/ángulo/número del tipo actual.</summary>
        public static double CurrentDouble(Document famDoc, FamilyParameter fp)
        {
            var type = famDoc.FamilyManager.CurrentType;
            return type?.AsDouble(fp) ?? 0;
        }
    }
}
