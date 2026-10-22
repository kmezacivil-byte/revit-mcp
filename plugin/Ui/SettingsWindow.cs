#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevitMcpPlugin.Ui
{
    /// <summary>Ventana de configuración del servidor MCP (WPF construido en código, sin XAML).</summary>
    public sealed class SettingsWindow : Window
    {
        private readonly CheckBox _enable;
        private readonly CheckBox _modify;
        private readonly TextBlock _hint;
        private bool _loading = true;

        public SettingsWindow()
        {
            Title = "RevitMCP — Configuración";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new StackPanel { Margin = new Thickness(18) };

            // ── Estado del servidor ───────────────────────────────────────────
            root.Children.Add(Heading("Servidor MCP"));
            root.Children.Add(Line($"Estado: activo en http://127.0.0.1:{McpHttpServer.Port}/mcp"));
            int toolCount;
            try { toolCount = ToolRegistry.ListJson().Count; } catch { toolCount = 0; }
            root.Children.Add(Line($"Herramientas publicadas ahora: {toolCount}"));

            var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            folderRow.Children.Add(Button("Abrir carpeta de datos", (s, e) => Open(McpHttpServer.DataDir, true)));
            folderRow.Children.Add(Button("Ver registro de execute_code", (s, e) => Open(McpConfig.LogPath, false)));
            root.Children.Add(folderRow);

            root.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 10) });

            // ── execute_code ──────────────────────────────────────────────────
            root.Children.Add(Heading("Ejecución de código (execute_code)"));
            root.Children.Add(Wrapped(
                "Permite que Claude escriba y ejecute código C# dentro de Revit cuando ninguna otra herramienta sirve. " +
                "Está desactivada por defecto y solo tú puedes activarla desde aquí."));

            _enable = new CheckBox { Content = new TextBlock { Text = "Habilitar execute_code" }, Margin = new Thickness(0, 10, 0, 0), FontSize = 14 };
            _modify = new CheckBox
            {
                Content = new TextBlock { Text = "Permitir que modifique el modelo (pide confirmación en cada ejecución)" },
                Margin = new Thickness(22, 6, 0, 0)
            };
            root.Children.Add(_enable);
            root.Children.Add(_modify);

            _hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 12 };
            root.Children.Add(_hint);

            if (!McpConfig.ExecuteCodeSupported)
            {
                _enable.IsEnabled = false;
                _modify.IsEnabled = false;
                _hint.Text = "No disponible en esta versión de Revit: execute_code solo funciona en Revit 2025.";
                _hint.Foreground = Brushes.DimGray;
            }

            _enable.Checked += (s, e) => Refresh();
            _enable.Unchecked += (s, e) => Refresh();
            _modify.Checked += OnModifyChecked;

            // ── Botones ───────────────────────────────────────────────────────
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var save = Button("Guardar", (s, e) => Save());
            save.IsDefault = true;
            var cancel = Button("Cancelar", (s, e) => Close());
            cancel.IsCancel = true;
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            Content = root;

            _enable.IsChecked = McpConfig.ExecuteCodeEnabled;
            _modify.IsChecked = McpConfig.ExecuteCodeAllowModify;
            _loading = false;
            Refresh();
        }

        // ── Piezas de interfaz ─────────────────────────────────────────────────
        private static TextBlock Heading(string text) =>
            new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };

        private static TextBlock Line(string text) => new TextBlock { Text = text, Margin = new Thickness(0, 2, 0, 0) };

        private static TextBlock Wrapped(string text) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };

        private static Button Button(string text, RoutedEventHandler onClick)
        {
            // TextBlock en vez de texto plano: un "_" en el contenido de un botón se interpreta como tecla de acceso y se pierde.
            var b = new Button { Content = new TextBlock { Text = text }, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), MinWidth = 80 };
            b.Click += onClick;
            return b;
        }

        private static void Open(string path, bool folder)
        {
            try
            {
                if (folder) Directory.CreateDirectory(path);
                else if (!File.Exists(path)) { MessageBox.Show("Aún no hay registro: execute_code todavía no se ha usado.", "RevitMCP"); return; }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show("No se pudo abrir: " + ex.Message, "RevitMCP"); }
        }

        private void Refresh()
        {
            if (_loading || !McpConfig.ExecuteCodeSupported) return;
            bool on = _enable.IsChecked == true;
            _modify.IsEnabled = on;
            if (!on) _modify.IsChecked = false;

            if (!on)
            {
                _hint.Text = "Desactivada: Claude no ve la herramienta.";
                _hint.Foreground = Brushes.DimGray;
            }
            else if (_modify.IsChecked == true)
            {
                _hint.Text = "Activada con permiso para MODIFICAR: cada ejecución que modifique el modelo mostrará el código y exigirá confirmación. Se registra en execute_code.log.";
                _hint.Foreground = Brushes.Firebrick;
            }
            else
            {
                _hint.Text = "Activada en solo lectura: el código corre dentro de una transacción que siempre se revierte, así que nada queda modificado.";
                _hint.Foreground = Brushes.DarkGreen;
            }
        }

        private void OnModifyChecked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var answer = MessageBox.Show(this,
                "Con este permiso, Claude podrá ejecutar código que cambie o borre elementos del modelo (se puede deshacer con Ctrl+Z).\n\n" +
                "El código nunca podrá guardar el archivo, acceder a disco ni a la red, pero las comprobaciones son una red de seguridad, no un aislamiento total.\n\n" +
                "¿Permitir que modifique el modelo?",
                "RevitMCP — Permiso de modificación", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) _modify.IsChecked = false;
            Refresh();
        }

        private void Save()
        {
            try
            {
                if (McpConfig.ExecuteCodeSupported)
                    McpConfig.SaveExecuteCode(_enable.IsChecked == true, _modify.IsChecked == true);
                MessageBox.Show(this,
                    "Configuración guardada. Se aplica al instante.\n\nSi Claude aún no ve (o sigue viendo) execute_code, inicia una conversación nueva o reconecta el servidor.",
                    "RevitMCP", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo guardar la configuración: " + ex.Message, "RevitMCP", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
