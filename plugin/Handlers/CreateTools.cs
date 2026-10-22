#nullable disable
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpPlugin.Handlers
{
    public class CreateLevelTool : McpTool
    {
        public override string Name => "create_level";
        public override string Description =>
            "Crea un nivel a una elevación dada (metros). No crea vistas de planta; usa create_view para eso.";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(new[] { "nombre", "elevacion_m" },
            ("nombre", "string", "Nombre del nivel"),
            ("elevacion_m", "number", "Elevación en metros"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var nombre = args.RequireStr("nombre");
            var elevation = args.RequireDbl("elevacion_m") / Units.FeetToMeters;

            return ToolHelpers.InTransaction(doc, "Crear nivel", () =>
            {
                var level = Level.Create(doc, elevation);
                try { level.Name = nombre; }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { throw new ToolException($"Ya existe un nivel llamado '{nombre}'"); }
                return new { id = level.Id.GetId(), nombre = level.Name, elevacion_m = Units.M(level.Elevation) };
            });
        }
    }

    public class CreateGridTool : McpTool
    {
        public override string Name => "create_grid";
        public override string Description => "Crea una rejilla (eje) recta entre dos puntos en metros.";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(new[] { "x1", "y1", "x2", "y2" },
            ("x1", "number", "X inicial (m)"), ("y1", "number", "Y inicial (m)"),
            ("x2", "number", "X final (m)"), ("y2", "number", "Y final (m)"),
            ("nombre", "string", "Nombre de la rejilla, p.ej. \"A\" o \"1\" (opcional)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var p1 = new XYZ(args.RequireDbl("x1") / Units.FeetToMeters, args.RequireDbl("y1") / Units.FeetToMeters, 0);
            var p2 = new XYZ(args.RequireDbl("x2") / Units.FeetToMeters, args.RequireDbl("y2") / Units.FeetToMeters, 0);
            if (p1.DistanceTo(p2) < 1e-6) throw new ToolException("Los puntos coinciden: la rejilla no tendría longitud");
            var nombre = args.Str("nombre");

            return ToolHelpers.InTransaction(doc, "Crear rejilla", () =>
            {
                var grid = Grid.Create(doc, Line.CreateBound(p1, p2));
                if (nombre != "")
                {
                    try { grid.Name = nombre; }
                    catch (Autodesk.Revit.Exceptions.ArgumentException) { throw new ToolException($"Ya existe una rejilla llamada '{nombre}'"); }
                }
                return new { id = grid.Id.GetId(), nombre = grid.Name };
            });
        }
    }

    public class CreateFloorTool : McpTool
    {
        public override string Name => "create_floor";
        public override string Description =>
            "Crea un piso a partir de un polígono cerrado [[x,y],...] en metros, sobre un nivel.";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(new[] { "puntos" },
            ("puntos", "number[][]", "Vértices del contorno en orden: [[x,y],[x,y],...] (m), mínimo 3"),
            ("nivel", "string", "Nombre del nivel; vacío = el más bajo"),
            ("tipo_piso", "string", "Nombre del tipo de piso; vacío = el primero"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var pts = args.Points("puntos", 3);
            var level = ToolHelpers.FindLevel(doc, args.Str("nivel"));
            var floorType = ToolHelpers.FindType<FloorType>(doc, args.Str("tipo_piso"), "tipos de piso");

            var xyz = pts.Select(p => new XYZ(p[0] / Units.FeetToMeters, p[1] / Units.FeetToMeters, level.Elevation)).ToList();
            var lines = new List<Curve>();
            for (int i = 0; i < xyz.Count; i++)
            {
                var a = xyz[i];
                var b = xyz[(i + 1) % xyz.Count];
                if (a.DistanceTo(b) < 1e-6) throw new ToolException("Hay dos vértices consecutivos iguales");
                lines.Add(Line.CreateBound(a, b));
            }

            return ToolHelpers.InTransaction(doc, "Crear piso", () =>
            {
#if REVIT2021
                var curves = new CurveArray();
                foreach (var l in lines) curves.Append(l);
                var floor = doc.Create.NewFloor(curves, floorType, level, false);
#else
                var floor = Floor.Create(doc, new List<CurveLoop> { CurveLoop.Create(lines) }, floorType.Id, level.Id);
#endif
                return new { id = floor.Id.GetId(), tipo = floorType.Name, nivel = level.Name, vertices = pts.Count };
            });
        }
    }

    public class CreateRoomTool : McpTool
    {
        public override string Name => "create_room";
        public override string Description =>
            "Crea un ambiente en el punto (x, y) en metros, dentro de un recinto cerrado por muros. " +
            "Si el punto no está encerrado, el ambiente queda sin área y se avisa.";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(new[] { "x", "y" },
            ("x", "number", "X del punto dentro del recinto (m)"), ("y", "number", "Y del punto (m)"),
            ("nivel", "string", "Nombre del nivel; vacío = el más bajo"),
            ("nombre", "string", "Nombre del ambiente (opcional)"),
            ("numero", "string", "Número del ambiente (opcional)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var level = ToolHelpers.FindLevel(doc, args.Str("nivel"));
            var uv = new UV(args.RequireDbl("x") / Units.FeetToMeters, args.RequireDbl("y") / Units.FeetToMeters);
            var nombre = args.Str("nombre");
            var numero = args.Str("numero");

            return ToolHelpers.InTransaction(doc, "Crear ambiente", () =>
            {
                var room = doc.Create.NewRoom(level, uv);
                if (room == null) throw new ToolException("Revit no pudo crear el ambiente en ese punto");
                if (nombre != "") room.Name = nombre;
                if (numero != "") room.Number = numero;
                return new
                {
                    id = room.Id.GetId(),
                    numero = room.Number,
                    nombre = ToolHelpers.SafeName(room),
                    area_m2 = Units.M2(room.Area),
                    aviso = room.Area <= 0 ? "El punto no está dentro de un recinto cerrado: el ambiente quedó sin área" : null
                };
            });
        }
    }

    public class CreateSheetTool : McpTool
    {
        public override string Name => "create_sheet";
        public override string Description => "Crea una lámina con número y nombre, opcionalmente con un cuadro de rotulación.";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(new[] { "numero", "nombre" },
            ("numero", "string", "Número de lámina, p.ej. A-101"),
            ("nombre", "string", "Nombre de la lámina"),
            ("cuadro_rotulacion", "string", "Nombre del tipo de cuadro de rotulación; vacío = el primero, \"ninguno\" = sin cuadro"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var numero = args.RequireStr("numero");
            var nombre = args.RequireStr("nombre");
            var tb = args.Str("cuadro_rotulacion");

            var titleBlocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType().Cast<FamilySymbol>().ToList();
            ElementId tbId = ElementId.InvalidElementId;
            if (!tb.Equals("ninguno", StringComparison.OrdinalIgnoreCase))
            {
                var found = tb == "" ? titleBlocks.FirstOrDefault()
                    : titleBlocks.FirstOrDefault(t => ToolHelpers.SafeName(t).Equals(tb, StringComparison.OrdinalIgnoreCase));
                if (tb != "" && found == null)
                    throw new ToolException($"Cuadro de rotulación '{tb}' no encontrado. Disponibles: " +
                        string.Join(", ", titleBlocks.Select(ToolHelpers.SafeName)));
                if (found != null) tbId = found.Id;
            }

            return ToolHelpers.InTransaction(doc, "Crear lámina", () =>
            {
                var sheet = ViewSheet.Create(doc, tbId);
                try { sheet.SheetNumber = numero; }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { throw new ToolException($"Ya existe una lámina con número '{numero}'"); }
                sheet.Name = nombre;
                return new { id = sheet.Id.GetId(), numero = sheet.SheetNumber, nombre = sheet.Name };
            });
        }
    }

    public class CreateViewTool : McpTool
    {
        public override string Name => "create_view";
        public override string Description =>
            "Crea una vista: \"planta\" (plano de planta de un nivel) o \"3d\" (isométrica).";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(new[] { "tipo" },
            ("tipo", "string", "\"planta\" o \"3d\""),
            ("nivel", "string", "Nivel para la planta; vacío = el más bajo"),
            ("nombre", "string", "Nombre de la vista (opcional)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var tipo = args.RequireStr("tipo").ToLowerInvariant();
            var nombre = args.Str("nombre");
            if (tipo != "planta" && tipo != "3d") throw new ToolException("'tipo' debe ser \"planta\" o \"3d\"");

            var family = tipo == "planta" ? ViewFamily.FloorPlan : ViewFamily.ThreeDimensional;
            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == family);
            if (vft == null) throw new ToolException("El proyecto no tiene un tipo de vista para eso");
            var level = tipo == "planta" ? ToolHelpers.FindLevel(doc, args.Str("nivel")) : null;

            return ToolHelpers.InTransaction(doc, "Crear vista", () =>
            {
                View view = tipo == "planta" ? (View)ViewPlan.Create(doc, vft.Id, level.Id) : View3D.CreateIsometric(doc, vft.Id);
                if (nombre != "")
                {
                    try { view.Name = nombre; }
                    catch (Autodesk.Revit.Exceptions.ArgumentException) { throw new ToolException($"Ya existe una vista llamada '{nombre}'"); }
                }
                return new { id = view.Id.GetId(), nombre = view.Name, tipo = view.ViewType.ToString(), nivel = level?.Name ?? "" };
            });
        }
    }

    public class PlaceFamilyInstanceTool : McpTool
    {
        public override string Name => "place_family_instance";
        public override string Description =>
            "Coloca una instancia de familia en un punto (m) sobre un nivel. Sirve para familias sin anfitrión " +
            "(mobiliario, equipos, columnas). Las puertas y ventanas necesitan un muro anfitrión y no se soportan aquí. " +
            "Usa get_families para ver familias y tipos.";
        public override bool ReadOnly => false;

        public override JObject InputSchema => Schema.Object(new[] { "familia", "x", "y" },
            ("familia", "string", "Nombre exacto de la familia"),
            ("tipo", "string", "Nombre exacto del tipo; vacío = el primero de la familia"),
            ("x", "number", "X (m)"), ("y", "number", "Y (m)"),
            ("z", "number", "Desfase vertical sobre el nivel en metros (0 por defecto)"),
            ("nivel", "string", "Nombre del nivel; vacío = el más bajo"),
            ("rotacion_grados", "number", "Giro alrededor de Z en grados (0 por defecto)"));

        public override object Run(ToolArgs args, ToolContext ctx)
        {
            var doc = ctx.RequireDoc();
            var familia = args.RequireStr("familia");
            var tipo = args.Str("tipo");
            var level = ToolHelpers.FindLevel(doc, args.Str("nivel"));

            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => string.Equals(s.FamilyName, familia, StringComparison.OrdinalIgnoreCase)).ToList();
            if (symbols.Count == 0) throw new ToolException($"Familia '{familia}' no encontrada (usa get_families)");
            var symbol = tipo == "" ? symbols[0]
                : symbols.FirstOrDefault(s => ToolHelpers.SafeName(s).Equals(tipo, StringComparison.OrdinalIgnoreCase));
            if (symbol == null)
                throw new ToolException($"Tipo '{tipo}' no existe en '{familia}'. Tipos: " +
                    string.Join(", ", symbols.Select(s => ToolHelpers.SafeName(s))));

            var point = new XYZ(args.RequireDbl("x") / Units.FeetToMeters, args.RequireDbl("y") / Units.FeetToMeters,
                level.Elevation + args.Dbl("z", 0) / Units.FeetToMeters);
            var angle = args.Dbl("rotacion_grados", 0) * Math.PI / 180.0;

            return ToolHelpers.InTransaction(doc, "Colocar familia", () =>
            {
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                FamilyInstance inst;
                try { inst = doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural); }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                {
                    throw new ToolException("Esta familia requiere un anfitrión (muro, piso...) y no se puede colocar en un punto libre");
                }
                if (Math.Abs(angle) > 1e-9)
                    ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateBound(point, point + XYZ.BasisZ), angle);
                return new { id = inst.Id.GetId(), familia = symbol.FamilyName, tipo = ToolHelpers.SafeName(symbol), nivel = level.Name };
            });
        }
    }
}
