using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace EtapaApp
{
    public partial class App : Application
    {
        private Window? _window;
        private static CoreWebView2Environment? _webViewEnvironment;
        private static bool _webViewEnvironmentReady = false;
        private static readonly object _lock = new object();
        private static readonly TaskCompletionSource<bool> _initializationTcs = new TaskCompletionSource<bool>();

        // Propriedade estática para acessar o ambiente do WebView2
        public static CoreWebView2Environment? WebViewEnvironment
        {
            get
            {
                lock (_lock)
                {
                    return _webViewEnvironment;
                }
            }
        }

        public static bool WebViewEnvironmentReady
        {
            get
            {
                lock (_lock)
                {
                    return _webViewEnvironmentReady;
                }
            }
        }

        public App()
        {
            InitializeComponent();
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                // Inicializa o ambiente WebView2 ANTES de criar a janela
                await EnsureWebViewEnvironmentAsync();

                _window = new MainWindow();
                _window.Activate();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Critical error during app launch: {ex.Message}");
                // Em caso de erro crítico, ainda tenta criar a janela
                _window = new MainWindow();
                _window.Activate();
            }
        }

        public static async Task EnsureWebViewEnvironmentAsync()
        {
            // Se já está pronto, retorna imediatamente
            lock (_lock)
            {
                if (_webViewEnvironmentReady && _webViewEnvironment != null)
                    return;
            }

            // Se já está sendo inicializado por outra thread, aguarda
            if (_initializationTcs.Task.IsCompleted)
            {
                await _initializationTcs.Task;
                return;
            }

            // Se é a primeira vez, inicia a inicialização
            lock (_lock)
            {
                if (_webViewEnvironmentReady && _webViewEnvironment != null)
                    return;
            }

            try
            {
                await InitializeWebView2EnvironmentAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Failed to initialize WebView2 environment: {ex.Message}");
                _initializationTcs.TrySetException(ex);
                throw;
            }
        }

        private static async Task InitializeWebView2EnvironmentAsync()
        {
            try
            {
                Debug.WriteLine("🔄 Initializing WebView2 environment...");

                // Define pasta específica para dados do WebView2
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "EtapaApp",
                    "WebView2");

                // Garante que a pasta existe
                Directory.CreateDirectory(userDataFolder);

                // Cria ambiente compartilhado
                var environment = await CoreWebView2Environment.CreateAsync();

                lock (_lock)
                {
                    if (_webViewEnvironment == null) // Double-check locking
                    {
                        _webViewEnvironment = environment;
                        _webViewEnvironmentReady = true;
                        Debug.WriteLine("✅ WebView2 environment initialized successfully");
                        _initializationTcs.TrySetResult(true);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Critical WebView2 init error: {ex.Message}");
                lock (_lock)
                {
                    _webViewEnvironment = null;
                    _webViewEnvironmentReady = false;
                }
                _initializationTcs.TrySetException(ex);
                throw;
            }
        }

        public static CoreWebView2Environment? GetWebViewEnvironment()
        {
            lock (_lock)
            {
                return _webViewEnvironment;
            }
        }

        // Método auxiliar para inicializar WebView2 de forma segura
        public static async Task<bool> SafeEnsureCoreWebView2Async(Microsoft.UI.Xaml.Controls.WebView2 webView)
        {
            try
            {
                await EnsureWebViewEnvironmentAsync();

                if (_webViewEnvironment != null)
                {
                    await webView.EnsureCoreWebView2Async(_webViewEnvironment);
                    return true;
                }
                else
                {
                    Debug.WriteLine("❌ WebView2 environment is null");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Failed to initialize WebView2: {ex.Message}");
                return false;
            }
        }
    }
}