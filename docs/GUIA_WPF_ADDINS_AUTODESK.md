# Guía de interfaces WPF para add-ins de Autodesk (marca DJP)

Guía práctica para construir ventanas WPF con la identidad de DJP dentro de add-ins de Revit, AutoCAD, Civil 3D, Navisworks, Inventor o 3ds Max. Sale de la implementación real de RevitMCP (`plugin/Ui/DjpTheme.cs`, `DjpUi.cs`, `SettingsWindow.cs`, `AccountWindow.cs`): todo lo que aparece aquí está compilado y probado en Revit 2021–2027 (net48, .NET 8 y .NET 10).

Fuentes de marca: *Guía rápida de marca DJP* (colores y tipografía) y *Guía de autenticación con Supabase*.

---

## 1. Principios

1. **Una acción principal por ventana.** Un solo botón dorado (primario). Lo demás es secundario, discreto o de peligro.
2. **El color dice algo.** Dorado = marca y acción principal; verde = correcto; rojo = destructivo o riesgoso; gris = ayuda. Nunca decorar con color.
3. **Los controles nativos de WPF no se ven bien dentro de un host de Autodesk** (esquinas cuadradas, estilo de Windows 7, sin foco visible coherente). Se reemplazan por plantillas propias: botones, radios, casillas, interruptores y cuadros de texto con bordes redondeados.
4. **Que se vea y se use bien sin ratón.** Foco de teclado visible, `Enter` = botón por defecto, `Esc` = cancelar, orden de tabulación natural.
5. **Cero dependencias del host para los estilos.** Nada de `pack://`, BAML ni archivos `.xaml` sueltos (ver §3).

---

## 2. Tokens de diseño

### 2.1 Color

| Token | Valor | Uso |
|---|---|---|
| `Carbon` | `#081013` | Texto fuerte, cabeceras, foco de teclado, marca |
| `CarbonSoft` | `#16222A` | Superficies oscuras secundarias |
| `Gold` | `#DDA40F` | Acción principal, interruptor activo, selección |
| `GoldHover` / `GoldPress` | `#E9B52C` / `#C58F08` | Estados del dorado |
| `GoldSoft` / `GoldInk` | `#FBF1D4` / `#7A5600` | Fondo y texto de avisos (no usar dorado sobre blanco para texto: no contrasta) |
| `White` | `#FFFFFF` | Tarjetas, campos |
| `Surface` | `#F3F5F6` | Fondo de la ventana |
| `Gray100/200/300/500` | `#E9EDEF` `#DFE4E7` `#C9D0D4` `#8B979D` | Hover, líneas, bordes, borde de control en reposo |
| `Ink` / `InkMuted` | `#16222A` / `#56646B` | Texto normal / texto de ayuda |
| `Success` / `SuccessSoft` | `#1D7A4B` / `#E3F4EB` | Estado correcto |
| `Danger` / `DangerSoft` | `#B3261E` / `#FBE7E5` | Error, acción destructiva |

Los grises tienen un leve tinte hacia el carbón: un gris puro junto al negro de marca se ve ajeno.
Contraste: carbón sobre dorado ≈ 8:1 (texto del botón primario); `InkMuted` sobre blanco ≈ 5.6:1. No pongas texto dorado sobre blanco ni blanco sobre dorado.

### 2.2 Forma y espacio

| Token | Valor |
|---|---|
| `RadiusSm` | 6 (casillas) |
| `RadiusMd` | 8 (botones, campos) |
| `RadiusLg` | 12 (tarjetas) |
| `RadiusPill` | 11 (etiquetas de estado) |
| Borde de control | 1.5 px (campos, botones) · 2 px (radio, casilla, interruptor) |
| Altura mínima de control | 36 px |
| Espacio | múltiplos de 4: 4, 8, 12, 16, 24 |
| Márgenes de ventana | 24 px laterales en cabecera y pie; 20 px en el contenido con tarjetas |

### 2.3 Tipografía

Familia corporativa **Poppins**, con respaldo: `Poppins, Segoe UI, Calibri, Arial`. WPF recorre la lista y usa la primera fuente instalada, así que la ventana nunca queda sin texto.

