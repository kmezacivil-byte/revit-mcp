#nullable disable
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitMcpPlugin.Ui
{
    /// <summary>Botón de la cinta: abre la ventana de configuración.</summary>
    [Transaction(TransactionMode.Manual)]
    public class OpenSettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            try
            {
                var window = new SettingsWindow();
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
                window.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    internal static class RibbonSetup
    {
        private const string TabName = "RevitMCP";

        public static void Create(UIControlledApplication app)
        {
            try { app.CreateRibbonTab(TabName); }
            catch { /* la pestaña ya existe */ }

            var panel = app.CreateRibbonPanel(TabName, "Servidor MCP");
            var data = new PushButtonData(
                "RevitMcpSettings", "Configuración\nMCP",
                typeof(OpenSettingsCommand).Assembly.Location, typeof(OpenSettingsCommand).FullName)
            {
                ToolTip = "Estado del servidor MCP y activación de execute_code",
                LongDescription = "Abre la ventana donde activas o desactivas la ejecución de código que Claude puede usar en Revit.",
                LargeImage = Icon(32),
                Image = Icon(16)
            };
            panel.AddItem(data);
        }

        /// <summary>Icono generado en código: cuadrado azul con "MCP".</summary>
        private static BitmapSource Icon(int size)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x1F, 0x5F, 0xA8)), null, new Rect(1, 1, size - 2, size - 2), size / 6.0, size / 6.0);
                var text = new FormattedText("MCP", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Semibold"), size * 0.36, Brushes.White, 1.0);
                dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
