#nullable disable
using Autodesk.Revit.DB;
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RevitMcpPlugin.Families
{
    public enum ParamKind { Length, Angle, Number, Integer, YesNo, Text, Material, Other }

    /// <summary>Conversión de valores del usuario (metros, grados, texto con unidades) a unidades internas de Revit.</summary>
    internal static class FamilyValues
    {
        private static readonly Regex NumberWithUnit =
            new Regex(@"^\s*(-?\d+(?:[.,]\d+)?)\s*([a-zA-Z°""']*)\s*$", RegexOptions.Compiled);

        public static bool IsNumeric(string text) => NumberWithUnit.IsMatch(text ?? "");

        /// <summary>Número sin unidad = metros. Acepta sufijos mm, cm, m, in, ft.</summary>
        public static double LengthFeet(string text)
        {
            var m = NumberWithUnit.Match(text ?? "");
            if (!m.Success) throw new ToolException($"'{text}' no es una longitud válida. Usa un número en metros o con unidad, p.ej. 0.125 o \"125 mm\"");
            var v = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            switch (m.Groups[2].Value.ToLowerInvariant())
            {
                case "": case "m": return v / Units.FeetToMeters;
                case "mm": return v / 1000.0 / Units.FeetToMeters;
                case "cm": return v / 100.0 / Units.FeetToMeters;
                case "in": case "\"": return v / 12.0;
                case "ft": case "'": return v;
                default: throw new ToolException($"Unidad de longitud no reconocida en '{text}' (usa m, cm, mm, in o ft)");
            }
        }

        /// <summary>Número sin unidad = grados. Acepta sufijos deg, °, rad.</summary>
        public static double AngleRadians(string text)
        {
            var m = NumberWithUnit.Match(text ?? "");
            if (!m.Success) throw new ToolException($"'{text}' no es un ángulo válido. Usa grados, p.ej. 45 o \"45 deg\"");
            var v = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            switch (m.Groups[2].Value.ToLowerInvariant())
            {
                case "": case "deg": case "°": return v * Math.PI / 180.0;
                case "rad": return v;
                default: throw new ToolException($"Unidad de ángulo no reconocida en '{text}' (usa deg o rad)");
            }
        }

        public static double FeetToMeters(double feet) => Math.Round(feet * Units.FeetToMeters, 6);
        public static double RadiansToDegrees(double rad) => Math.Round(rad * 180.0 / Math.PI, 6);

        public static string CanonicalType(string tipo)
        {
            switch (FamilyHelpers.Normalize(tipo).Replace(" ", "").Replace("_", ""))
            {
                case "longitud": case "length": return "Length";
                case "angulo": case "angle": return "Angle";
                case "entero": case "integer": return "Integer";
                case "numero": case "number": return "Number";
                case "texto": case "text": return "Text";
                case "material": return "Material";
                case "sino": case "yesno": case "booleano": case "boolean": return "YesNo";
                default: throw new ToolException($"Tipo de dato '{tipo}' no soportado. Usa: Longitud, Angulo, Numero, Entero, Texto, Material, SiNo");
            }
        }

        public static ParamKind KindOf(FamilyParameter fp) => KindOf(fp.Definition);

        public static ParamKind KindOf(Definition def)
        {
#if REVIT2021
            switch (def.ParameterType)
            {
                case ParameterType.Length: return ParamKind.Length;
                case ParameterType.Angle: return ParamKind.Angle;
                case ParameterType.Number: return ParamKind.Number;
                case ParameterType.Integer: return ParamKind.Integer;
                case ParameterType.YesNo: return ParamKind.YesNo;
                case ParameterType.Text: case ParameterType.MultilineText: case ParameterType.URL: return ParamKind.Text;
                case ParameterType.Material: return ParamKind.Material;
                default: return ParamKind.Other;
            }
#else
            var t = def.GetDataType();
            if (t == SpecTypeId.Length) return ParamKind.Length;
            if (t == SpecTypeId.Angle) return ParamKind.Angle;
            if (t == SpecTypeId.Number) return ParamKind.Number;
            if (t == SpecTypeId.Int.Integer) return ParamKind.Integer;
            if (t == SpecTypeId.Boolean.YesNo) return ParamKind.YesNo;
            if (t == SpecTypeId.String.Text || t == SpecTypeId.String.MultilineText || t == SpecTypeId.String.Url) return ParamKind.Text;
            if (t == SpecTypeId.Reference.Material) return ParamKind.Material;
            return ParamKind.Other;
#endif
        }

        public static string KindName(ParamKind k)
        {
            switch (k)
            {
                case ParamKind.Length: return "Longitud";
                case ParamKind.Angle: return "Angulo";
                case ParamKind.Number: return "Numero";
                case ParamKind.Integer: return "Entero";
                case ParamKind.YesNo: return "SiNo";
                case ParamKind.Text: return "Texto";
                case ParamKind.Material: return "Material";
                default: return "Otro";
            }
        }

        /// <summary>Valor actual del parámetro en un tipo, en las unidades de la API del plugin (m, grados, texto).</summary>
        public static object ReadValue(Document famDoc, FamilyType type, FamilyParameter fp)
        {
            if (type == null) return null;
            try
            {
                var kind = KindOf(fp);
                switch (kind)
                {
                    case ParamKind.Length: return FeetToMeters(type.AsDouble(fp) ?? 0);
                    case ParamKind.Angle: return RadiansToDegrees(type.AsDouble(fp) ?? 0);
                    case ParamKind.Number: return Math.Round(type.AsDouble(fp) ?? 0, 6);
                    case ParamKind.Integer: return type.AsInteger(fp) ?? 0;
                    case ParamKind.YesNo: return (type.AsInteger(fp) ?? 0) != 0;
                    case ParamKind.Text: return type.AsString(fp) ?? "";
                    case ParamKind.Material:
                        var id = type.AsElementId(fp);
                        return id == null || id == ElementId.InvalidElementId ? "" : famDoc.GetElement(id)?.Name ?? "";
                    default: return type.AsValueString(fp);
                }
            }
            catch { return null; }
        }
    }
}
