using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
using Windows.Storage;
namespace EtapaApp
{
    public sealed partial class WebViewPage : Page
    {
        // Eventos para comunicação com a MainWindow
        public event EventHandler<bool> CanGoBackChanged;
        public event EventHandler<string> NewTabRequested;
        public event EventHandler FullScreenEntered;
        public event EventHandler FullScreenExited;
        public string Url { get; private set; } = string.Empty;
        public static WebView2 CurrentWebView { get; private set; }
        private bool isFullScreen = false;
        private bool isOnline = true;
        private bool isNavigating = false;
        private int retryCount = 0;
        private const int maxRetryAttempts = 3;
        private readonly TimeSpan retryDelay = TimeSpan.FromSeconds(2);
        private bool _currentCanGoBackState = false;
        private bool _isInitialNavigation = true;
        // --- SOLUÇÃO DOWNLOAD DE PDF: Variável para rastrear o arquivo temporário ---
        private string _tempFilePath = null;

        // Chaves para armazenamento de credenciais
        private const string MatriculaKey = "SavedMatricula";
        private const string SenhaKey = "SavedSenha";
        // Script para autopreenchimento de credenciais
        private const string AutoFillScript = @"
            (function() {
                function setupAutofill() {
                    const matriculaInput = document.getElementById('matricula');
                    const senhaInput = document.getElementById('senha');
                    
                    if (matriculaInput && senhaInput) {
                        console.log('Campos de login encontrados. Solicitando credenciais salvas...');
                        // Preencher campos se existirem valores salvos
                        window.chrome.webview.postMessage(JSON.stringify({
                            type: 'getCredentials',
                            field: 'matricula'
                        }));
                        window.chrome.webview.postMessage(JSON.stringify({
                            type: 'getCredentials',
                            field: 'senha'
                        }));
                        
                        // Salvar credenciais quando alteradas
                        function saveCredentials() {
                            console.log('Salvando credenciais...');
                            window.chrome.webview.postMessage(JSON.stringify({
                                type: 'saveCredentials',
                                matricula: matriculaInput.value,
                                senha: senhaInput.value
                            }));
                        }
                        
                        matriculaInput.addEventListener('input', saveCredentials);
                        senhaInput.addEventListener('input', saveCredentials);
                        
                        // Salvar no envio do formulário
                        const form = document.querySelector('form[action*=""login/aluno""]');
                        if (form) {
                            form.addEventListener('submit', saveCredentials);
                        }
                        
                        return true;
                    }
                    return false;
                }
                
                console.log('Iniciando autopreenchimento...');
                if (document.readyState === 'loading') {
                    document.addEventListener('DOMContentLoaded', function() {
                        if (!setupAutofill()) {
                            console.log('Campos não encontrados. Iniciando observação...');
                            const observer = new MutationObserver(setupAutofill);
                            observer.observe(document.body, { childList: true, subtree: true });
                        }
                    });
                } else {
                    if (!setupAutofill()) {
                        console.log('Campos não encontrados. Iniciando observação...');
                        const observer = new MutationObserver(setupAutofill);
                        observer.observe(document.body, { childList: true, subtree: true });
                    }
                }
            })();";
        public bool CanGoBack => WebViewControl?.CanGoBack ?? false;
        public void GoBack()
        {
            if (CanGoBack)
            {
                WebViewControl.GoBack();
            }
        }
        private const string UniversalRemoveScript = @"
            (function() {
                function removeElements() {
                    try {
                        document.documentElement.style.webkitTouchCallout = 'none';
                        document.documentElement.style.webkitUserSelect = 'none';
                        document.documentElement.style.userSelect = 'none';
                        
                        const selectorsToRemove = [
                            '#page-content-wrapper > nav',
                            '#sidebar-wrapper',
                            'body > div.row.mx-0.pt-4 > div > div.card.mt-4.border-radius-card.border-0.shadow',
                            '#page-content-wrapper > div.d-lg-flex > div.container-fluid.p-3 > div.card.bg-transparent.border-0 > div.card-body.px-0.px-md-3 > div:nth-child(1) > div.card-header.bg-soft-blue.border-left-blue.text-blue.rounded > i.fas.fa-chevron-left.btn-outline-primary.py-1.px-2.rounded.mr-2',
                            '#page-content-wrapper > div.d-lg-flex > div.container-fluid.p-3 > div.card.bg-transparent.border-0 > div.card-header.bg-dark.rounded.d-flex.align-items-center.justify-content-center',
                            'nav',
                            '.navbar',
                            '.sidebar',
                            '[class*=""sidebar""]',
                            '[id*=""sidebar""]'
                        ];
                        
                        let removedCount = 0;
                        selectorsToRemove.forEach(selector => {
                            try {
                                const elements = document.querySelectorAll(selector);
                                elements.forEach(element => {
                                    if (element && element.parentNode) {
                                        element.remove();
                                        removedCount++;
                                    }
                                });
                            } catch (err) {
                                console.warn('Erro ao remover:', selector, err);
                            }
                        });
                        
                        if (!document.getElementById('custom-style')) {
                            const style = document.createElement('style');
                            style.id = 'custom-style';
                            style.innerHTML = '::-webkit-scrollbar { display: none !important; } body { -ms-overflow-style: none; scrollbar-width: none; }';
                            document.head.appendChild(style);
                        }
                        
                        console.log('Elementos removidos:', removedCount);
                        return removedCount;
                    } catch (error) {
                        console.error('Erro na remoção:', error);
                        return 0;
                    }
                }
                
                if (document.readyState === 'loading') {
                    document.addEventListener('DOMContentLoaded', removeElements);
                } else {
                    removeElements();
                }
                
                setInterval(removeElements, 2000);
                window.customRemoveElements = removeElements;
            })();";
        private const string FullScreenDetectionScript = @"
            (function() {
                let isInFullscreen = false;
                
                function checkFullscreen() {
                    const fullscreenElement = 
                        document.fullscreenElement || 
                        document.webkitFullscreenElement || 
                        document.mozFullScreenElement || 
                        document.msFullscreenElement;
                    
                    const newState = !!fullscreenElement;
                    if (newState !== isInFullscreen) {
                        isInFullscreen = newState;
                        if (window.chrome && window.chrome.webview) {
                            window.chrome.webview.postMessage(JSON.stringify({ 
                                type: 'fullscreenChange', 
                                isFullscreen: newState 
                            }));
                        }
                    }
                }
                
                const events = ['fullscreenchange', 'webkitfullscreenchange', 'mozfullscreenchange', 'MSFullscreenChange'];
                events.forEach(event => document.addEventListener(event, checkFullscreen));
                checkFullscreen();
            })();";
        public WebViewPage()
        {
            this.InitializeComponent();
            this.Loaded += OnPageLoaded;
            NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        }
        public void SetUrl(string url)
        {
            if (!string.IsNullOrEmpty(url) && url != Url)
            {
                Url = url;
                _ = NavigateToUrlSafeAsync();
            }
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (_isInitialNavigation && e.Parameter is string url)
            {
                SetUrl(url);
                _isInitialNavigation = false;
            }
        }
        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            await InitializeWebViewSafeAsync();
            CheckConnectivity();
            if (!string.IsNullOrEmpty(Url) && _isInitialNavigation)
            {
                await NavigateToUrlSafeAsync();
            }
        }
        private async Task InitializeWebViewSafeAsync()
        {
            try
            {
                ShowLoadingOverlay(true, "Inicializando navegador...");
                if (WebViewControl.CoreWebView2 == null)
                {
                    await App.SafeEnsureCoreWebView2Async(WebViewControl);
                }
                CurrentWebView = WebViewControl;
                var settings = WebViewControl.CoreWebView2.Settings;
                settings.IsZoomControlEnabled = false;
                settings.AreDefaultContextMenusEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.UserAgent = settings.UserAgent + " EtapaAppOfflineCapable";
                // Eventos principais do WebView
                WebViewControl.NavigationStarting += OnNavigationStarting;
                WebViewControl.NavigationCompleted += OnNavigationCompleted;
                WebViewControl.CoreWebView2.ContainsFullScreenElementChanged += OnFullScreenChanged;
                WebViewControl.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                WebViewControl.CoreWebView2.DOMContentLoaded += OnDOMContentLoaded;
                WebViewControl.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
                WebViewControl.CoreWebView2.HistoryChanged += CoreWebView2_HistoryChanged;
                // --- SOLUÇÃO DOWNLOAD DE PDF: Adiciona o manipulador de download ---
                WebViewControl.CoreWebView2.DownloadStarting += CoreWebView2_DownloadStarting;
                Debug.WriteLine("WebView inicializado com sucesso");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro na inicialização do WebView: {ex.Message}");
                ShowError("Erro de Inicialização", "Não foi possível inicializar o navegador. Verifique sua conexão e tente novamente.");
            }
            finally
            {
                ShowLoadingOverlay(false);
            }
        }
        #region Manipuladores de Download de PDF
        private void CoreWebView2_DownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
        {
            // Limpa qualquer arquivo temporário anterior
            CleanupTempFile();
            // Define um caminho para o arquivo temporário
            var tempFolderPath = Path.GetTempPath();
            var tempFileName = Guid.NewGuid().ToString() + Path.GetExtension(args.DownloadOperation.ResultFilePath);
            _tempFilePath = Path.Combine(tempFolderPath, tempFileName);
            // Informa ao WebView para salvar o arquivo neste local
            args.ResultFilePath = _tempFilePath;
            args.Handled = true;
            Debug.WriteLine($"Iniciando download para: {_tempFilePath}");
            // Monitora o estado do download
            args.DownloadOperation.StateChanged += DownloadOperation_StateChanged;
        }
        private void DownloadOperation_StateChanged(CoreWebView2DownloadOperation sender, object args)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                switch (sender.State)
                {
                    case CoreWebView2DownloadState.Completed:
                        Debug.WriteLine("Download concluído. Navegando para o arquivo local...");
                        // Navega para o arquivo local para exibi-lo
                        WebViewControl.CoreWebView2.Navigate($"file:///{_tempFilePath.Replace('\\', '/')}");
                        sender.StateChanged -= DownloadOperation_StateChanged; // Limpa o evento
                        break;
                    case CoreWebView2DownloadState.Interrupted:
                        Debug.WriteLine("Download interrompido.");
                        ShowError("Download Falhou", "O download do arquivo foi interrompido.");
                        CleanupTempFile();
                        sender.StateChanged -= DownloadOperation_StateChanged;
                        break;
                }
            });
        }
        private void CleanupTempFile()
        {
            try
            {
                if (!string.IsNullOrEmpty(_tempFilePath) && File.Exists(_tempFilePath))
                {
                    File.Delete(_tempFilePath);
                    Debug.WriteLine($"Arquivo temporário removido: {_tempFilePath}");
                    _tempFilePath = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao limpar arquivo temporário: {ex.Message}");
            }
        }
        #endregion
        #region Autopreenchimento de Credenciais
        // Salva as credenciais no armazenamento local
        private void SaveCredentials(string matricula, string senha)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[MatriculaKey] = matricula;
                ApplicationData.Current.LocalSettings.Values[SenhaKey] = senha;
                Debug.WriteLine("Credenciais salvas com sucesso");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao salvar credenciais: {ex.Message}");
            }
        }
        // Recupera uma credencial do armazenamento local
        private string GetSavedCredential(string key)
        {
            try
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object value))
                {
                    return value?.ToString() ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao recuperar credencial: {ex.Message}");
            }
            return string.Empty;
        }
        // Verifica se a URL é a página de login
        private bool IsLoginPage(Uri uri)
        {
            return uri.AbsoluteUri.Contains("https://areaexclusiva.colegioetapa.com.br/");
        }
        #endregion
        private void CoreWebView2_NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
        {
            args.Handled = true;
            NewTabRequested?.Invoke(this, args.Uri);
        }
        private void CoreWebView2_HistoryChanged(CoreWebView2 sender, object args)
        {
            DispatcherQueue.TryEnqueue(UpdateCanGoBackState);
        }
        private void UpdateCanGoBackState()
        {
            if (WebViewControl != null && _currentCanGoBackState != CanGoBack)
            {
                _currentCanGoBackState = CanGoBack;
                CanGoBackChanged?.Invoke(this, _currentCanGoBackState);
            }
        }
        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            // A limpeza de eventos agora é feita ao fechar a aba em MainWindow
        }
        public void DisposeWebViewCode()
        {
            CleanupEvents();
            CleanupTempFile(); // Garante a limpeza ao fechar a aba
            CurrentWebView = null;
            Debug.WriteLine("WebView disposed manualmente");
        }
        private void CleanupEvents()
        {
            try
            {
                NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
                if (WebViewControl != null)
                {
                    WebViewControl.NavigationStarting -= OnNavigationStarting;
                    WebViewControl.NavigationCompleted -= OnNavigationCompleted;
                    if (WebViewControl.CoreWebView2 != null)
                    {
                        WebViewControl.CoreWebView2.ContainsFullScreenElementChanged -= OnFullScreenChanged;
                        WebViewControl.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                        WebViewControl.CoreWebView2.DOMContentLoaded -= OnDOMContentLoaded;
                        WebViewControl.CoreWebView2.NewWindowRequested -= CoreWebView2_NewWindowRequested;
                        WebViewControl.CoreWebView2.HistoryChanged -= CoreWebView2_HistoryChanged;
                        WebViewControl.CoreWebView2.DownloadStarting -= CoreWebView2_DownloadStarting;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro durante limpeza de recursos: {ex.Message}");
            }
        }
        private async Task NavigateToUrlSafeAsync()
        {
            if (isNavigating) return;
            try
            {
                if (string.IsNullOrEmpty(Url)) return;
                if (WebViewControl?.CoreWebView2 == null)
                {
                    await InitializeWebViewSafeAsync();
                    if (WebViewControl?.CoreWebView2 == null)
                    {
                        ShowOfflineMessage();
                        return;
                    }
                }
                if (!isOnline)
                {
                    ShowOfflineMessage();
                    return;
                }
                isNavigating = true;
                retryCount = 0;
                Debug.WriteLine($"Navegando para: {Url}");
                WebViewControl.Source = new Uri(Url);
            }
            catch (UriFormatException ex)
            {
                Debug.WriteLine($"URL inválida: {ex.Message}");
                ShowError("URL Inválida", "A URL fornecida não é válida.");
                isNavigating = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro na navegação: {ex.Message}");
                await HandleNavigationError(ex);
            }
        }
        private async Task HandleNavigationError(Exception ex)
        {
            isNavigating = false;
            if (!isOnline)
            {
                ShowOfflineMessage();
                return;
            }
            if (retryCount < maxRetryAttempts)
            {
                retryCount++;
                Debug.WriteLine($"Tentativa {retryCount} de {maxRetryAttempts}...");
                ShowLoadingOverlay(true, $"Tentando conectar... ({retryCount}/{maxRetryAttempts})",
                    "Verifique sua conexão com a internet");
                await Task.Delay(retryDelay);
                if (isOnline)
                {
                    await NavigateToUrlSafeAsync();
                }
                else
                {
                    ShowOfflineMessage();
                }
            }
            else
            {
                ShowError("Erro de Conexão", "Não foi possível carregar a página após várias tentativas. Verifique sua conexão com a internet.");
            }
        }
        private void OnNavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            // Se a navegação não for para um arquivo local, limpa o PDF temporário antigo
            if (!args.Uri.StartsWith("file:///"))
            {
                CleanupTempFile();
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!isFullScreen && isOnline)
                {
                    ShowLoadingOverlay(true, "Carregando...");
                }
            });
        }
        private async void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            isNavigating = false;
            DispatcherQueue.TryEnqueue(() =>
            {
                ShowLoadingOverlay(false);
            });
            if (args.IsSuccess)
            {
                retryCount = 0;
                // Não injeta scripts em arquivos locais para evitar erros
                if (!sender.Source.ToString().StartsWith("file:///"))
                {
                    try
                    {
                        await SafeExecuteScriptAsync(FullScreenDetectionScript);
                        await SafeExecuteScriptAsync(UniversalRemoveScript);
                        // Se for a página de login, injeta o script de autopreenchimento
                        if (IsLoginPage(sender.Source))
                        {
                            Debug.WriteLine("Injetando script de autopreenchimento (NavigationCompleted)");
                            await SafeExecuteScriptAsync(AutoFillScript);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Erro ao executar scripts: {ex.Message}");
                    }
                }
                Debug.WriteLine($"Navegação completada com sucesso para: {sender.Source}");
            }
            else
            {
                Debug.WriteLine($"Navegação falhou: {args.WebErrorStatus}");
                if (args.WebErrorStatus != CoreWebView2WebErrorStatus.ConnectionAborted && isOnline)
                {
                    await HandleNavigationFailure(args.WebErrorStatus);
                }
                else if (!isOnline)
                {
                    ShowOfflineMessage();
                }
            }
        }
        private async Task HandleNavigationFailure(CoreWebView2WebErrorStatus errorStatus)
        {
            var errorMessage = GetUserFriendlyErrorMessage(errorStatus);
            if (ShouldRetryOnError(errorStatus) && retryCount < maxRetryAttempts)
            {
                Debug.WriteLine($"Erro recuperável: {errorStatus}, tentando novamente...");
                await HandleNavigationError(new Exception($"Navigation failed: {errorStatus}"));
            }
            else
            {
                ShowError("Erro ao Carregar Página", errorMessage);
            }
        }
        private bool ShouldRetryOnError(CoreWebView2WebErrorStatus errorStatus)
        {
            return errorStatus == CoreWebView2WebErrorStatus.Timeout ||
                   errorStatus == CoreWebView2WebErrorStatus.HostNameNotResolved ||
                   errorStatus == CoreWebView2WebErrorStatus.ConnectionAborted ||
                   errorStatus == CoreWebView2WebErrorStatus.ConnectionReset ||
                   errorStatus == CoreWebView2WebErrorStatus.Disconnected;
        }
        private string GetUserFriendlyErrorMessage(CoreWebView2WebErrorStatus errorStatus)
        {
            return errorStatus switch
            {
                CoreWebView2WebErrorStatus.HostNameNotResolved => "Não foi possível encontrar o servidor. Verifique sua conexão com a internet.",
                CoreWebView2WebErrorStatus.Timeout => "A conexão expirou. Verifique sua conexão e tente novamente.",
                CoreWebView2WebErrorStatus.ConnectionAborted => "A conexão foi interrompida. Tente novamente.",
                CoreWebView2WebErrorStatus.ConnectionReset => "A conexão foi reiniciada. Verifique sua rede.",
                CoreWebView2WebErrorStatus.Disconnected => "Sem conexão com a internet. Verifique sua rede.",
                CoreWebView2WebErrorStatus.CannotConnect => "Não foi possível conectar ao servidor.",
                _ => "Ocorreu um erro desconhecido ao carregar a página."
            };
        }
        private async Task SafeExecuteScriptAsync(string script)
        {
            try
            {
                if (WebViewControl?.CoreWebView2 != null && isOnline)
                {
                    await WebViewControl.ExecuteScriptAsync(script);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao executar script: {ex.Message}");
            }
        }
        private async void OnDOMContentLoaded(object sender, CoreWebView2DOMContentLoadedEventArgs e)
        {
            if (!WebViewControl.Source.ToString().StartsWith("file:///"))
            {
                await SafeExecuteScriptAsync(UniversalRemoveScript);
                // Se for a página de login, injetar o script de autopreenchimento
                if (IsLoginPage(WebViewControl.Source))
                {
                    Debug.WriteLine("Injetando script de autopreenchimento (DOMContentLoaded)");
                    await SafeExecuteScriptAsync(AutoFillScript);
                }
            }
        }
        private void CheckConnectivity()
        {
            try
            {
                var connectionProfile = NetworkInformation.GetInternetConnectionProfile();
                var networkConnectivity = connectionProfile?.GetNetworkConnectivityLevel();
                isOnline = networkConnectivity == NetworkConnectivityLevel.InternetAccess;
                Debug.WriteLine($"Status de conectividade: {(isOnline ? "Online" : "Offline")}");
                if (!isOnline)
                {
                    ShowOfflineMessage();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao verificar conectividade: {ex.Message}");
                isOnline = true;
            }
        }
        private void OnNetworkStatusChanged(object sender)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var wasOnline = isOnline;
                CheckConnectivity();
                if (!wasOnline && isOnline)
                {
                    Debug.WriteLine("Conectividade restaurada, tentando navegar novamente...");
                    _ = NavigateToUrlSafeAsync();
                }
                else if (wasOnline && !isOnline)
                {
                    Debug.WriteLine("Conectividade perdida");
                    ShowOfflineMessage();
                }
            });
        }
        private void ShowOfflineMessage()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ShowLoadingOverlay(true, "Sem conexão com a internet",
                    "Verifique sua conexão e aguarde a reconexão automática. Caso o erro persista, reincie o app.");
            });
        }
        private void OnFullScreenChanged(object sender, object e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (WebViewControl?.CoreWebView2 == null) return;
                    var newState = WebViewControl.CoreWebView2.ContainsFullScreenElement;
                    if (newState && !isFullScreen)
                    {
                        isFullScreen = true;
                        FullScreenEntered?.Invoke(this, EventArgs.Empty);
                    }
                    else if (!newState && isFullScreen)
                    {
                        isFullScreen = false;
                        FullScreenExited?.Invoke(this, EventArgs.Empty);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Erro ao processar mudança de tela cheia: {ex.Message}");
                }
            });
        }
        private void ShowLoadingOverlay(bool show, string message = "Carregando...", string subtext = null)
        {
            try
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    LoadingIndicator.IsActive = show;
                    if (show)
                    {
                        LoadingText.Text = message;
                        LoadingSubtext.Visibility = !string.IsNullOrEmpty(subtext) ? Visibility.Visible : Visibility.Collapsed;
                        if (LoadingSubtext.Visibility == Visibility.Visible)
                        {
                            LoadingSubtext.Text = subtext;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao mostrar/ocultar loading overlay: {ex.Message}");
            }
        }
        private void ShowError(string title, string message)
        {
            if (Content?.XamlRoot == null) return;
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var dlg = new ContentDialog
                    {
                        Title = title,
                        Content = message,
                        CloseButtonText = "OK",
                        XamlRoot = Content.XamlRoot
                    };
                    await dlg.ShowAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Erro ao mostrar diálogo: {ex.Message}");
                }
            });
        }
        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var message = e.TryGetWebMessageAsString();
                Debug.WriteLine($"Mensagem recebida: {message}");
                if (!string.IsNullOrEmpty(message))
                {
                    // Tratamento de mensagens de tela cheia
                    if (message.Contains("fullscreenChange"))
                    {
                        var data = JsonSerializer.Deserialize<FullScreenMessage>(message);
                        if (data?.type == "fullscreenChange")
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (data.isFullscreen && !isFullScreen)
                                {
                                    isFullScreen = true;
                                    FullScreenEntered?.Invoke(this, EventArgs.Empty);
                                }
                                else if (!data.isFullscreen && isFullScreen)
                                {
                                    isFullScreen = false;
                                    FullScreenExited?.Invoke(this, EventArgs.Empty);
                                }
                            });
                        }
                    }
                    // Tratamento de mensagens de credenciais
                    else if (message.Contains("saveCredentials"))
                    {
                        var data = JsonSerializer.Deserialize<SaveCredentialsMessage>(message);
                        if (data?.type == "saveCredentials")
                        {
                            SaveCredentials(data.matricula, data.senha);
                        }
                    }
                    // Resposta para solicitação de credenciais
                    else if (message.Contains("getCredentials"))
                    {
                        var data = JsonSerializer.Deserialize<GetCredentialsMessage>(message);
                        if (data?.type == "getCredentials")
                        {
                            string key = data.field == "matricula" ? MatriculaKey : SenhaKey;
                            string value = GetSavedCredential(key);
                            Debug.WriteLine($"Solicitação de credencial para campo: {data.field}, valor: {value}");
                            // Escapar a string para JavaScript
                            string escapedValue = value.Replace("\\", "\\\\").Replace("'", "\\'");
                            string responseScript = $@"
                                (function() {{
                                    const field = document.getElementById('{data.field}');
                                    if (field) {{
                                        field.value = '{escapedValue}';
                                        console.log('Campo {data.field} preenchido automaticamente.');
                                    }} else {{
                                        console.log('Campo {data.field} não encontrado ao tentar preencher.');
                                    }}
                                }})();";

                            _ = WebViewControl.ExecuteScriptAsync(responseScript);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao processar mensagem web: {ex.Message}");
            }
        }
        private class FullScreenMessage
        {
            public string type { get; set; }
            public bool isFullscreen { get; set; }
        }
        private class SaveCredentialsMessage
        {
            public string type { get; set; }
            public string matricula { get; set; }
            public string senha { get; set; }
        }
        private class GetCredentialsMessage
        {
            public string type { get; set; }
            public string field { get; set; }
        }
    }
}