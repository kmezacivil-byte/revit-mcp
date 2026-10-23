#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using RevitMcpPlugin.Auth;

namespace RevitMcpPlugin.Ui
{
    /// <summary>Ventana de configuración del servidor MCP (WPF construido en código, con el sistema de diseño DJP).</summary>
    public sealed class SettingsWindow : Window
    {
        private readonly StackPanel _profileList = new StackPanel();
        private readonly CheckBox _enable;
        private readonly CheckBox _modify;
        private readonly TextBlock _hint;
        private readonly CheckBox _remote;
        private readonly CheckBox _remoteReadOnly;
        private readonly TextBlock _remoteHint;
        private readonly StackPanel _remotePanel;
        private readonly TextBox _remoteUrl;
        private readonly TextBox _publicUrl;
        private readonly TextBlock _tunnelStatus;
        private readonly CheckBox _authRequired;
        private readonly TextBlock _accountText;
        private readonly Border _accountBadge;
        private string _cloudflaredPath = McpConfig.CloudflaredPath;
        private bool _loading = true;

        public SettingsWindow()
        {
            DjpTheme.Apply(this);
            Title = "RevitMCP — Configuración";
            Width = 620;
            MaxHeight = 820;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new StackPanel { Margin = new Thickness(20, 18, 20, 8) };

            // ── Cuenta ──────────────────────────────────────────────────────────
            var account = new StackPanel();
            account.Children.Add(DjpUi.Heading("Cuenta DJP"));
            _accountText = DjpUi.Body("");
            _accountText.Margin = new Thickness(0, 4, 0, 0);
            account.Children.Add(_accountText);
            _accountBadge = new Border { Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            account.Children.Add(_accountBadge);
            _authRequired = DjpUi.Switch("Exigir inicio de sesión para usar las herramientas");
            _authRequired.Margin = new Thickness(0, 12, 0, 0);
            account.Children.Add(_authRequired);
            account.Children.Add(DjpUi.Row(DjpUi.Button("Iniciar o cerrar sesión…", ButtonKind.Secondary, (s, e) => OpenAccount())));
            root.Children.Add(Card(account));

            // ── Servidor y perfil de herramientas ───────────────────────────────
            var server = new StackPanel();
            server.Children.Add(DjpUi.Heading("Servidor MCP"));
            int toolCount;
            try { toolCount = ToolRegistry.ListJson().Count; } catch { toolCount = 0; }
            var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            status.Children.Add(DjpUi.Badge("Activo", BadgeKind.Success));
            var statusText = DjpUi.Body($"  http://127.0.0.1:{McpHttpServer.Port}/mcp  ·  {toolCount} herramientas publicadas");
            statusText.VerticalAlignment = VerticalAlignment.Center;
            statusText.Margin = new Thickness(8, 0, 0, 0);
            status.Children.Add(statusText);
            server.Children.Add(status);

            var profileLabel = DjpUi.Label("Perfil de herramientas");
            profileLabel.Margin = new Thickness(0, 16, 0, 2);
            server.Children.Add(profileLabel);
            server.Children.Add(DjpUi.Muted("Menos herramientas ayudan a algunos modelos de IA a elegir mejor."));
            _profileList.Margin = new Thickness(0, 8, 0, 0);
            foreach (var name in ToolProfiles.Names)
            {
                var radio = DjpUi.Radio(ToolProfiles.Describe(name), "perfil", name, 500);
                radio.Margin = new Thickness(0, 0, 0, 8);
                _profileList.Children.Add(radio);
            }
            server.Children.Add(_profileList);

            var folderRow = DjpUi.Row(
                DjpUi.Button("Abrir carpeta de datos", ButtonKind.Secondary, (s, e) => Open(McpHttpServer.DataDir, true)),
                DjpUi.Button("Ver registro de execute_code", ButtonKind.Ghost, (s, e) => Open(McpConfig.LogPath, false)));
            server.Children.Add(folderRow);
            root.Children.Add(Card(server));

            // ── execute_code ────────────────────────────────────────────────────
            var exec = new StackPanel();
            exec.Children.Add(DjpUi.Heading("Ejecución de código (execute_code)"));
            var execInfo = DjpUi.Muted("Permite que la IA escriba y ejecute código C# dentro de Revit cuando ninguna otra herramienta sirve. " +
                                       "Está desactivada por defecto y solo tú puedes activarla desde aquí.");
            execInfo.Margin = new Thickness(0, 4, 0, 10);
            exec.Children.Add(execInfo);

            _enable = DjpUi.Switch("Habilitar execute_code");
            _modify = DjpUi.Check("Permitir que modifique el modelo (pide confirmación en cada ejecución)");
            _modify.Margin = new Thickness(46, 10, 0, 0);
            exec.Children.Add(_enable);
            exec.Children.Add(_modify);

            _hint = DjpUi.Muted("");
            _hint.Margin = new Thickness(0, 10, 0, 0);
            exec.Children.Add(_hint);

            if (!McpConfig.ExecuteCodeSupported)
            {
                _enable.IsEnabled = false;
                _modify.IsEnabled = false;
                _hint.Text = "No disponible en esta versión de Revit: execute_code solo funciona en Revit 2025 o superior.";
            }
            root.Children.Add(Card(exec));

            // ── Acceso remoto ───────────────────────────────────────────────────
            var remote = new StackPanel();
            remote.Children.Add(DjpUi.Heading("Acceso remoto (ChatGPT y otros clientes en la nube)"));
            var remoteInfo = DjpUi.Muted(
                "ChatGPT y los servicios web de IA solo conectan con servidores públicos por HTTPS, así que necesitan un túnel hasta tu Revit " +
                "(Cloudflare Tunnel, gratuito). Desactivado por defecto.");
            remoteInfo.Margin = new Thickness(0, 4, 0, 10);
            remote.Children.Add(remoteInfo);

            _remote = DjpUi.Switch("Permitir acceso remoto");
            _remoteReadOnly = DjpUi.Check("Solo lectura (recomendado): el acceso remoto no puede modificar el modelo ni escribir archivos");
            _remoteReadOnly.Margin = new Thickness(46, 10, 0, 0);
            remote.Children.Add(_remote);
            remote.Children.Add(_remoteReadOnly);

            _remoteHint = DjpUi.Muted("");
            _remoteHint.Margin = new Thickness(0, 10, 0, 0);
            remote.Children.Add(_remoteHint);

            _remotePanel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };

            _remotePanel.Children.Add(DjpUi.Label("1. Túnel"));
            var tunnelInfo = DjpUi.Muted("Pulsa \"Iniciar túnel\": la ventana busca cloudflared.exe (p.ej. el que descargaste en Descargas), lo ejecuta por ti " +
                                         "y rellena la dirección pública. Si ya lo tienes corriendo, pulsa \"Detectar túnel\".");
            tunnelInfo.Margin = new Thickness(0, 2, 0, 8);
            _remotePanel.Children.Add(tunnelInfo);
            _remotePanel.Children.Add(DjpUi.Muted("Dirección pública del túnel"));
            _publicUrl = DjpUi.Input();
            _publicUrl.Margin = new Thickness(0, 4, 0, 0);
            _publicUrl.Text = McpConfig.PublicUrl;
            _publicUrl.TextChanged += (s, e) => UpdateFinalUrl();
            _remotePanel.Children.Add(_publicUrl);
            _remotePanel.Children.Add(DjpUi.Row(
                DjpUi.Button("Iniciar túnel", ButtonKind.Primary, (s, e) => StartTunnel()),
                DjpUi.Button("Detectar túnel", ButtonKind.Secondary, (s, e) => DetectTunnel(true)),
                DjpUi.Button("Detener túnel", ButtonKind.Ghost, (s, e) => StopTunnel())));
            _tunnelStatus = DjpUi.Muted("");
            _tunnelStatus.Margin = new Thickness(0, 8, 0, 0);
            _remotePanel.Children.Add(_tunnelStatus);

            var step2 = DjpUi.Label("2. Dirección para pegar en ChatGPT");
            step2.Margin = new Thickness(0, 16, 0, 0);
            _remotePanel.Children.Add(step2);
            var step2Info = DjpUi.Muted("ChatGPT ▸ Complementos ▸ Añadir servidor MCP personalizado ▸ URL del servidor, con \"Sin autenticación\". " +
                                        "Esta dirección contiene el token: es la credencial, no la compartas.");
            step2Info.Margin = new Thickness(0, 2, 0, 8);
            _remotePanel.Children.Add(step2Info);
            _remoteUrl = DjpUi.Input();
            _remoteUrl.IsReadOnly = true;
            _remotePanel.Children.Add(_remoteUrl);
            _remotePanel.Children.Add(DjpUi.Row(
                DjpUi.Button("Copiar dirección para ChatGPT", ButtonKind.Primary, (s, e) => CopyUrl()),
                DjpUi.Button("Regenerar token", ButtonKind.Danger, (s, e) => Regenerate())));
            remote.Children.Add(_remotePanel);
            root.Children.Add(Card(remote));

            // ── Botones: fijos abajo, fuera del área con desplazamiento ──────────
            var save = DjpUi.Button("Guardar", ButtonKind.Primary, (s, e) => Save());
            save.IsDefault = true;
            var cancel = DjpUi.Button("Cancelar", ButtonKind.Secondary, (s, e) => Close());
            cancel.IsCancel = true;

            var dock = new DockPanel();
            var header = DjpUi.Header("Configuración MCP", "Servidor, cuenta y herramientas disponibles para la IA");
            DockPanel.SetDock(header, Dock.Top);
            var footer = DjpUi.Footer(cancel, save);
            DockPanel.SetDock(footer, Dock.Bottom);
            dock.Children.Add(header);
            dock.Children.Add(footer);
            dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root });
            Content = dock;

