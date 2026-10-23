#nullable disable
using System;

namespace RevitMcpPlugin
{
    /// <summary>
    /// Perfiles que limitan cuántas herramientas ve el modelo. Útil con LLM que rinden peor con muchas herramientas
    /// (o clientes con tope de funciones) y para ofrecer solo lo necesario.
    /// </summary>
    internal static class ToolProfiles
    {
        public static readonly string[] Names = { "completo", "modelado", "familias", "solo_lectura" };

        public static string Describe(string profile)
        {
            switch (profile)
            {
                case "modelado": return "Modelado: consulta, crear, modificar y exportar (sin las herramientas de familias)";
                case "familias": return "Familias: crear familias paramétricas, más todas las herramientas de consulta";
                case "solo_lectura": return "Solo lectura: únicamente consulta";
                default: return "Completo: todas las herramientas";
            }
        }

        private static bool IsFamilyTool(McpTool t) => t.GetType().Namespace == "RevitMcpPlugin.Families";

        public static bool Includes(string profile, McpTool t)
        {
            switch (profile)
            {
                case "solo_lectura": return t.RemoteSafe;
                case "modelado": return !IsFamilyTool(t);
                case "familias": return IsFamilyTool(t) || t.RemoteSafe;
                default: return true;
            }
        }
    }
}
