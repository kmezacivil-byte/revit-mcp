#nullable disable
using System;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace RevitMcpPlugin.Ui
{
    /// <summary>
    /// Sistema de diseño DJP para WPF (paleta y tipografía de "Guía rápida de marca"). Los estilos se escriben como XAML en texto
    /// y se cargan con XamlReader: así no dependen de BAML ni de URIs "pack://" (que dentro de Revit/AutoCAD fallan según cómo
    /// se cargue el ensamblado) y funcionan igual en .NET Framework 4.8 y en .NET 8/10.
    ///
    /// Tokens de marca: Negro carbón #081013, Dorado DJP #DDA40F, Blanco #FFFFFF. Tipografía: Poppins, con Segoe UI y Calibri de respaldo.
    /// </summary>
    internal static class DjpTheme
    {
        private static ResourceDictionary _resources;

        public static ResourceDictionary Resources => _resources ?? (_resources = Load());

        // Colores para código (los mismos valores que los tokens del XAML)
        public static readonly Color Carbon = Color.FromRgb(0x08, 0x10, 0x13);
        public static readonly Color Gold = Color.FromRgb(0xDD, 0xA4, 0x0F);

        /// <summary>Aplica fondo, tipografía y estilos a una ventana propia.</summary>
        public static void Apply(Window window)
        {
            window.Resources.MergedDictionaries.Add(Resources);
            window.SetResourceReference(Window.BackgroundProperty, "Surface");
            window.SetResourceReference(Window.FontFamilyProperty, "FontBody");
            window.SetResourceReference(Window.ForegroundProperty, "Ink");
            window.FontSize = 13;
            if (Logo != null) window.Icon = Logo;
            window.UseLayoutRounding = true;
            System.Windows.Media.TextOptions.SetTextFormattingMode(window, System.Windows.Media.TextFormattingMode.Display);
        }

        private static System.Windows.Media.Imaging.BitmapImage _logo;

        /// <summary>Logo DJP (PNG incrustado). null si no se pudo leer: la interfaz funciona igual sin él.</summary>
        public static System.Windows.Media.Imaging.BitmapImage Logo
        {
            get
            {
                if (_logo != null) return _logo;
                try
                {
                    using (var stream = typeof(DjpTheme).Assembly.GetManifestResourceStream("djp-logo.png"))
                    {
                        if (stream == null) return null;
                        var image = new System.Windows.Media.Imaging.BitmapImage();
                        image.BeginInit();
                        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;   // se copia a memoria: el stream se puede cerrar
                        image.StreamSource = stream;
                        image.EndInit();
                        image.Freeze();
                        _logo = image;
                    }
                }
                catch { _logo = null; }
                return _logo;
            }
        }

        private static ResourceDictionary Load()
        {
            return (ResourceDictionary)XamlReader.Parse(Xaml());
        }

        // ── Estilos de botón: misma plantilla, cambian colores ───────────────────
        private static string ButtonStyle(string key, string bg, string fg, string border, string hover, string press, string borderHover) => $@"
  <Style x:Key=""{key}"" TargetType=""Button"">
    <Setter Property=""Background"" Value=""{{StaticResource {bg}}}""/>
    <Setter Property=""Foreground"" Value=""{{StaticResource {fg}}}""/>
    <Setter Property=""BorderBrush"" Value=""{{StaticResource {border}}}""/>
    <Setter Property=""BorderThickness"" Value=""1.5""/>
    <Setter Property=""FontFamily"" Value=""{{StaticResource FontBody}}""/>
    <Setter Property=""FontSize"" Value=""13""/>
    <Setter Property=""FontWeight"" Value=""SemiBold""/>
    <Setter Property=""Padding"" Value=""16,7""/>
    <Setter Property=""MinHeight"" Value=""36""/>
    <Setter Property=""MinWidth"" Value=""88""/>
    <Setter Property=""Margin"" Value=""0,0,8,0""/>
    <Setter Property=""Cursor"" Value=""Hand""/>
    <Setter Property=""FocusVisualStyle"" Value=""{{x:Null}}""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""Button"">
          <Border x:Name=""Bd"" Background=""{{TemplateBinding Background}}"" BorderBrush=""{{TemplateBinding BorderBrush}}""
                  BorderThickness=""{{TemplateBinding BorderThickness}}"" CornerRadius=""{{StaticResource RadiusMd}}""
                  Padding=""{{TemplateBinding Padding}}"" SnapsToDevicePixels=""True"">
            <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center"" RecognizesAccessKey=""False""/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
              <Setter TargetName=""Bd"" Property=""Background"" Value=""{{StaticResource {hover}}}""/>
              <Setter TargetName=""Bd"" Property=""BorderBrush"" Value=""{{StaticResource {borderHover}}}""/>
            </Trigger>
            <Trigger Property=""IsPressed"" Value=""True"">
              <Setter TargetName=""Bd"" Property=""Background"" Value=""{{StaticResource {press}}}""/>
            </Trigger>
            <Trigger Property=""IsKeyboardFocused"" Value=""True"">
              <Setter TargetName=""Bd"" Property=""BorderBrush"" Value=""{{StaticResource Carbon}}""/>
            </Trigger>
            <Trigger Property=""IsEnabled"" Value=""False"">
              <Setter Property=""Opacity"" Value=""0.45""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>";

        private static string TextInputStyle(string key, string targetType) => $@"
  <Style x:Key=""{key}"" TargetType=""{targetType}"">
    <Setter Property=""Background"" Value=""{{StaticResource White}}""/>
    <Setter Property=""Foreground"" Value=""{{StaticResource Ink}}""/>
    <Setter Property=""BorderBrush"" Value=""{{StaticResource Gray300}}""/>
    <Setter Property=""BorderThickness"" Value=""1.5""/>
    <Setter Property=""Padding"" Value=""10,7""/>
    <Setter Property=""MinHeight"" Value=""36""/>
    <Setter Property=""FontFamily"" Value=""{{StaticResource FontBody}}""/>
    <Setter Property=""FontSize"" Value=""13""/>
    <Setter Property=""VerticalContentAlignment"" Value=""Center""/>
    <Setter Property=""SelectionBrush"" Value=""{{StaticResource Gold}}""/>
    <Setter Property=""CaretBrush"" Value=""{{StaticResource Carbon}}""/>
    <Setter Property=""FocusVisualStyle"" Value=""{{x:Null}}""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""{targetType}"">
          <Border x:Name=""Bd"" Background=""{{TemplateBinding Background}}"" BorderBrush=""{{TemplateBinding BorderBrush}}""
                  BorderThickness=""{{TemplateBinding BorderThickness}}"" CornerRadius=""{{StaticResource RadiusMd}}"" SnapsToDevicePixels=""True"">
            <ScrollViewer x:Name=""PART_ContentHost"" Margin=""{{TemplateBinding Padding}}"" VerticalAlignment=""Center""/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
              <Setter TargetName=""Bd"" Property=""BorderBrush"" Value=""{{StaticResource Gray500}}""/>
            </Trigger>
            <Trigger Property=""IsKeyboardFocused"" Value=""True"">
              <Setter TargetName=""Bd"" Property=""BorderBrush"" Value=""{{StaticResource Gold}}""/>
            </Trigger>
            <Trigger Property=""IsEnabled"" Value=""False"">
              <Setter Property=""Opacity"" Value=""0.5""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>";

        private static string Xaml() => @"<ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">

  <!-- ══ Tokens de color ══ Marca: Negro carbón, Dorado, Blanco. Los grises tienen un leve tinte hacia el carbón (no son grises puros). -->
  <SolidColorBrush x:Key=""Carbon""     Color=""#081013""/>
  <SolidColorBrush x:Key=""CarbonSoft"" Color=""#16222A""/>
  <SolidColorBrush x:Key=""Gold""       Color=""#DDA40F""/>
  <SolidColorBrush x:Key=""GoldHover""  Color=""#E9B52C""/>
  <SolidColorBrush x:Key=""GoldPress""  Color=""#C58F08""/>
  <SolidColorBrush x:Key=""GoldSoft""   Color=""#FBF1D4""/>
  <SolidColorBrush x:Key=""GoldInk""    Color=""#7A5600""/>
  <SolidColorBrush x:Key=""White""      Color=""#FFFFFF""/>
  <SolidColorBrush x:Key=""Surface""    Color=""#F3F5F6""/>
  <SolidColorBrush x:Key=""Gray100""    Color=""#E9EDEF""/>
  <SolidColorBrush x:Key=""Gray200""    Color=""#DFE4E7""/>
  <SolidColorBrush x:Key=""Gray300""    Color=""#C9D0D4""/>
  <SolidColorBrush x:Key=""Gray500""    Color=""#8B979D""/>
  <SolidColorBrush x:Key=""Ink""        Color=""#16222A""/>
  <SolidColorBrush x:Key=""InkMuted""   Color=""#56646B""/>
  <!-- Estado (semánticos, aparte del dorado de marca) -->
  <SolidColorBrush x:Key=""Success""     Color=""#1D7A4B""/>
  <SolidColorBrush x:Key=""SuccessSoft"" Color=""#E3F4EB""/>
  <SolidColorBrush x:Key=""Danger""      Color=""#B3261E""/>
  <SolidColorBrush x:Key=""DangerSoft""  Color=""#FBE7E5""/>
  <SolidColorBrush x:Key=""DangerHover"" Color=""#C9382F""/>
  <SolidColorBrush x:Key=""DangerPress"" Color=""#96201A""/>

  <!-- ══ Tokens de forma ══ -->
  <CornerRadius x:Key=""RadiusSm"">6</CornerRadius>
  <CornerRadius x:Key=""RadiusMd"">8</CornerRadius>
  <CornerRadius x:Key=""RadiusLg"">12</CornerRadius>
  <CornerRadius x:Key=""RadiusPill"">11</CornerRadius>

  <!-- ══ Tipografía ══ Poppins (corporativa); si no está instalada, Segoe UI y Calibri. -->
  <FontFamily x:Key=""FontBody"">Poppins, Segoe UI, Calibri, Arial</FontFamily>

  <!-- ══ Texto ══ -->
  <Style x:Key=""DjpTitle"" TargetType=""TextBlock"">
    <Setter Property=""FontSize"" Value=""20""/><Setter Property=""FontWeight"" Value=""SemiBold""/>
    <Setter Property=""Foreground"" Value=""{StaticResource White}""/><Setter Property=""TextWrapping"" Value=""Wrap""/>
  </Style>
  <Style x:Key=""DjpSubtitle"" TargetType=""TextBlock"">
    <Setter Property=""FontSize"" Value=""12""/><Setter Property=""Foreground"" Value=""{StaticResource Gold}""/>
    <Setter Property=""TextWrapping"" Value=""Wrap""/>
  </Style>
  <Style x:Key=""DjpHeading"" TargetType=""TextBlock"">
    <Setter Property=""FontSize"" Value=""15""/><Setter Property=""FontWeight"" Value=""SemiBold""/>
    <Setter Property=""Foreground"" Value=""{StaticResource Carbon}""/><Setter Property=""Margin"" Value=""0,0,0,8""/>
    <Setter Property=""TextWrapping"" Value=""Wrap""/>
  </Style>
  <Style x:Key=""DjpBody"" TargetType=""TextBlock"">
    <Setter Property=""FontSize"" Value=""13""/><Setter Property=""Foreground"" Value=""{StaticResource Ink}""/>
    <Setter Property=""TextWrapping"" Value=""Wrap""/><Setter Property=""LineHeight"" Value=""19""/>
  </Style>
  <Style x:Key=""DjpMuted"" TargetType=""TextBlock"">
    <Setter Property=""FontSize"" Value=""12""/><Setter Property=""Foreground"" Value=""{StaticResource InkMuted}""/>
    <Setter Property=""TextWrapping"" Value=""Wrap""/><Setter Property=""LineHeight"" Value=""17""/>
  </Style>
  <Style x:Key=""DjpLabel"" TargetType=""TextBlock"">
    <Setter Property=""FontSize"" Value=""12""/><Setter Property=""FontWeight"" Value=""Medium""/>
    <Setter Property=""Foreground"" Value=""{StaticResource Carbon}""/><Setter Property=""Margin"" Value=""0,0,0,4""/>
  </Style>

  <!-- ══ Contenedores ══ -->
  <Style x:Key=""DjpCard"" TargetType=""Border"">
    <Setter Property=""Background"" Value=""{StaticResource White}""/>
    <Setter Property=""BorderBrush"" Value=""{StaticResource Gray200}""/>
    <Setter Property=""BorderThickness"" Value=""1""/>
    <Setter Property=""CornerRadius"" Value=""{StaticResource RadiusLg}""/>
    <Setter Property=""Padding"" Value=""16""/>
    <Setter Property=""Margin"" Value=""0,0,0,12""/>
  </Style>

  <!-- ══ Botones ══ Primario (dorado, una sola acción principal), secundario (contorno), discreto y de peligro. -->" +
            ButtonStyle("DjpPrimaryButton", "Gold", "Carbon", "Gold", "GoldHover", "GoldPress", "GoldHover") +
            ButtonStyle("DjpSecondaryButton", "White", "Carbon", "Gray300", "Surface", "Gray100", "Carbon") +
            ButtonStyle("DjpGhostButton", "Surface", "InkMuted", "Surface", "Gray100", "Gray200", "Gray100") +
            ButtonStyle("DjpDangerButton", "Danger", "White", "Danger", "DangerHover", "DangerPress", "DangerHover") + @"

  <!-- ══ Interruptor (activar / desactivar) ══ -->
  <Style x:Key=""DjpSwitch"" TargetType=""CheckBox"">
    <Setter Property=""FontFamily"" Value=""{StaticResource FontBody}""/><Setter Property=""FontSize"" Value=""13""/>
    <Setter Property=""Foreground"" Value=""{StaticResource Ink}""/><Setter Property=""Cursor"" Value=""Hand""/>
    <Setter Property=""FocusVisualStyle"" Value=""{x:Null}""/><Setter Property=""Margin"" Value=""0,4,0,0""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""CheckBox"">
          <StackPanel Orientation=""Horizontal"" Background=""Transparent"">
            <Grid Width=""42"" Height=""24"" VerticalAlignment=""Top"" Margin=""0,0,10,0"">
              <Border x:Name=""Track"" CornerRadius=""12"" Background=""{StaticResource Gray300}"" BorderBrush=""Transparent"" BorderThickness=""2""/>
              <Ellipse x:Name=""Knob"" Width=""16"" Height=""16"" Fill=""{StaticResource White}"" HorizontalAlignment=""Left"" Margin=""4,0,0,0""/>
            </Grid>
            <ContentPresenter VerticalAlignment=""Center"" RecognizesAccessKey=""False""/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
              <Setter TargetName=""Track"" Property=""Background"" Value=""{StaticResource Gray500}""/>
            </Trigger>
            <Trigger Property=""IsChecked"" Value=""True"">
              <Setter TargetName=""Track"" Property=""Background"" Value=""{StaticResource Gold}""/>
              <Setter TargetName=""Knob"" Property=""HorizontalAlignment"" Value=""Right""/>
              <Setter TargetName=""Knob"" Property=""Margin"" Value=""0,0,4,0""/>
              <Setter TargetName=""Knob"" Property=""Fill"" Value=""{StaticResource Carbon}""/>
            </Trigger>
            <MultiTrigger>
              <MultiTrigger.Conditions><Condition Property=""IsChecked"" Value=""True""/><Condition Property=""IsMouseOver"" Value=""True""/></MultiTrigger.Conditions>
              <Setter TargetName=""Track"" Property=""Background"" Value=""{StaticResource GoldHover}""/>
            </MultiTrigger>
            <Trigger Property=""IsKeyboardFocused"" Value=""True"">
              <Setter TargetName=""Track"" Property=""BorderBrush"" Value=""{StaticResource Carbon}""/>
            </Trigger>
            <Trigger Property=""IsEnabled"" Value=""False"">
              <Setter Property=""Opacity"" Value=""0.5""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ══ Casilla cuadrada con esquinas redondeadas ══ -->
  <Style x:Key=""DjpCheck"" TargetType=""CheckBox"">
    <Setter Property=""FontFamily"" Value=""{StaticResource FontBody}""/><Setter Property=""FontSize"" Value=""13""/>
    <Setter Property=""Foreground"" Value=""{StaticResource Ink}""/><Setter Property=""Cursor"" Value=""Hand""/>
    <Setter Property=""FocusVisualStyle"" Value=""{x:Null}""/><Setter Property=""Margin"" Value=""0,6,0,0""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""CheckBox"">
          <StackPanel Orientation=""Horizontal"" Background=""Transparent"">
            <Border x:Name=""Box"" Width=""20"" Height=""20"" CornerRadius=""{StaticResource RadiusSm}"" VerticalAlignment=""Top"" Margin=""0,0,10,0""
                    Background=""{StaticResource White}"" BorderBrush=""{StaticResource Gray500}"" BorderThickness=""2"">
              <Path x:Name=""Tick"" Data=""M 3.5,9.5 L 7.5,13.5 L 14.5,5"" Stroke=""{StaticResource Carbon}"" StrokeThickness=""2.4""
                    StrokeStartLineCap=""Round"" StrokeEndLineCap=""Round"" StrokeLineJoin=""Round"" Visibility=""Collapsed""/>
            </Border>
            <ContentPresenter VerticalAlignment=""Center"" RecognizesAccessKey=""False""/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
              <Setter TargetName=""Box"" Property=""BorderBrush"" Value=""{StaticResource Carbon}""/>
            </Trigger>
            <Trigger Property=""IsChecked"" Value=""True"">
              <Setter TargetName=""Box"" Property=""Background"" Value=""{StaticResource Gold}""/>
              <Setter TargetName=""Box"" Property=""BorderBrush"" Value=""{StaticResource Gold}""/>
              <Setter TargetName=""Tick"" Property=""Visibility"" Value=""Visible""/>
            </Trigger>
            <Trigger Property=""IsKeyboardFocused"" Value=""True"">
              <Setter TargetName=""Box"" Property=""BorderBrush"" Value=""{StaticResource Carbon}""/>
            </Trigger>
            <Trigger Property=""IsEnabled"" Value=""False"">
              <Setter Property=""Opacity"" Value=""0.5""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ══ Botón de opción (radio) ══ -->
  <Style x:Key=""DjpRadio"" TargetType=""RadioButton"">
    <Setter Property=""FontFamily"" Value=""{StaticResource FontBody}""/><Setter Property=""FontSize"" Value=""13""/>
    <Setter Property=""Foreground"" Value=""{StaticResource Ink}""/><Setter Property=""Cursor"" Value=""Hand""/>
    <Setter Property=""FocusVisualStyle"" Value=""{x:Null}""/><Setter Property=""Margin"" Value=""0,6,0,0""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""RadioButton"">
          <StackPanel Orientation=""Horizontal"" Background=""Transparent"">
            <Grid Width=""20"" Height=""20"" VerticalAlignment=""Top"" Margin=""0,0,10,0"">
              <Ellipse x:Name=""Ring"" Stroke=""{StaticResource Gray500}"" StrokeThickness=""2"" Fill=""{StaticResource White}""/>
              <Ellipse x:Name=""Dot"" Width=""10"" Height=""10"" Fill=""{StaticResource Gold}"" Visibility=""Collapsed""/>
            </Grid>
            <ContentPresenter VerticalAlignment=""Center"" RecognizesAccessKey=""False""/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
              <Setter TargetName=""Ring"" Property=""Stroke"" Value=""{StaticResource Carbon}""/>
            </Trigger>
            <Trigger Property=""IsChecked"" Value=""True"">
              <Setter TargetName=""Ring"" Property=""Stroke"" Value=""{StaticResource Gold}""/>
              <Setter TargetName=""Dot"" Property=""Visibility"" Value=""Visible""/>
            </Trigger>
            <Trigger Property=""IsKeyboardFocused"" Value=""True"">
              <Setter TargetName=""Ring"" Property=""Stroke"" Value=""{StaticResource Carbon}""/>
            </Trigger>
            <Trigger Property=""IsEnabled"" Value=""False"">
              <Setter Property=""Opacity"" Value=""0.5""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
" + TextInputStyle("DjpTextBox", "TextBox") + TextInputStyle("DjpPasswordBox", "PasswordBox") + @"

  <!-- ══ Barra de desplazamiento fina ══ -->
  <Style TargetType=""ScrollBar"">
    <Setter Property=""Background"" Value=""Transparent""/>
    <Setter Property=""Width"" Value=""10""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""ScrollBar"">
          <Track x:Name=""PART_Track"" IsDirectionReversed=""True"">
            <Track.DecreaseRepeatButton><RepeatButton Command=""ScrollBar.PageUpCommand"" Opacity=""0"" Focusable=""False""/></Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton><RepeatButton Command=""ScrollBar.PageDownCommand"" Opacity=""0"" Focusable=""False""/></Track.IncreaseRepeatButton>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template>
                  <ControlTemplate TargetType=""Thumb"">
                    <Border Background=""{StaticResource Gray300}"" CornerRadius=""4"" Margin=""2,0""/>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";
    }
}