            _enable.Checked += (s, e) => Refresh();
            _enable.Unchecked += (s, e) => Refresh();
            _modify.Checked += OnModifyChecked;
            _remote.Checked += OnRemoteChecked;
            _remote.Unchecked += (s, e) => Refresh();
            _remoteReadOnly.Unchecked += OnRemoteWritableChecked;
            _remoteReadOnly.Checked += (s, e) => Refresh();
            _authRequired.Unchecked += OnAuthUnchecked;

            var current = McpConfig.Profile;
            var selected = _profileList.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == current)
                           ?? (RadioButton)_profileList.Children[0];
            selected.IsChecked = true;
            _enable.IsChecked = McpConfig.ExecuteCodeEnabled;
            _modify.IsChecked = McpConfig.ExecuteCodeAllowModify;
            _remote.IsChecked = McpConfig.RemoteEnabled;
            _remoteReadOnly.IsChecked = McpConfig.RemoteReadOnly;
            _authRequired.IsChecked = McpConfig.AuthRequired;
            _loading = false;
            RefreshAccount();
            Refresh();
            if (_remote.IsChecked == true && NormalizePublicUrl(_publicUrl.Text).Length == 0) DetectTunnel(false);
            UpdateFinalUrl();
        }

        // ── Piezas de interfaz ─────────────────────────────────────────────────
        private static Border Card(UIElement content)
        {
            var card = DjpUi.Card(content);
            card.Margin = new Thickness(0, 0, 0, 14);
            return card;
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

        /// <summary>Solo esquema y host: si pegan una dirección con ruta (/mcp/...), se descarta la ruta.</summary>
        private static string NormalizePublicUrl(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return "";
            if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                text = "https://" + text;
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri.Scheme + "://" + uri.Authority : "";
        }

        private string FinalUrl()
        {
            var host = NormalizePublicUrl(_publicUrl.Text);
            return host.Length == 0 ? null : $"{host}/mcp/{McpConfig.RemoteToken()}";
        }

        private void UpdateFinalUrl()
        {
            if (_remoteUrl == null) return;
            var url = FinalUrl();
            _remoteUrl.Text = url ?? "(primero inicia el túnel o escribe su dirección pública)";
        }

        private void CopyUrl()
        {
            var url = FinalUrl();
            if (url == null) { MessageBox.Show("Primero inicia el túnel o escribe su dirección pública.", "RevitMCP"); return; }
            try { Clipboard.SetText(url); }
            catch (Exception ex) { MessageBox.Show("No se pudo copiar: " + ex.Message, "RevitMCP"); }
        }

        private void SetPublicUrl(string url)
        {
            _publicUrl.Text = url;
            UpdateFinalUrl();
        }

        private void DetectTunnel(bool verbose)
        {
            var url = TunnelManager.Detect();
            if (url != null)
            {
                SetPublicUrl(url);
                _tunnelStatus.Text = "Túnel detectado: " + url;
            }
            else if (verbose)
                _tunnelStatus.Text = "No se detectó ningún túnel de cloudflared en marcha.";
        }

        private void StartTunnel()
        {
            var exe = TunnelManager.FindExecutable(_cloudflaredPath);
            if (exe == null)
            {
                var ask = MessageBox.Show(this,
                    "No encontré cloudflared.exe (busqué en el PATH, Descargas y Program Files).\n\n" +
                    "Descárgalo de https://github.com/cloudflare/cloudflared/releases/latest (cloudflared-windows-amd64.exe).\n\n" +
                    "¿Quieres elegir ahora el archivo que ya tienes?", "RevitMCP — cloudflared", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ask != MessageBoxResult.Yes) return;
                var dialog = new OpenFileDialog { Title = "Elige cloudflared.exe", Filter = "cloudflared|cloudflared*.exe|Programas|*.exe" };
                if (dialog.ShowDialog(this) != true) return;
                exe = dialog.FileName;
            }
            _cloudflaredPath = exe;

            _tunnelStatus.Text = "Iniciando el túnel con " + exe + " … (puede tardar unos segundos)";
            try
            {
                TunnelManager.Start(exe, McpHttpServer.Port,
                    url => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SetPublicUrl(url);
                        _tunnelStatus.Text = "Túnel activo: " + url + ". Puede tardar un momento en responder.";
                    })),
                    error => Dispatcher.BeginInvoke(new Action(() => _tunnelStatus.Text = error)));
            }
            catch (Exception ex)
            {
                _tunnelStatus.Text = "No se pudo iniciar cloudflared: " + ex.Message;
            }
        }

        private void StopTunnel()
        {
            TunnelManager.Stop();
            _tunnelStatus.Text = "Túnel detenido (si lo iniciaste con esta ventana). Un túnel abierto a mano en PowerShell se cierra allí.";
        }

        private void Regenerate()
        {
            var answer = MessageBox.Show(this, "Se generará un token nuevo y la dirección anterior dejará de funcionar. ¿Continuar?",
                "RevitMCP", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            McpConfig.RegenerateRemoteToken();
            Refresh();
        }

        private void OpenAccount()
        {
            var window = new AccountWindow { Owner = this };
            window.ShowDialog();
            RefreshAccount();
        }

        private void RefreshAccount()
        {
            string email = AuthManager.GetCurrentUserEmail();
            bool signedIn = email != null && AuthManager.IsAuthenticated();
            _accountText.Text = signedIn ? email : "Aún no has iniciado sesión.";
            _accountBadge.Child = DjpUi.Badge(signedIn ? "Sesión activa" : "Sin sesión", signedIn ? BadgeKind.Success : BadgeKind.Warning);
        }

        private static void Tint(TextBlock block, string brushKey) => block.Foreground = (Brush)DjpTheme.Resources[brushKey];

        private void Refresh()
        {
            if (_loading) return;

            // execute_code
            if (McpConfig.ExecuteCodeSupported)
            {
                bool on = _enable.IsChecked == true;
                _modify.IsEnabled = on;
                if (!on) _modify.IsChecked = false;

                if (!on)
                {
                    _hint.Text = "Desactivada: la IA no ve la herramienta.";
                    Tint(_hint, "InkMuted");
                }
                else if (_modify.IsChecked == true)
                {
                    _hint.Text = "Activada con permiso para MODIFICAR: cada ejecución que modifique el modelo mostrará el código y exigirá confirmación. Se registra en execute_code.log.";
                    Tint(_hint, "Danger");
                }
                else
                {
                    _hint.Text = "Activada en solo lectura: el código corre dentro de una transacción que siempre se revierte, así que nada queda modificado.";
                    Tint(_hint, "Success");
                }
            }

            // acceso remoto
            bool remoteOn = _remote.IsChecked == true;
            _remoteReadOnly.IsEnabled = remoteOn;
            _remotePanel.Visibility = remoteOn ? Visibility.Visible : Visibility.Collapsed;
            if (!remoteOn)
            {
                _remoteHint.Text = "Desactivado: el servidor solo responde en esta máquina y con el token local.";
                Tint(_remoteHint, "InkMuted");
            }
            else if (_remoteReadOnly.IsChecked == true)
            {
                _remoteHint.Text = "Activo en solo lectura: quien tenga la dirección puede consultar el modelo, pero no modificarlo.";
                Tint(_remoteHint, "GoldInk");
                UpdateFinalUrl();
            }
            else
            {
                _remoteHint.Text = "Activo con permiso de ESCRITURA: quien tenga la dirección puede modificar tu modelo. Úsalo solo con túneles que controlas.";
                Tint(_remoteHint, "Danger");
                UpdateFinalUrl();
            }
        }

        private void OnAuthUnchecked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var answer = MessageBox.Show(this,
                "Sin la exigencia de sesión, cualquier cliente de IA conectado podrá usar las herramientas sin una cuenta DJP.\n\n¿Desactivarla?",
                "RevitMCP — Cuenta DJP", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) _authRequired.IsChecked = true;
        }

        private void OnModifyChecked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var answer = MessageBox.Show(this,
                "Con este permiso, la IA podrá ejecutar código que cambie o borre elementos del modelo (se puede deshacer con Ctrl+Z).\n\n" +
                "El código nunca podrá guardar el archivo, acceder a disco ni a la red, pero las comprobaciones son una red de seguridad, no un aislamiento total.\n\n" +
                "¿Permitir que modifique el modelo?",
                "RevitMCP — Permiso de modificación", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) _modify.IsChecked = false;
            Refresh();
        }

        private void OnRemoteChecked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var answer = MessageBox.Show(this,
                "El acceso remoto abre una dirección secreta (/mcp/<token>) pensada para exponerse a internet con un túnel. " +
                "Quien conozca esa dirección podrá usar el servidor MCP de tu Revit.\n\n" +
                "Se activará en solo lectura. Compártela solo con servicios de IA que uses tú.\n\n¿Activar el acceso remoto?",
                "RevitMCP — Acceso remoto", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) { _remote.IsChecked = false; return; }
            _remoteReadOnly.IsChecked = true;
            Refresh();
        }

        private void OnRemoteWritableChecked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var answer = MessageBox.Show(this,
                "Con la escritura remota habilitada, quien tenga la dirección podrá crear, modificar y borrar elementos de tu modelo.\n\n¿Permitirlo?",
                "RevitMCP — Escritura remota", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) _remoteReadOnly.IsChecked = true;
            Refresh();
        }

        private void Save()
        {
            try
            {
                var profile = _profileList.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
                McpConfig.SaveProfile(profile != null ? (string)profile.Tag : "completo");
                McpConfig.SaveAuthRequired(_authRequired.IsChecked == true);
                if (McpConfig.ExecuteCodeSupported)
                    McpConfig.SaveExecuteCode(_enable.IsChecked == true, _modify.IsChecked == true);
                McpConfig.SaveRemote(_remote.IsChecked == true, _remoteReadOnly.IsChecked == true, NormalizePublicUrl(_publicUrl.Text), _cloudflaredPath);
                MessageBox.Show(this,
                    "Configuración guardada. Se aplica al instante.\n\nSi tu cliente de IA no refleja el cambio de herramientas, inicia una conversación nueva o reconecta el servidor.",
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
