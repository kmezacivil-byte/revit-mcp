#nullable disable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevitMcpPlugin.Ui
{
    internal enum ButtonKind { Primary, Secondary, Ghost, Danger }
    internal enum BadgeKind { Neutral, Success, Warning, Danger }

    /// <summary>Fábricas de controles con el estilo DJP, para construir ventanas en código sin repetir plantillas.</summary>
    internal static class DjpUi
    {
        private static Style S(string key) => (Style)DjpTheme.Resources[key];
        private static Brush B(string key) => (Brush)DjpTheme.Resources[key];

        // ── Texto ───────────────────────────────────────────────────────────────
        public static TextBlock Heading(string text) => new TextBlock { Text = text, Style = S("DjpHeading") };
        public static TextBlock Body(string text) => new TextBlock { Text = text, Style = S("DjpBody") };
        public static TextBlock Muted(string text) => new TextBlock { Text = text, Style = S("DjpMuted") };
        public static TextBlock Label(string text) => new TextBlock { Text = text, Style = S("DjpLabel") };

        // ── Botones ─────────────────────────────────────────────────────────────
        // El contenido es un TextBlock: con texto plano, un "_" se interpreta como tecla de acceso y desaparece ("execute_code").
        public static Button Button(string text, ButtonKind kind, RoutedEventHandler onClick)
        {
            string key = kind == ButtonKind.Primary ? "DjpPrimaryButton"
                       : kind == ButtonKind.Danger ? "DjpDangerButton"
                       : kind == ButtonKind.Ghost ? "DjpGhostButton" : "DjpSecondaryButton";
            var b = new Button { Content = new TextBlock { Text = text }, Style = S(key) };
            if (onClick != null) b.Click += onClick;
            return b;
        }

        // ── Casillas y opciones: el texto se ajusta a una anchura máxima para que no se corte ──
        private static TextBlock Wrapped(string text, double maxWidth) =>
            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = maxWidth, VerticalAlignment = VerticalAlignment.Center };

        public static CheckBox Switch(string text, double maxWidth = 440) =>
            new CheckBox { Content = Wrapped(text, maxWidth), Style = S("DjpSwitch") };

        public static CheckBox Check(string text, double maxWidth = 440) =>
            new CheckBox { Content = Wrapped(text, maxWidth), Style = S("DjpCheck") };

        public static RadioButton Radio(string text, string group, object tag, double maxWidth = 440) =>
            new RadioButton { Content = Wrapped(text, maxWidth), GroupName = group, Tag = tag, Style = S("DjpRadio") };

        // ── Campos de texto ─────────────────────────────────────────────────────
        public static TextBox Input() => new TextBox { Style = S("DjpTextBox") };
        public static PasswordBox Password() => new PasswordBox { Style = S("DjpPasswordBox") };

        // ── Contenedores ────────────────────────────────────────────────────────
        public static Border Card(UIElement content) => new Border { Style = S("DjpCard"), Child = content };

        /// <summary>Etiqueta de estado con forma de píldora.</summary>
        public static Border Badge(string text, BadgeKind kind)
        {
            Brush bg, fg;
            switch (kind)
            {
                case BadgeKind.Success: bg = B("SuccessSoft"); fg = B("Success"); break;
                case BadgeKind.Warning: bg = B("GoldSoft"); fg = B("GoldInk"); break;
                case BadgeKind.Danger: bg = B("DangerSoft"); fg = B("Danger"); break;
                default: bg = B("Gray100"); fg = B("InkMuted"); break;
            }
            return new Border
            {
                Background = bg,
                CornerRadius = (CornerRadius)DjpTheme.Resources["RadiusPill"],
                Padding = new Thickness(10, 2, 10, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = text, Foreground = fg, FontSize = 11.5, FontWeight = FontWeights.SemiBold }
            };
        }

        /// <summary>Banda de cabecera carbón con filo dorado.</summary>
        public static Border Header(string title, string subtitle)
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = title, Style = S("DjpTitle") });
            if (!string.IsNullOrEmpty(subtitle)) stack.Children.Add(new TextBlock { Text = subtitle, Style = S("DjpSubtitle"), Margin = new Thickness(0, 2, 0, 0) });

            var row = new DockPanel { Margin = new Thickness(24, 14, 24, 12) };
            var logo = DjpTheme.Logo;
            if (logo != null)
            {
                var image = new Image
                {
                    Source = logo, Width = 46, Height = 46, Margin = new Thickness(0, 0, 14, 0),
                    VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                DockPanel.SetDock(image, Dock.Left);
                row.Children.Add(image);
            }
            row.Children.Add(stack);
            return new Border
            {
                Background = B("Carbon"),
                BorderBrush = B("Gold"),
                BorderThickness = new Thickness(0, 0, 0, 3),
                Child = row
            };
        }

        /// <summary>Fila horizontal de botones, alineada a la derecha, para el pie de una ventana.</summary>
        public static Border Footer(params UIElement[] buttons)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 12, 16, 14) };
            foreach (var b in buttons) row.Children.Add(b);
            return new Border { Background = B("White"), BorderBrush = B("Gray200"), BorderThickness = new Thickness(0, 1, 0, 0), Child = row };
        }

        public static StackPanel Row(params UIElement[] items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            foreach (var i in items) row.Children.Add(i);
            return row;
        }
    }
}