| Rol | Tamaño | Peso | Color |
|---|---|---|---|
| Título (cabecera) | 20 | SemiBold | Blanco sobre carbón |
| Subtítulo (cabecera) | 12 | Regular | Dorado |
| Encabezado de tarjeta | 15 | SemiBold | Carbón |
| Cuerpo | 13 | Regular | Ink |
| Ayuda / secundario | 12 | Regular | InkMuted |
| Etiqueta de campo | 12 | Medium | Carbón |
| Botón | 13 | SemiBold | según tipo |

> **Poppins no se incluye en el instalador.** Si la distribuyes, hazlo como `FontFamily="pack://application:,,,/Fuentes/#Poppins"` solo si tu host resuelve `pack://` (en Revit no es fiable) o instálala con el instalador del add-in en `%WINDIR%\Fonts` (requiere administrador). Sin ella, Segoe UI es el respaldo y la interfaz sigue siendo coherente.

---

## 3. Cómo cargar los estilos: XAML en texto + `XamlReader`

Dentro de Revit/AutoCAD el ensamblado del add-in se carga desde su carpeta, y los recursos `pack://application:,,,/Ensamblado;component/...` fallan según cómo se haya cargado. La solución que funciona siempre:

```csharp
internal static class DjpTheme
{
    private static ResourceDictionary _resources;
    public static ResourceDictionary Resources => _resources ??= (ResourceDictionary)XamlReader.Parse(Xaml());

    public static void Apply(Window window)
    {
        window.Resources.MergedDictionaries.Add(Resources);
        window.SetResourceReference(Window.BackgroundProperty, "Surface");
        window.SetResourceReference(Window.FontFamilyProperty, "FontBody");
        window.SetResourceReference(Window.ForegroundProperty, "Ink");
        window.FontSize = 13;
        window.UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display); // texto nítido a 100 % de escala
    }

    private static string Xaml() => @"<ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""> ... </ResourceDictionary>";
}
```

Reglas:

- El diccionario se parsea **una vez** (perezoso) y se comparte entre ventanas.
- Los estilos van dentro del XAML en texto; así no hay archivos que olvidar en el instalador.
- Los colores y radios son recursos (`{StaticResource Gold}`); los estilos de botón se generan con una función que cambia solo los colores (un solo `ControlTemplate`, cuatro botones).
- Para `net48` en `.csproj` hace falta `<UseWPF>true</UseWPF>`; en `net8.0-windows`/`net10.0-windows` también.
- Para que `XamlReader.Parse` no falle con `x:Key` hay que declarar `xmlns:x`.

> Un `ResourceDictionary` implícito (sin `x:Key`) como el de `ScrollBar` se aplica a **todas** las barras de la ventana que lo fusiona, no al host. No lo fusiones en `Application.Resources` del host.

---

## 4. Plantillas de los controles

Todas siguen el mismo patrón: `ControlTemplate` con partes nombradas y `Trigger`s para hover, pulsado, foco de teclado y deshabilitado (`Opacity 0.45–0.5`). Se muestra el esqueleto; el código completo está en `DjpTheme.cs`.

### 4.1 Botones (4 tipos)

| Tipo | Fondo | Texto | Borde | Cuándo |
|---|---|---|---|---|
| Primario | Dorado | Carbón | Dorado | La acción principal (Guardar, Iniciar sesión). **Uno por ventana.** |
| Secundario | Blanco | Carbón | Gris 300 → Carbón al pasar el ratón | Acciones normales |
| Discreto | Surface | InkMuted | Surface | Cancelar, acciones de baja importancia |
| Peligro | Rojo | Blanco | Rojo | Borrar, regenerar credenciales, cerrar sesión |

```xml
<Border x:Name="Bd" CornerRadius="{StaticResource RadiusMd}" BorderThickness="1.5" Padding="16,7">
  <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="False"/>
</Border>
<!-- Triggers: IsMouseOver → hover; IsPressed → press; IsKeyboardFocused → BorderBrush=Carbon; IsEnabled=False → Opacity 0.45 -->
```

**Trampa del guion bajo:** en un `Button`/`CheckBox`/`RadioButton`, un `_` en un `Content` de texto plano es una tecla de acceso y **desaparece** (`execute_code` se ve `executecode`). Soluciones: poner el texto dentro de un `TextBlock` (lo que hace `DjpUi.Button`) y `RecognizesAccessKey="False"` en el `ContentPresenter`.

### 4.2 Radio (botón de opción)

Anillo de 20 px con borde de 2 px (gris 500) y punto interior dorado de 10 px al marcar; el anillo pasa a dorado. Al pasar el ratón el anillo se oscurece a carbón. Texto a la derecha, alineado al centro del anillo, con ajuste de línea (`MaxWidth`) para que no se corte.

