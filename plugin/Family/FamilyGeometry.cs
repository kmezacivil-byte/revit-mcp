#nullable disable
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpPlugin.Families
{
    /// <summary>Utilidades geométricas del documento de familia: vistas, planos de referencia y cotas.</summary>
    internal static class FamilyGeo
    {
        public static XYZ AxisVector(char a) => a == 'X' ? XYZ.BasisX : a == 'Y' ? XYZ.BasisY : XYZ.BasisZ;
        public static double Comp(XYZ p, char a) => a == 'X' ? p.X : a == 'Y' ? p.Y : p.Z;

        public static char ParseAxis(string text, string field)
        {
            var t = (text ?? "").Trim().ToUpperInvariant();
            if (t == "X" || t == "Y" || t == "Z") return t[0];
            throw new ToolException($"'{field}' debe ser X, Y o Z");
        }

        /// <summary>Ejes del perfil (u, v) para una extrusión a lo largo de 'axis'.</summary>
        public static void ProfileAxes(char axis, out char u, out char v)
        {
            if (axis == 'Y') { u = 'X'; v = 'Z'; }
            else if (axis == 'X') { u = 'Y'; v = 'Z'; }
            else { u = 'X'; v = 'Y'; }
        }

        public static XYZ Point(char axis, double along, char u, double uv, char v, double vv)
        {
            var c = new double[3];
            c["XYZ".IndexOf(axis)] = along;
            c["XYZ".IndexOf(u)] = uv;
            c["XYZ".IndexOf(v)] = vv;
            return new XYZ(c[0], c[1], c[2]);
        }

        // ── Vistas ──────────────────────────────────────────────────────────────
        private static IEnumerable<View> Views(Document d) =>
            new FilteredElementCollector(d).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate);

        public static View PlanView(Document d)
        {
            var v = Views(d).FirstOrDefault(x => x.ViewType == ViewType.FloorPlan);
            if (v == null) throw new ToolException("La plantilla de familia no tiene una vista de planta; usa otra plantilla");
            return v;
        }

        public static View ElevationLookingAlong(Document d, char axis)
        {
            var v = Views(d).FirstOrDefault(x => x.ViewType == ViewType.Elevation && Math.Abs(Comp(x.ViewDirection, axis)) > 0.9);
            if (v == null) throw new ToolException($"La plantilla de familia no tiene una vista de alzado orientada según {axis}; usa otra plantilla");
            return v;
        }

        /// <summary>Vista donde el perfil de una extrusión a lo largo de 'axis' se ve de frente.</summary>
        public static View ProfileView(Document d, char axis) => axis == 'Z' ? PlanView(d) : ElevationLookingAlong(d, axis);

        /// <summary>Vista en la que un plano con normal 'normalAxis' se dibuja como línea.</summary>
        public static View PlaneCreationView(Document d, char normalAxis) =>
            normalAxis == 'Z' ? ElevationLookingAlong(d, 'Y') : PlanView(d);

        // ── Planos de referencia ───────────────────────────────────────────────
        public static ReferencePlane FindPlane(Document d, string name)
        {
            var p = new FilteredElementCollector(d).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (p == null)
            {
                var names = new FilteredElementCollector(d).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                    .Select(x => x.Name).Where(n => n != "").Take(20);
                throw new ToolException($"No existe un plano de referencia '{name}'. Planos: {string.Join(", ", names)}. Créalo con create_reference_plane");
            }
            return p;
        }

        public static char NormalAxisOf(ReferencePlane p)
        {
            var n = p.Normal;
            if (Math.Abs(n.X) > 0.999) return 'X';
            if (Math.Abs(n.Y) > 0.999) return 'Y';
            if (Math.Abs(n.Z) > 0.999) return 'Z';
            throw new ToolException($"El plano '{p.Name}' no está alineado con X, Y o Z");
        }

        public static double CoordinateOf(ReferencePlane p) => Comp(p.GetPlane().Origin, NormalAxisOf(p));

        public static ReferencePlane CreatePlane(Document d, string name, char normalAxis, double coordFt, string reference = null)
        {
            if (new FilteredElementCollector(d).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                .Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ToolException($"Ya existe un plano de referencia llamado '{name}'");

            const double h = 6.5;   // semilongitud visible del plano en pies (~2 m)
            XYZ bubble, free, cut;
            switch (normalAxis)
            {
                case 'X': bubble = new XYZ(coordFt, -h, 0); free = new XYZ(coordFt, h, 0); cut = XYZ.BasisZ; break;
                case 'Y': bubble = new XYZ(-h, coordFt, 0); free = new XYZ(h, coordFt, 0); cut = XYZ.BasisZ; break;
                default: bubble = new XYZ(-h, 0, coordFt); free = new XYZ(h, 0, coordFt); cut = XYZ.BasisY; break;
            }

            var plane = d.FamilyCreate.NewReferencePlane(bubble, free, cut, PlaneCreationView(d, normalAxis));
            plane.Name = name;
            if (!string.IsNullOrEmpty(reference)) SetStrength(plane, reference);
            return plane;
        }

        public static ReferencePlane CenterPlane(Document d, char axis)
        {
            foreach (var rp in new FilteredElementCollector(d).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>())
            {
                var n = rp.Normal;
                if (Math.Abs(Comp(n, axis)) > 0.999 && Math.Abs(Comp(rp.GetPlane().Origin, axis)) < 1e-6) return rp;
            }
            return CreatePlane(d, "MCP_Centro_" + axis, axis, 0);
        }

        private static readonly Dictionary<string, FamilyInstanceReferenceType> Strengths =
            new Dictionary<string, FamilyInstanceReferenceType>(StringComparer.OrdinalIgnoreCase)
        {
            { "ninguna", FamilyInstanceReferenceType.NotAReference }, { "fuerte", FamilyInstanceReferenceType.StrongReference },
            { "debil", FamilyInstanceReferenceType.WeakReference }, { "izquierda", FamilyInstanceReferenceType.Left },
            { "centro_izquierda_derecha", FamilyInstanceReferenceType.CenterLeftRight }, { "derecha", FamilyInstanceReferenceType.Right },
            { "frente", FamilyInstanceReferenceType.Front }, { "centro_frente_atras", FamilyInstanceReferenceType.CenterFrontBack },
            { "atras", FamilyInstanceReferenceType.Back }, { "abajo", FamilyInstanceReferenceType.Bottom },
            { "centro_elevacion", FamilyInstanceReferenceType.CenterElevation }, { "arriba", FamilyInstanceReferenceType.Top },
        };

        public static IEnumerable<string> StrengthNames => Strengths.Keys;

        public static void SetStrength(ReferencePlane plane, string reference)
        {
            if (!Strengths.TryGetValue(FamilyHelpers.Normalize(reference).Replace(" ", "_"), out var type))
                throw new ToolException($"'referencia' no reconocida. Valores: {string.Join(", ", Strengths.Keys)}");
            var p = plane.get_Parameter(BuiltInParameter.ELEM_IS_REFERENCE);
            if (p == null || p.IsReadOnly) throw new ToolException("No se pudo cambiar el tipo de referencia del plano");
            p.Set((int)type);
        }

        // ── Cotas ──────────────────────────────────────────────────────────────
        /// <summary>
        /// Cota lineal entre planos paralelos (misma normal). Se dibuja en la vista donde los planos son líneas.
        /// 'slot' desplaza la cota para que varias no se solapen.
        /// </summary>
        public static Dimension LinearDimension(Document d, View view, IList<ReferencePlane> planes, int slot)
        {
            var axis = NormalAxisOf(planes[0]);
            if (planes.Any(p => NormalAxisOf(p) != axis)) throw new ToolException("Los planos de una cota deben ser paralelos (misma normal)");

            var sorted = planes.OrderBy(CoordinateOf).ToList();
            double min = CoordinateOf(sorted.First()), max = CoordinateOf(sorted.Last());
            if (max - min < 1e-9) throw new ToolException("Los planos de la cota están en la misma posición");

            // La cota se coloca fuera del perfil, en el otro eje visible de la vista.
            char viewNormal = Math.Abs(view.ViewDirection.X) > 0.9 ? 'X' : Math.Abs(view.ViewDirection.Y) > 0.9 ? 'Y' : 'Z';
            char other = "XYZ".First(c => c != axis && c != viewNormal);
            double off = -(2.0 + slot * 1.0);

            XYZ a = Point(viewNormal, 0, axis, min, other, off);
            XYZ b = Point(viewNormal, 0, axis, max, other, off);

            var refs = new ReferenceArray();
            foreach (var p in sorted) refs.Append(p.GetReference());
            try { return d.FamilyCreate.NewLinearDimension(view, Line.CreateBound(a, b), refs); }
            catch (Exception ex)
            {
                throw new ToolException($"Revit no pudo crear la cota entre {string.Join(", ", sorted.Select(p => p.Name))}: {ex.Message}");
            }
        }
    }
}