```xml
<Grid Width="20" Height="20" VerticalAlignment="Top" Margin="0,0,10,0">
  <Ellipse x:Name="Ring" Stroke="{StaticResource Gray500}" StrokeThickness="2" Fill="{StaticResource White}"/>
  <Ellipse x:Name="Dot" Width="10" Height="10" Fill="{StaticResource Gold}" Visibility="Collapsed"/>
</Grid>
```

Un grupo de radios comparte `GroupName`; la opción se guarda en `Tag` (`(string)radio.Tag`) en vez de comparar el texto visible.

### 4.3 Casilla (check)

Caja de 20 px con esquinas de 6 px, borde de 2 px; marcada = fondo dorado + marca en carbón (`Path` con `StrokeStartLineCap="Round"`). Úsala para opciones secundarias; para **activar/desactivar una función** usa el interruptor.

### 4.4 Interruptor (switch)

`CheckBox` con plantilla: pista de 42×24 (`CornerRadius=12`, gris 300; dorada al activar) y bola de 16 px que se mueve cambiando `HorizontalAlignment` de `Left` a `Right` (y su margen). La bola pasa a carbón al activarse para contrastar con el dorado.

### 4.5 Cuadros de texto y contraseña

`Border` redondeado de 1.5 px (gris 300 → gris 500 al hover → **dorado con foco de teclado**), `PART_ContentHost` obligatorio dentro de un `ScrollViewer`, `SelectionBrush=Gold`, `CaretBrush=Carbon`. Se usa la misma función para `TextBox` y `PasswordBox`.

### 4.6 Tarjetas y etiquetas de estado

- **Tarjeta:** `Border` blanco, borde `Gray200` 1 px, `RadiusLg`, padding 16, margen inferior 12–14. Agrupa un tema (Cuenta, Servidor, Acceso remoto); un encabezado de 15 px arriba y una línea de ayuda debajo.
- **Etiqueta (badge):** píldora con fondo suave y texto del color fuerte: `Success`, `Warning (GoldSoft/GoldInk)`, `Danger`, `Neutral (Gray100/InkMuted)`. Para estados: *Activo*, *Sin sesión*, *Solo lectura*.

---

## 5. Fábricas de controles (`DjpUi`)

Para ventanas construidas en código (sin archivos XAML) conviene una clase de fábricas, así una ventana nueva son 30 líneas:

```csharp
DjpUi.Header("Configuración MCP", "Servidor, cuenta y herramientas");   // banda carbón con filo dorado de 3 px
DjpUi.Card(content);                                                     // tarjeta
DjpUi.Heading / Body / Muted / Label(text)                               // texto con estilo
DjpUi.Button("Guardar", ButtonKind.Primary, handler);                    // Primary | Secondary | Ghost | Danger
DjpUi.Switch("Habilitar X") / Check("...") / Radio("...", "grupo", tag)
DjpUi.Input() / Password()
DjpUi.Badge("Activo", BadgeKind.Success);
DjpUi.Footer(cancel, save);                                              // pie con borde superior, botones a la derecha
```

Estructura estándar de una ventana:

```csharp
var dock = new DockPanel();
DockPanel.SetDock(header, Dock.Top);    // cabecera fija
DockPanel.SetDock(footer, Dock.Bottom); // botones siempre visibles
dock.Children.Add(header);
dock.Children.Add(footer);
dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body }); // lo único que se desplaza
```

Ventana: `Width` fijo (420 para diálogos, 600–640 para configuración), `SizeToContent=Height`, `MaxHeight` ≈ 820, `ResizeMode=NoResize`, `ShowInTaskbar=false`, `WindowStartupLocation=CenterOwner`.

---

### 5.1 Ventana sin barra de Windows (cabecera propia)

Patrón usado en PropertysetManager (Civil 3D) y recomendado para toda ventana de marca: se quita la barra del sistema y se dibuja una propia, con esquinas redondeadas y solo dos botones, **minimizar** y **cerrar**.

```xml
<Window WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        ResizeMode="NoResize"  <!-- CanResize + WindowResizer.EnableResize(this) si debe redimensionarse -->
        WindowStartupLocation="CenterScreen">
  <!-- Marco redondeado: es lo único que pinta; el Window es transparente -->
  <Border BorderBrush="#CCCCCC" BorderThickness="1" Background="White" CornerRadius="16">
    <Grid>
      <Border Width="20" HorizontalAlignment="Left" Background="#1a1a1a" CornerRadius="15,0,0,15"/> <!-- franja decorativa -->
      <Grid Margin="34,8,20,20">
        <!-- Cabecera propia: arrastra la ventana y aloja los botones -->
        <Grid Background="Transparent" MouseLeftButtonDown="Header_MouseLeftButtonDown">
          <Button Content="−" Style="{StaticResource CaptionButton}" Click="MinimizeButton_Click"/>
          <Button Content="✕" Style="{StaticResource CaptionButton}" Click="CloseButton_Click"/>
        </Grid>
        ...
```

```csharp
private void Header_MouseLeftButtonDown(object s, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
private void MinimizeButton_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
private void CloseButton_Click(object s, RoutedEventArgs e) => Close();
```

Reglas:

- **Radio exterior 16**, radio de la franja o banda interior = exterior − 1 (15) para que no sobresalga del borde; botones y tarjetas internas 8.
- El `Background` del `Window` debe ser `Transparent`; el color blanco va en el `Border` redondeado. Si no, se ven las esquinas cuadradas detrás.
- Los botones de ventana son de 30×30 con `CornerRadius=8`; hover gris claro y, en cerrar, texto rojo `#E81123` (o `Danger` en el tema DJP). Sin botón de maximizar salvo que la ventana lo necesite.
- Sin barra del sistema se pierde el arrastre: hay que poner `MouseLeftButtonDown` + `DragMove()` en la cabecera (con `Background="Transparent"` para que reciba el clic).
- Sin barra tampoco hay redimensión: `ResizeMode="NoResize"` para diálogos pequeños; para ventanas grandes `CanResize` + un helper (`WindowResizer`, mensajes `WM_SYSCOMMAND`/`SC_SIZE*`) y un `ResizeGrip` en la esquina inferior derecha.
- Toda ventana de la aplicación debe seguir el patrón, **incluidos los avisos de error** (`LicenseErrorWindow`): una sola ventana con barra de Windows rompe la coherencia.
- `AllowsTransparency=True` desactiva la aceleración por hardware de esa ventana: úsalo en ventanas de interfaz, no en las que dibujan gráficos pesados.
- `DialogResult` solo funciona si se abrió con `ShowDialog()`; el botón cerrar de un diálogo debe asignarlo igual que "Cancelar".

## 6. Alojar la ventana dentro del programa de Autodesk

La ventana debe tener como **propietario** la ventana principal del programa; si no, queda detrás de Revit al hacer clic fuera y no aparece en Alt+Tab como parte de él.

| Programa | Ventana modal | Propietario |
|---|---|---|
| **Revit** | `window.ShowDialog()` dentro de un `IExternalCommand` | `new WindowInteropHelper(w).Owner = commandData.Application.MainWindowHandle;` (en `Idling`: `uiApp.MainWindowHandle`) |
| **AutoCAD / Civil 3D** | `Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(w)` (modeless: `ShowModelessWindow`) | Lo asigna AutoCAD |
| **Navisworks** | `w.ShowDialog()` | `new WindowInteropHelper(w).Owner = Autodesk.Navisworks.Api.Application.Gui.MainWindow.Handle` |
| **Inventor** | `w.ShowDialog()` | `new WindowInteropHelper(w).Owner = new IntPtr(inventorApp.MainFrameHWND)` |
| **3ds Max** | `w.ShowDialog()` | `ManagedServices.AppSDK.GetMainWinHandle()` |

Reglas generales:

- **Modal** (`ShowDialog`) para configuración, inicio de sesión y asistentes; **no modal** solo si debe convivir con el modelo (en Revit, entonces cualquier acción sobre el documento va por `ExternalEvent`, nunca directo desde el evento de un botón).
- No toques la API del documento desde un hilo de la ventana ni desde `Task.Run`: usa `ExternalEvent` (Revit) o `DocumentLock` + el hilo de la aplicación (AutoCAD).
- No crees `new Application()` ni establezcas `Application.Current.Resources`: el host ya tiene el suyo y lo contaminarías.
- Cuando el host sea una versión con tema oscuro (Revit 2024+), la ventana sigue clara a propósito: es una ventana de marca, no un control nativo. Si quieres tema oscuro, define un segundo diccionario con los mismos nombres de clave.
- Un `TextBlock` dentro de `Content` evita el problema de las teclas de acceso (§4.1).

### Targets por versión (resumen)

| Host | .NET | `TargetFramework` |
|---|---|---|
| Revit 2021–2024 | Framework 4.8 | `net48` |
| Revit 2025–2026 | .NET 8 | `net8.0-windows` |
| Revit 2027 | .NET 10 | `net10.0-windows` |
| AutoCAD 2021–2024 | Framework 4.8 | `net48` |
| AutoCAD 2025+ | .NET 8 | `net8.0-windows` |

Con `LangVersion=latest` se puede usar C# moderno incluso en `net48`; evita APIs que solo existen en .NET moderno dentro del código compartido (o ponlas tras `#if`).

---

## 7. Hilos, async y bloqueos

El hilo principal de Revit/AutoCAD tiene un `SynchronizationContext`. Un `await` sin `ConfigureAwait(false)` que se espera con `.Result`/`.GetAwaiter().GetResult()` bloquea el hilo y se produce un **deadlock**.

```csharp
// Servicios de red: siempre ConfigureAwait(false)
var response = await _http.PostAsync(url, content).ConfigureAwait(false);

// Llamar a un async desde código síncrono: sacarlo del contexto del host
bool ok = Task.Run(IsAuthenticatedAsync).GetAwaiter().GetResult();

// En un controlador de botón de la ventana (hilo de interfaz) sí se puede usar await normal:
private async void OnSignIn(object s, RoutedEventArgs e) { SetBusy(true); var r = await AuthManager.SignInAsync(...); SetBusy(false); }
```

- Timeout corto en `HttpClient` (10 s) y mensajes legibles para "sin internet".
- Mientras algo está en curso: deshabilita campos y botones y cambia el texto del botón ("Verificando…"); nunca dejes la ventana congelada.
- Actualizar controles desde otro hilo: `Dispatcher.BeginInvoke(new Action(() => ...))`.

---

## 8. Patrón de autenticación (Supabase)

Basado en *supabase_auth_guide.md*. Piezas (carpeta `plugin/Auth/`):

| Archivo | Responsabilidad |
|---|---|
| `SupabaseConfig.cs` | URL y *anon key* (sustituibles por `DJP_SUPABASE_URL` / `DJP_SUPABASE_ANON_KEY`) |
| `SupabaseAuthService.cs` | `POST /auth/v1/token?grant_type=password` y `refresh_token`; traduce errores a mensajes en español; timeout 10 s |
| `AuthManager.cs` | Guarda la sesión en `HKCU\SOFTWARE\DJP\<Producto>`, la renueva si faltan < 30 min, la borra al cerrar sesión; cache de 30 s para no consultar el registro en cada llamada |
| `AccountWindow.cs` | Vista de inicio de sesión y vista de cuenta (correo, badge "Activa", *Cerrar sesión*) |

Decisiones y por qué:

1. **Tokens cifrados con DPAPI** (`ProtectedData`, `CurrentUser`, con entropía propia) en lugar de Base64: el registro no queda legible para otro usuario ni al copiarlo a otro equipo. En `net48` hay que referenciar `System.Security` y `System.Net.Http`; en .NET 8/10 con `UseWPF` ya viene en `Microsoft.WindowsDesktop.App` (si tu host no carga WPF, añade el paquete NuGet `System.Security.Cryptography.ProtectedData`).
2. **Sin red no se cierra la sesión.** Si el refresco falla por conexión, se sigue mientras el token actual no haya caducado; solo se cierra cuando Supabase responde que la sesión es inválida.
3. **La clave *anon* es pública por diseño**: no da acceso por sí sola; los datos los protegen las políticas **RLS** del proyecto. Revisa que estén activas antes de publicar el repositorio.
4. **La comprobación en el cliente es una puerta de producto, no seguridad.** Un add-in de código abierto o descompilable puede saltársela. Si hay algo que debe estar realmente protegido (licencias, datos de pago), valídalo en el servidor con el JWT (`AuthManager.GetAccessToken()`), por ejemplo en una Edge Function.
5. **Inicio de sesión al arrancar:** en Revit no hay ventana principal en `OnStartup`; se suscribe `Idling`, se muestra la ventana una sola vez y se desuscribe. Debe ser a prueba de fallos (`try/catch`): un error aquí nunca puede impedir que cargue Revit.
6. **Interruptor `autenticacion.requerida`** en la configuración (por defecto activo) con confirmación al apagarlo; `REVITMCP_AUTH=0` lo desactiva en pruebas automáticas.

En el servidor MCP la puerta está en `CallTool`: `ping` queda libre, `tools/list` sigue funcionando y las demás herramientas devuelven un mensaje claro para el usuario ("Inicia sesión con tu cuenta DJP…") en vez de un error de protocolo, de modo que la IA pueda explicárselo.

---

## 9. Accesibilidad y detalles de pulido

- **Foco de teclado visible** en todos los controles (borde carbón en botones, radios y casillas; dorado en campos). `FocusVisualStyle="{x:Null}"` solo si el control tiene su propio indicador, como aquí.
- `IsDefault` en el botón primario (Enter) e `IsCancel` en el de cancelar (Esc).
- Texto de ayuda ≥ 12 px y con contraste ≥ 4.5:1 (`InkMuted` sobre blanco lo cumple).
- Estados deshabilitados: opacidad 0.45–0.5, nunca ocultar un control que el usuario espera ver (explica por qué está deshabilitado en una línea de ayuda).
- Color + texto: un estado nunca se comunica solo con color (badge con palabra: "Activo", "Sin sesión").
- Confirmaciones solo para acciones riesgosas (modificar el modelo, abrir acceso remoto, apagar la autenticación); texto corto, consecuencia concreta y botón **No** como respuesta por defecto (`MessageBoxResult.No`).
- Mensajes de error con la causa y qué hacer; nada de excepciones en crudo.
- DPI: usa `UseLayoutRounding=True` y `SnapsToDevicePixels=True` en bordes; no fijes alturas en píxeles para texto (usa `MinHeight`). Prueba a 100, 125 y 150 % de escala.
- Textos largos: `TextWrapping=Wrap` y `MaxWidth` en casillas/radios para que el texto no se corte ni ensanche la ventana.
- Cuando cambie el texto de un `Button` en tiempo de ejecución, cambia el `TextBlock` que contiene, no `Content`.

---

## 10. Probar la interfaz sin abrir el programa

Las ventanas se pueden crear y renderizar a PNG fuera de Revit referenciando el `.dll` del add-in (la API de Revit solo hace falta si la ventana la toca). Un proyecto de consola `WinExe` `net8.0-windows` con `UseWPF`:

```csharp
var t = new Thread(() =>
{
    var w = new SettingsWindow();                 // o AccountWindow
    w.Show(); w.UpdateLayout();
    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
    timer.Tick += (s, e) =>
    {
        timer.Stop();
        var rtb = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render((FrameworkElement)w.Content);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var f = File.Create("ventana.png")) enc.Save(f);
        w.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown();
    };
    timer.Start(); Dispatcher.Run();
});
t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
```

Para ventanas que leen configuración, apunta `REVITMCP_DATA_DIR` a una carpeta temporal y escribe ahí un `config.json` con el estado que quieras ver (activado/desactivado, error visible…). Revisa siempre: estado en reposo, hover, foco de teclado, deshabilitado y mensaje de error.

---

## 11. Lista de comprobación antes de publicar una ventana

- [ ] Un solo botón primario; el resto secundario/discreto/peligro.
- [ ] Cabecera carbón con filo dorado, título blanco, subtítulo dorado (o ventana sin barra con cabecera propia, §5.1).
- [ ] Sin barra de Windows en ninguna ventana; esquinas de 16, botones minimizar y cerrar, cabecera que arrastra.
- [ ] Fondo `Surface`, contenido en tarjetas blancas de `RadiusLg`.
- [ ] Radios, casillas, interruptores y campos con las plantillas DJP (nada con aspecto de Windows por defecto).
- [ ] Botones con `TextBlock` como contenido (sin teclas de acceso accidentales).
- [ ] Pie fijo; solo el cuerpo se desplaza.
- [ ] Propietario = ventana principal del host; `CenterOwner`; modal salvo razón explícita.
- [ ] Sin `await` bloqueados en el hilo principal (`ConfigureAwait(false)` + `Task.Run`).
- [ ] Sin `pack://`, BAML ni recursos externos; el diccionario se carga con `XamlReader`.
- [ ] Teclado completo: Tab, Enter, Esc, foco visible.
- [ ] Mensajes en español claros; confirmaciones solo para acciones riesgosas.
- [ ] Probado a 100/125/150 % y renderizado a PNG.
- [ ] Compilado para todas las versiones del host que soportas.
