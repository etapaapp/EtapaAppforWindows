using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using WinRT.Interop;
using Microsoft.UI.Dispatching;

namespace EtapaApp
{
    public static class DispatcherQueueExtensions
    {
        public static async Task TryEnqueueAsync(this DispatcherQueue dispatcher, Func<Task> task)
        {
            var tcs = new TaskCompletionSource<bool>();
            dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await task();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            await tcs.Task;
        }
    }

    public sealed partial class MainWindow : Window
    {
        // --- Propriedades e Constantes ---
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;
        private const uint WM_SETICON = 0x0080;
        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint LR_DEFAULTSIZE = 0x00000040;

        private const string AUTH_CHECK_URL = "https://areaexclusiva.colegioetapa.com.br/provas/notas";
        private const string PROFILE_URL = "https://areaexclusiva.colegioetapa.com.br/profile";
        private const string PROFILE_IMAGE_FILENAME = "profile_image.jpg";
        private const string PROFILE_DATA_FILENAME = "profile_data.json";

        private UserProfileData _currentProfile;
        private NavigationViewItem _profileMenuItem;
        private PersonPicture _profilePicture;
        private bool _isLoadingProfile = false;
        private bool _isMenuExpanded = false;
        private BitmapImage _cachedProfileImage;

        private System.Threading.Timer _connectivityTimer;
        private readonly object _connectivityLock = new object();
        public static bool CurrentDialogActive = false;
        public static bool IsOffline = false;

        private WebViewPage _currentWebViewPage;

        private WebViewPage _fullscreenWebViewPage;
        private Frame _originalWebViewParentFrame;
        private int _tabIdCounter = 0;

        private string _lastOpenedUrlFromWebView = string.Empty;
        private DateTime _lastOpenedUrlTimestamp;


        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = "EtapaApp";
            this.Closed += MainWindow_Closed;

            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            UpdateTitleBarTheme(hWnd);
            RootGrid.ActualThemeChanged += (s, e) => UpdateTitleBarTheme(hWnd);
            RootGrid.ActualThemeChanged += async (s, e) =>
            {
                if (_currentProfile != null)
                    await UpdateProfileUIAsync(_currentProfile);
            };

            this.Activated += MainWindow_Activated;

            // *** MELHORIA ***
            // Adiciona um manipulador de eventos para atualizar o estado da aba "Início" sempre que a coleção de abas mudar.
            MainTabView.TabItemsChanged += (s, e) => UpdateHomeTabClosableState();

            InitializeOfflineSystem();
            InitializeProfileSystem();
            CheckWebViewInitialization();

            NavView.PaneOpening += async (s, e) =>
            {
                _isMenuExpanded = true;
                if (!_isLoadingProfile && !IsOffline)
                {
                    await UpdateProfileDataAsync();
                }
            };
            NavView.PaneClosing += (s, e) =>
            {
                _isMenuExpanded = false;
            };
        }

        #region Sistema de Abas e Navegação

        /// <summary>
        /// *** MELHORIA ***
        /// Controla se a aba "Início" pode ser fechada.
        /// Ela só pode ser fechada se for a única aba aberta.
        /// </summary>
        private void UpdateHomeTabClosableState()
        {
            // Encontra a aba "Início" usando sua tag base "home".
            var homeTab = MainTabView.TabItems
                .OfType<TabViewItem>()
                .FirstOrDefault(tab => (tab.Tag as string)?.StartsWith("home_") ?? false);

            if (homeTab != null)
            {
                // Permite fechar a aba "Início" somente se ela for a única aba.
                homeTab.IsClosable = MainTabView.TabItems.Count == 1;
            }
        }

        private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                return;
            }

            if (args.InvokedItemContainer is NavigationViewItem selectedItem)
            {
                string tag = selectedItem.Tag?.ToString();
                if (string.IsNullOrEmpty(tag)) return;

                if (tag == "profile_header")
                {
                    if (!IsOffline) _ = UpdateProfileDataAsync();
                    return;
                }

                // Sempre abre uma nova aba ao invocar um item do menu
                AddNewTab(selectedItem.Content.ToString(), tag);
            }
        }

        private void AddNewTab(string title, string baseTag, bool isClosable = true)
        {
            var newTab = new TabViewItem
            {
                Tag = $"{baseTag}_{++_tabIdCounter}",
                Header = title,
                IsClosable = isClosable
            };

            var frame = new Frame();
            newTab.Content = frame;

            Type pageType = GetPageTypeForTag(baseTag);
            if (pageType != null)
            {
                if (pageType == typeof(WebViewPage))
                {
                    var url = (Uri.TryCreate(baseTag, UriKind.Absolute, out _)) ? baseTag : GetUrlForTag(baseTag);
                    frame.Navigate(typeof(WebViewPage), url);
                }
                else
                {
                    frame.Navigate(pageType);
                }
            }

            MainTabView.TabItems.Add(newTab);
            MainTabView.SelectedItem = newTab;
        }

        private void MainTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Item is TabViewItem tabItem && tabItem.Content is Frame frame)
            {
                if (frame.Content is WebViewPage webViewPage)
                {
                    webViewPage.CanGoBackChanged -= OnCanGoBackChanged;
                    webViewPage.NewTabRequested -= OnNewTabRequestedFromWebView;
                    webViewPage.FullScreenEntered -= OnFullScreenEntered;
                    webViewPage.FullScreenExited -= OnFullScreenExited;
                    webViewPage.DisposeWebViewCode();
                }
            }

            sender.TabItems.Remove(args.Item);

            // Se a última aba foi fechada, fecha o aplicativo
            if (sender.TabItems.Count == 0)
            {
                this.Close();
            }
        }

        private void MainTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_currentWebViewPage != null)
            {
                _currentWebViewPage.CanGoBackChanged -= OnCanGoBackChanged;
                _currentWebViewPage.NewTabRequested -= OnNewTabRequestedFromWebView;
                _currentWebViewPage.FullScreenEntered -= OnFullScreenEntered;
                _currentWebViewPage.FullScreenExited -= OnFullScreenExited;
            }

            if (MainTabView.SelectedItem is TabViewItem selectedTab && selectedTab.Content is Frame frame)
            {
                if (frame.Content is WebViewPage webViewPage)
                {
                    _currentWebViewPage = webViewPage;
                    _currentWebViewPage.CanGoBackChanged += OnCanGoBackChanged;
                    _currentWebViewPage.NewTabRequested += OnNewTabRequestedFromWebView;
                    _currentWebViewPage.FullScreenEntered += OnFullScreenEntered;
                    _currentWebViewPage.FullScreenExited += OnFullScreenExited;
                }
                else
                {
                    _currentWebViewPage = null;
                }
            }
            else
            {
                _currentWebViewPage = null;
            }

            UpdateBackButtonState();
        }

        private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            _currentWebViewPage?.GoBack();
        }

        private void OnCanGoBackChanged(object sender, bool canGoBack)
        {
            DispatcherQueue.TryEnqueue(UpdateBackButtonState);
        }

        private void UpdateBackButtonState()
        {
            NavView.IsBackEnabled = _currentWebViewPage?.CanGoBack ?? false;
        }

        #endregion

        #region Handlers de Eventos da WebViewPage

        private void OnNewTabRequestedFromWebView(object sender, string url)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (url == _lastOpenedUrlFromWebView && (DateTime.Now - _lastOpenedUrlTimestamp).TotalSeconds < 2)
                {
                    Debug.WriteLine($"Ignorando solicitação de aba duplicada para: {url}");
                    return;
                }

                _lastOpenedUrlFromWebView = url;
                _lastOpenedUrlTimestamp = DateTime.Now;

                string title = "Carregando...";
                try
                {
                    var uri = new Uri(url);
                    title = uri.Host;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Não foi possível criar Uri de {url}: {ex.Message}");
                }
                AddNewTab(title, url);
            });
        }

        private void OnFullScreenEntered(object sender, EventArgs e)
        {
            if (sender is not WebViewPage webViewPage) return;
            var appWindow = this.AppWindow;
            if (appWindow == null) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

                _fullscreenWebViewPage = webViewPage;
                _originalWebViewParentFrame = webViewPage.Parent as Frame;

                if (_originalWebViewParentFrame != null)
                {
                    _originalWebViewParentFrame.Content = null;
                }

                FullScreenContainer.Content = _fullscreenWebViewPage;
                FullScreenContainer.Visibility = Visibility.Visible;

                NavView.Visibility = Visibility.Collapsed;
            });
        }

        private void OnFullScreenExited(object sender, EventArgs e)
        {
            var appWindow = this.AppWindow;
            if (appWindow == null) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                appWindow.SetPresenter(AppWindowPresenterKind.Default);

                NavView.Visibility = Visibility.Visible;

                FullScreenContainer.Visibility = Visibility.Collapsed;
                FullScreenContainer.Content = null;

                if (_originalWebViewParentFrame != null && _fullscreenWebViewPage != null)
                {
                    _originalWebViewParentFrame.Content = _fullscreenWebViewPage;
                }

                _fullscreenWebViewPage = null;
                _originalWebViewParentFrame = null;
            });
        }

        #endregion

        #region Métodos de Mapeamento e Configuração

        private Type GetPageTypeForTag(string tag)
        {
            switch (tag)
            {
                case "notas": return typeof(NotasPage);
                case "horario": return typeof(HorarioAulaPage);
                case "calendario": return typeof(CalendarioPage);
                case "provas_antigas": return typeof(ProvasAntigasPage);
                case "ead_antigo": return typeof(EadAntigoPage);
                default:
                    return IsWebViewTag(tag) ? typeof(WebViewPage) : null;
            }
        }

        private bool IsWebViewTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;

            if (Uri.TryCreate(tag, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                return true;
            }

            string[] webViewTags = {
                "home", "boletins_simulados", "cardapio", "ead","detalhes_provas",
                "enviar_redacao", "info_escreve_etapa","link_enem", "minhas_redacoes",
                "etapa_digital", "etapa_link", "material_complementar",
                "perfil", "provas_gabaritos", "redacao_semanal", "relatorio_evolucao"
            };
            return webViewTags.Contains(tag);
        }

        private string GetUrlForTag(string tag)
        {
            switch (tag)
            {
                case "home": return "https://areaexclusiva.colegioetapa.com.br/home";
                case "boletins_simulados": return "https://areaexclusiva.colegioetapa.com.br/provas/boletins-simulados";
                case "cardapio": return "https://areaexclusiva.colegioetapa.com.br/cardapio";
                case "detalhes_provas": return "https://areaexclusiva.colegioetapa.com.br/provas/detalhes";
                case "ead": return "https://areaexclusiva.colegioetapa.com.br/ead/";
                case "enviar_redacao": return "https://areaexclusiva.colegioetapa.com.br/escreve-etapa/enviar-redacao";
                case "info_escreve_etapa": return "https://areaexclusiva.colegioetapa.com.br/escreve-etapa/informacoes";
                case "minhas_redacoes": return "https://areaexclusiva.colegioetapa.com.br/escreve-etapa/minhas-redacoes";
                case "link_enem": return "https://areaexclusiva.colegioetapa.com.br/link-enem";
                case "etapa_digital": return "https://areaexclusiva.colegioetapa.com.br/etapa-digital";
                case "etapa_link": return "https://areaexclusiva.colegioetapa.com.br/etapa-link";
                case "material_complementar": return "https://areaexclusiva.colegioetapa.com.br/material-complementar";
                case "perfil": return "https://areaexclusiva.colegioetapa.com.br/profile";
                case "provas_gabaritos": return "https://areaexclusiva.colegioetapa.com.br/provas/provas-gabaritos";
                case "redacao_semanal": return "https://areaexclusiva.colegioetapa.com.br/redacao";
                case "relatorio_evolucao": return "https://areaexclusiva.colegioetapa.com.br/provas/relatorio-evolucao";
                default: return string.Empty;
            }
        }

        #endregion

        #region Métodos Iniciais e de Ciclo de Vida

        private async void CheckWebViewInitialization()
        {
            Debug.WriteLine("🚀 [Init] Iniciando CheckWebViewInitialization...");
            bool webViewReady = await WaitForWebViewReadyAsync(100, 200);
            Debug.WriteLine($"🚀 [Init] WebView Ready: {webViewReady}");

            if (webViewReady)
            {
                Debug.WriteLine("✅ [Init] WebView pronto, configurando aba inicial...");
                await Task.Delay(500);

                // A lógica de fechamento da aba inicial agora é controlada dinamicamente por UpdateHomeTabClosableState.
                AddNewTab("Início", "home", isClosable: true);

                Debug.WriteLine("✅ [Init] Inicialização completa");
            }
            else
            {
                Debug.WriteLine("❌ [Init] WebView2 não conseguiu inicializar - agendando nova tentativa");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000);
                    DispatcherQueue.TryEnqueue(() => CheckWebViewInitialization());
                });
            }
        }

        private async Task<bool> WaitForWebViewReadyAsync(int maxAttempts = 50, int delayMs = 100)
        {
            int attempts = 0;
            while (!App.WebViewEnvironmentReady && attempts < maxAttempts)
            {
                await Task.Delay(delayMs);
                attempts++;
            }
            return App.WebViewEnvironmentReady;
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _connectivityTimer?.Dispose();
        }

        #endregion

        #region Sistema de Perfil e Offline

        private void InitializeOfflineSystem()
        {
            StartConnectivityMonitoring();
        }

        private void StartConnectivityMonitoring()
        {
            _ = Task.Run(async () => await CheckConnectivityAsync());
            _connectivityTimer = new System.Threading.Timer(
                async _ => await CheckConnectivityAsync(),
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10)
            );
        }

        private async Task CheckConnectivityAsync()
        {
            bool isOnline = await IsInternetAvailableAsync();
            lock (_connectivityLock)
            {
                bool previousState = IsOffline;
                IsOffline = !isOnline;

                if (previousState && isOnline)
                {
                    Debug.WriteLine("🌐 Conexão restaurada - tentando atualizar perfil");
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        await Task.Delay(2000);
                        if (App.WebViewEnvironmentReady)
                        {
                            await UpdateProfileDataAsync();
                        }
                    });
                }
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                Debug.WriteLine($"🔄 Status de conectividade: {(IsOffline ? "Offline" : "Online")}");
            });
        }

        private async Task<bool> IsInternetAvailableAsync()
        {
            try
            {
                if (!NetworkInterface.GetIsNetworkAvailable()) return false;
                using (var ping = new Ping())
                {
                    var reply = await ping.SendPingAsync("8.8.8.8", 2000);
                    if (reply.Status == IPStatus.Success) return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Connectivity check failed: {ex.Message}");
                return false;
            }
        }

        private void InitializeProfileSystem()
        {
            CreateProfileMenuItem();
            DispatcherQueue.TryEnqueue(async () =>
            {
                Debug.WriteLine("🚀 Iniciando carregamento do perfil...");
                await LoadCachedProfileDataAsync();
                await Task.Delay(100);
                await LoadCachedProfileImageAsync();
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000);
                    Debug.WriteLine($"🔍 [Init] WebViewEnvironmentReady: {App.WebViewEnvironmentReady}");
                    Debug.WriteLine($"🔍 [Init] IsOffline: {IsOffline}");
                    if (App.WebViewEnvironmentReady && !IsOffline)
                    {
                        Debug.WriteLine("🌐 Iniciando atualização online...");
                        await UpdateProfileDataAsync();
                    }
                    else
                    {
                        Debug.WriteLine("🔍 [Init] Condições não atendidas para atualização");
                    }
                });
            });
        }

        private async void ProfileMenuItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (!_isLoadingProfile && !IsOffline)
            {
                await UpdateProfileDataAsync();
            }
        }

        private async Task LoadCachedProfileDataAsync()
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                bool fileExists = await FileExistsAsync(localFolder, PROFILE_DATA_FILENAME);
                if (!fileExists)
                {
                    Debug.WriteLine("📂 Dados do perfil não encontrados no cache.");
                    _currentProfile = GetDefaultProfileData();
                    await UpdateProfileUIAsync(_currentProfile);
                    return;
                }
                try
                {
                    var profileFile = await localFolder.GetFileAsync(PROFILE_DATA_FILENAME);
                    var profileJson = await FileIO.ReadTextAsync(profileFile);
                    _currentProfile = JsonSerializer.Deserialize<UserProfileData>(profileJson);
                    if (_currentProfile != null && IsProfileDataValid(_currentProfile))
                    {
                        await UpdateProfileUIAsync(_currentProfile);
                        Debug.WriteLine($"Dados do perfil carregados do cache: {_currentProfile.Aluno}");
                    }
                    else
                    {
                        Debug.WriteLine("📂 Dados do perfil no cache são inválidos");
                        _currentProfile = GetDefaultProfileData();
                        await UpdateProfileUIAsync(_currentProfile);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"📂 Erro ao carregar dados do cache: {ex.Message}");
                    _currentProfile = GetDefaultProfileData();
                    await UpdateProfileUIAsync(_currentProfile);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading cached profile: {ex.Message}");
                _currentProfile = GetDefaultProfileData();
                await UpdateProfileUIAsync(_currentProfile);
            }
        }

        private async Task LoadCachedProfileImageAsync()
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                bool fileExists = await FileExistsAsync(localFolder, PROFILE_IMAGE_FILENAME);
                if (!fileExists)
                {
                    Debug.WriteLine("📂 Imagem de perfil não encontrada no cache");
                    SetInitialsAsFallback();
                    return;
                }
                StorageFile imageFile = null;
                try
                {
                    imageFile = await localFolder.GetFileAsync(PROFILE_IMAGE_FILENAME);
                    Debug.WriteLine($"📂 Arquivo de imagem encontrado: {imageFile.Name}");
                    var properties = await imageFile.GetBasicPropertiesAsync();
                    if (properties.Size == 0)
                    {
                        Debug.WriteLine("📂 Arquivo de imagem vazio, removendo...");
                        try { await imageFile.DeleteAsync(); }
                        catch (Exception deleteEx) { Debug.WriteLine($"📂 Erro ao deletar arquivo vazio: {deleteEx.Message}"); }
                        SetInitialsAsFallback();
                        return;
                    }
                    using (var stream = await imageFile.OpenReadAsync())
                    {
                        var bitmap = new BitmapImage();
                        await bitmap.SetSourceAsync(stream);
                        _cachedProfileImage = bitmap;
                        Debug.WriteLine("📂 ✅ Imagem de perfil carregada do cache");
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_profilePicture != null)
                            {
                                _profilePicture.ProfilePicture = _cachedProfileImage;
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"📂 Erro ao carregar imagem do cache: {ex.Message}");
                    SetInitialsAsFallback();
                    if (imageFile != null)
                    {
                        try { await imageFile.DeleteAsync(); }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Erro geral ao carregar imagem do cache: {ex.Message}");
                SetInitialsAsFallback();
            }
        }

        private async Task UpdateProfileDataAsync()
        {
            if (_isLoadingProfile || IsOffline)
            {
                Debug.WriteLine($"🚫 UpdateProfileDataAsync bloqueado - Loading: {_isLoadingProfile}, Offline: {IsOffline}");
                return;
            }
            Debug.WriteLine("🔄 Iniciando UpdateProfileDataAsync...");
            _isLoadingProfile = true;
            try
            {
                Debug.WriteLine("📊 Exibindo loading...");
                ShowProfileLoading(true);
                Debug.WriteLine("🌐 Chamando FetchProfileDataAsync...");
                var profileData = await FetchProfileDataAsync();
                if (profileData != null && IsProfileDataValid(profileData))
                {
                    Debug.WriteLine($"✅ Dados válidos obtidos: {profileData.Aluno}");
                    _currentProfile = profileData;
                    await SaveProfileDataAsync(profileData);
                    await UpdateProfileUIAsync(profileData);
                    await FetchAndSaveProfileImageAsync();
                }
                else
                {
                    Debug.WriteLine("⚠️ Dados inválidos ou nulos, usando fallback");
                    if (_currentProfile == null || !IsProfileDataValid(_currentProfile))
                        _currentProfile = GetDefaultProfileData();
                    await UpdateProfileUIAsync(_currentProfile);
                    if (_cachedProfileImage == null)
                        await LoadCachedProfileImageAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Erro em UpdateProfileDataAsync: {ex.Message}");
                if (_currentProfile == null || !IsProfileDataValid(_currentProfile))
                    _currentProfile = GetDefaultProfileData();
                await UpdateProfileUIAsync(_currentProfile);
                if (_cachedProfileImage == null)
                    await LoadCachedProfileImageAsync();
            }
            finally
            {
                Debug.WriteLine("🏁 Finalizando UpdateProfileDataAsync...");
                _isLoadingProfile = false;
                ShowProfileLoading(false);
            }
        }

        private void ShowProfileLoading(bool isLoading)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_profileMenuItem.Content is StackPanel stackPanel && stackPanel.Children[1] is ProgressRing progressRing)
                {
                    progressRing.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
                }
            });
        }

        private async Task<bool> CheckAuthenticationAsync(WebView2 webView, string currentUrl)
        {
            Debug.WriteLine("🔐 [Auth] Iniciando verificação de autenticação...");
            try
            {
                if (webView?.CoreWebView2 == null)
                {
                    Debug.WriteLine("🔐 [Auth] ERRO: WebView ou CoreWebView2 é null");
                    return false;
                }
                var authScript = @"
                (function() {
                    try {
                        setTimeout(function() {
                            const profileElements = document.querySelectorAll('[class*=""profile""], [id*=""profile""]');
                            if (profileElements.length > 0) return true;
                            const alunoElement = document.querySelector('span.d-none.d-md-inline');
                            if (alunoElement && alunoElement.textContent.trim() !== '') return true;
                            const logoutButton = document.querySelector('a[href*=""logout""]');
                            return !!logoutButton;
                        }, 500);
                        return false;
                    } catch(e) {
                        console.error('Erro na verificação: ' + e);
                        return false;
                    }
                })();";
                await Task.Delay(4000);
                try
                {
                    var result = await webView.CoreWebView2.ExecuteScriptAsync(authScript);
                    Debug.WriteLine($"🔐 [Auth] Resultado: {result}");
                    bool isAuthenticated = result?.Trim('"')?.ToLower() == "true";
                    if (!isAuthenticated)
                    {
                        Debug.WriteLine("🔐 [Auth] Primeira tentativa falhou, tentando novamente...");
                        await Task.Delay(2000);
                        var simpleCheck = @"
                        (function() {
                            try {
                                const loginForm = document.querySelector('form[action*=""login""]');
                                const loginInput = document.querySelector('input[name=""email""], input[name=""username""]');
                                return !loginForm && !loginInput;
                            } catch(e) { return false; }
                        })();";
                        var secondResult = await webView.CoreWebView2.ExecuteScriptAsync(simpleCheck);
                        isAuthenticated = secondResult?.Trim('"')?.ToLower() == "true";
                        Debug.WriteLine($"🔐 [Auth] Segunda tentativa: {isAuthenticated}");
                    }
                    return isAuthenticated;
                }
                catch (Exception scriptEx)
                {
                    Debug.WriteLine($"🔐 [Auth] Erro ao executar script: {scriptEx.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"🔐 [Auth] ERRO geral: {ex.Message}");
                return false;
            }
        }

        private async Task<UserProfileData> FetchProfileDataAsync()
        {
            Debug.WriteLine("🔍 [FetchProfile] Iniciando FetchProfileDataAsync");
            if (IsOffline)
            {
                Debug.WriteLine("🔍 [FetchProfile] BLOQUEADO: IsOffline = true");
                return null;
            }
            var webViewReady = await WaitForWebViewReadyAsync();
            Debug.WriteLine($"🔍 [FetchProfile] WebView Ready: {webViewReady}");
            if (!webViewReady)
            {
                Debug.WriteLine("🔍 [FetchProfile] BLOQUEADO: WebView2 não está pronto");
                return null;
            }
            var tcs = new TaskCompletionSource<UserProfileData>();
            await DispatcherQueue.TryEnqueueAsync(async () =>
            {
                WebView2 profileWebView = null;
                try
                {
                    profileWebView = await CreateAndInitializeWebViewAsync();
                    if (profileWebView == null)
                    {
                        Debug.WriteLine("🔍 [FetchProfile] ERRO: Falha na criação do WebView2");
                        tcs.SetResult(null);
                        return;
                    }
                    Debug.WriteLine("🔍 [FetchProfile] ✅ WebView2 criado e pronto para uso");
                    bool waitingProfilePage = false;
                    profileWebView.NavigationCompleted += async (s, e) =>
                    {
                        try
                        {
                            if (s is not WebView2 webView || webView.Source == null)
                            {
                                Debug.WriteLine("🔍 [FetchProfile] ERRO: WebView ou Source é null no NavigationCompleted");
                                tcs.TrySetResult(null);
                                return;
                            }
                            var currentUrl = webView.Source.ToString();
                            Debug.WriteLine($"🔍 [FetchProfile] Navegação completada: {currentUrl}");
                            Debug.WriteLine($"🔍 [FetchProfile] Navigation Success: {e.IsSuccess}");
                            if (!e.IsSuccess)
                            {
                                Debug.WriteLine($"🔍 [FetchProfile] ERRO na navegação: {e.WebErrorStatus}");
                                tcs.TrySetResult(null);
                                return;
                            }
                            if (!waitingProfilePage && currentUrl.Contains("provas/notas"))
                            {
                                Debug.WriteLine("🔍 [FetchProfile] Na página de notas, verificando autenticação...");
                                bool isAuthenticated = await CheckAuthenticationAsync(webView, currentUrl);
                                Debug.WriteLine($"🔍 [FetchProfile] Resultado da autenticação: {isAuthenticated}");
                                if (isAuthenticated)
                                {
                                    waitingProfilePage = true;
                                    Debug.WriteLine("🔍 [FetchProfile] Navegando para página de perfil...");
                                    webView.Source = new Uri(PROFILE_URL);
                                }
                                else
                                {
                                    Debug.WriteLine("🔍 [FetchProfile] Usuário não autenticado - retornando dados padrão");
                                    var defaultData = new UserProfileData { Aluno = "Faça login no site para obter os dados", Matrícula = "Clique para atualizar após login", Unidade = "--", Período = "--", Sala = "--", Grau = "--", SérieAno = "--", NúmeroChamada = "--" };
                                    tcs.TrySetResult(defaultData);
                                }
                                return;
                            }
                            if (waitingProfilePage && currentUrl.Contains("profile"))
                            {
                                Debug.WriteLine("🔍 [FetchProfile] Na página de perfil, extraindo dados...");
                                await Task.Delay(4000);
                                try
                                {
                                    var extractScript = ExtractProfileScript;
                                    Debug.WriteLine("🔍 [FetchProfile] Executando script de extração...");
                                    var result = await webView.CoreWebView2.ExecuteScriptAsync(extractScript);
                                    Debug.WriteLine($"🔍 [FetchProfile] Raw script result: {result}");
                                    if (string.IsNullOrWhiteSpace(result) || result == "\"\"")
                                    {
                                        Debug.WriteLine("🔍 [FetchProfile] Script retornou vazio - tentando fallback");
                                        result = await webView.CoreWebView2.ExecuteScriptAsync("document.body.innerText");
                                        Debug.WriteLine($"🔍 [FetchProfile] Fallback result: {result?.Substring(0, Math.Min(100, result.Length))}...");
                                    }
                                    if (string.IsNullOrEmpty(result) || result == "null")
                                    {
                                        Debug.WriteLine("🔍 [FetchProfile] Resultado final vazio após fallback");
                                        tcs.TrySetResult(null);
                                        return;
                                    }
                                    Debug.WriteLine($"🔍 [FetchProfile] Script raw result: {result}");
                                    if (string.IsNullOrEmpty(result) || result == "null")
                                    {
                                        Debug.WriteLine("🔍 [FetchProfile] Script retornou resultado vazio");
                                        tcs.TrySetResult(null);
                                        return;
                                    }
                                    result = result?.Trim('"')?.Replace("\\\"", "\"")?.Replace("\\n", "")?.Replace("\\t", "");
                                    Debug.WriteLine($"🔍 [FetchProfile] Script cleaned result: {result}");
                                    Dictionary<string, string> dict = null;
                                    try
                                    {
                                        dict = JsonSerializer.Deserialize<Dictionary<string, string>>(result);
                                    }
                                    catch (JsonException jsonEx)
                                    {
                                        Debug.WriteLine($"🔍 [FetchProfile] ERRO ao deserializar JSON: {jsonEx.Message}");
                                        tcs.TrySetResult(null);
                                        return;
                                    }
                                    if (dict == null)
                                    {
                                        Debug.WriteLine("🔍 [FetchProfile] ERRO: dict é null após deserialização");
                                        tcs.TrySetResult(null);
                                        return;
                                    }
                                    Debug.WriteLine($"🔍 [FetchProfile] Parsed dict count: {dict.Count}");
                                    foreach (var kvp in dict)
                                    {
                                        Debug.WriteLine($"🔍 [FetchProfile] {kvp.Key}: '{kvp.Value}'");
                                    }
                                    var essentialFields = new[] { "Aluno", "Matrícula", "Unidade" };
                                    int validFields = essentialFields.Count(f => dict.ContainsKey(f) && !string.IsNullOrWhiteSpace(dict[f]) && dict[f] != "--");
                                    Debug.WriteLine($"🔍 [FetchProfile] Valid fields count: {validFields}");
                                    if (validFields >= 2)
                                    {
                                        var profileData = new UserProfileData { Aluno = dict.GetValueOrDefault("Aluno") ?? "", Matrícula = dict.GetValueOrDefault("Matrícula") ?? "", Unidade = dict.GetValueOrDefault("Unidade") ?? "", Período = dict.GetValueOrDefault("Período") ?? "", Sala = dict.GetValueOrDefault("Sala") ?? "", Grau = dict.GetValueOrDefault("Grau") ?? "", SérieAno = dict.GetValueOrDefault("Série/Ano") ?? "", NúmeroChamada = dict.GetValueOrDefault("Nº chamada") ?? "" };
                                        Debug.WriteLine($"🔍 [FetchProfile] ✅ Dados válidos criados: {profileData.Aluno}");
                                        tcs.TrySetResult(profileData);
                                    }
                                    else
                                    {
                                        Debug.WriteLine("🔍 [FetchProfile] ❌ Dados insuficientes");
                                        tcs.TrySetResult(null);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"🔍 [FetchProfile] ERRO na extração: {ex.Message}");
                                    tcs.TrySetResult(null);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"🔍 [FetchProfile] ERRO geral no NavigationCompleted: {ex.Message}");
                            tcs.TrySetResult(null);
                        }
                    };
                    Debug.WriteLine($"🔍 [FetchProfile] Iniciando navegação para: {AUTH_CHECK_URL}");
                    profileWebView.Source = new Uri(AUTH_CHECK_URL);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"🔍 [FetchProfile] ERRO CRÍTICO na UI thread: {ex.Message}");
                    tcs.TrySetResult(null);
                }
                finally
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5000);
                        await DispatcherQueue.TryEnqueueAsync(async () =>
                        {
                            try
                            {
                                if (profileWebView != null && RootGrid != null && RootGrid.Children.Contains(profileWebView))
                                {
                                    Debug.WriteLine("🔍 [FetchProfile] Removendo WebView do RootGrid no cleanup diferido");
                                    RootGrid.Children.Remove(profileWebView);
                                }
                            }
                            catch (Exception cleanupEx)
                            {
                                Debug.WriteLine($"🔍 [FetchProfile] ERRO no cleanup diferido: {cleanupEx.Message}");
                            }
                        });
                    });
                }
            });
            var timeoutTask = Task.Delay(45000);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            if (completedTask == timeoutTask)
            {
                Debug.WriteLine("🔍 [FetchProfile] ⏰ TIMEOUT na obtenção do perfil");
                return null;
            }
            var finalResult = await tcs.Task;
            Debug.WriteLine($"🔍 [FetchProfile] Resultado final: {finalResult?.Aluno ?? "null"}");
            return finalResult;
        }

        private async Task<WebView2> CreateAndInitializeWebViewAsync()
        {
            WebView2 webView = null;
            try
            {
                Debug.WriteLine("🔧 [CreateWebView] Iniciando criação do WebView2...");
                var tcs = new TaskCompletionSource<WebView2>();
                DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        if (RootGrid == null)
                        {
                            Debug.WriteLine("🔧 [CreateWebView] ERRO: RootGrid é null");
                            tcs.SetResult(null);
                            return;
                        }
                        webView = new WebView2 { Visibility = Visibility.Collapsed, Width = 800, Height = 600 };
                        Debug.WriteLine("🔧 [CreateWebView] WebView2 criado, adicionando ao RootGrid...");
                        RootGrid.Children.Add(webView);
                        await Task.Delay(200);
                        bool initialized = false;
                        Exception lastException = null;
                        if (App.WebViewEnvironment != null && !initialized)
                        {
                            try
                            {
                                Debug.WriteLine("🔧 [CreateWebView] Tentativa 1: Usando App.WebViewEnvironment");
                                await webView.EnsureCoreWebView2Async(App.WebViewEnvironment);
                                await Task.Delay(500);
                                if (webView.CoreWebView2 != null)
                                {
                                    Debug.WriteLine("🔧 [CreateWebView] ✅ Sucesso com App.WebViewEnvironment");
                                    initialized = true;
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"🔧 [CreateWebView] Falha método 1: {ex.Message}");
                                lastException = ex;
                            }
                        }
                        if (!initialized)
                        {
                            try
                            {
                                Debug.WriteLine("🔧 [CreateWebView] Tentativa 2: Inicialização padrão");
                                await webView.EnsureCoreWebView2Async();
                                await Task.Delay(500);
                                if (webView.CoreWebView2 != null)
                                {
                                    Debug.WriteLine("🔧 [CreateWebView] ✅ Sucesso com inicialização padrão");
                                    initialized = true;
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"🔧 [CreateWebView] Falha método 2: {ex.Message}");
                                lastException = ex;
                            }
                        }
                        if (!initialized)
                        {
                            Debug.WriteLine("🔧 [CreateWebView] Tentativa 3: Retry com delays progressivos");
                            for (int i = 0; i < 3 && !initialized; i++)
                            {
                                try
                                {
                                    int delayMs = 1000 * (i + 1);
                                    await Task.Delay(delayMs);
                                    Debug.WriteLine($"🔧 [CreateWebView] Retry {i + 1}/3 após {delayMs}ms");
                                    await webView.EnsureCoreWebView2Async();
                                    await Task.Delay(1000);
                                    if (webView.CoreWebView2 != null)
                                    {
                                        Debug.WriteLine($"🔧 [CreateWebView] ✅ Sucesso no retry {i + 1}");
                                        initialized = true;
                                        break;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"🔧 [CreateWebView] Falha retry {i + 1}: {ex.Message}");
                                    lastException = ex;
                                }
                            }
                        }
                        if (!initialized)
                        {
                            Debug.WriteLine("🔧 [CreateWebView] ❌ Todos os métodos falharam");
                            if (lastException != null)
                            {
                                Debug.WriteLine($"🔧 [CreateWebView] Última exceção: {lastException}");
                                Debug.WriteLine($"🔧 [CreateWebView] Stack trace: {lastException.StackTrace}");
                            }
                            if (RootGrid.Children.Contains(webView))
                            {
                                RootGrid.Children.Remove(webView);
                            }
                            tcs.SetResult(null);
                            return;
                        }
                        try
                        {
                            var settings = webView.CoreWebView2.Settings;
                            if (settings != null)
                            {
                                settings.IsScriptEnabled = true;
                                settings.AreDefaultScriptDialogsEnabled = true;
                                settings.IsWebMessageEnabled = true;
                                settings.AreHostObjectsAllowed = false;
                                settings.IsGeneralAutofillEnabled = false;
                                settings.IsPasswordAutosaveEnabled = false;
                                Debug.WriteLine("🔧 [CreateWebView] ✅ Settings configurados");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"🔧 [CreateWebView] Erro ao configurar settings: {ex.Message}");
                        }
                        Debug.WriteLine("🔧 [CreateWebView] ✅ WebView2 criado e inicializado com sucesso");
                        tcs.SetResult(webView);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"🔧 [CreateWebView] ERRO na UI thread: {ex.Message}");
                        Debug.WriteLine($"🔧 [CreateWebView] Stack trace: {ex.StackTrace}");
                        if (webView != null && RootGrid != null && RootGrid.Children.Contains(webView))
                        {
                            RootGrid.Children.Remove(webView);
                        }
                        tcs.SetResult(null);
                    }
                });
                return await tcs.Task;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"🔧 [CreateWebView] ERRO CRÍTICO: {ex.Message}");
                Debug.WriteLine($"🔧 [CreateWebView] Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        private async Task FetchAndSaveProfileImageAsync()
        {
            if (!App.WebViewEnvironmentReady || IsOffline)
            {
                Debug.WriteLine("🖼️ [ProfileImage] Bloqueado - WebView não pronto ou offline");
                SetInitialsAsFallback();
                return;
            }
            try
            {
                Debug.WriteLine("🖼️ [ProfileImage] Iniciando busca da imagem de perfil...");
                await DispatcherQueue.TryEnqueueAsync(async () =>
                {
                    WebView2 imageWebView = null;
                    try
                    {
                        imageWebView = await CreateImageWebViewAsync();
                        if (imageWebView == null)
                        {
                            Debug.WriteLine("🖼️ [ProfileImage] Falha ao criar WebView para imagem");
                            SetInitialsAsFallback();
                            return;
                        }
                        var tcs = new TaskCompletionSource<string>();
                        try
                        {
                            imageWebView.NavigationCompleted += async (s, e) =>
                            {
                                try
                                {
                                    if (e.IsSuccess && s is WebView2 webView && webView.CoreWebView2 != null)
                                    {
                                        await Task.Delay(2000);
                                        var imageScript = @"
                                        (function() {
                                            try {
                                                const selectors = [
                                                    'div.d-flex.justify-content-center img.rounded-circle',
                                                    '.profile-image img', '.avatar img', 'img.rounded-circle',
                                                    '.user-avatar img', '.profile-photo img'
                                                ];
                                                for (let selector of selectors) {
                                                    const imgElement = document.querySelector(selector);
                                                    if (imgElement && imgElement.src && imgElement.src.startsWith('http')) {
                                                        return imgElement.src;
                                                    }
                                                }
                                                return '';
                                            } catch (e) { return 'ERROR: ' + e.message; }
                                        })();";
                                        var imageUrl = await webView.CoreWebView2.ExecuteScriptAsync(imageScript);
                                        imageUrl = imageUrl?.Trim('"') ?? "";
                                        Debug.WriteLine($"🖼️ [ProfileImage] URL da imagem extraída: {imageUrl}");
                                        tcs.TrySetResult(imageUrl);
                                    }
                                    else
                                    {
                                        Debug.WriteLine($"🖼️ [ProfileImage] Navegação falhou: {e.WebErrorStatus}");
                                        tcs.TrySetResult("");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"🖼️ [ProfileImage] Erro na extração da URL: {ex.Message}");
                                    tcs.TrySetResult("");
                                }
                            };
                            imageWebView.Source = new Uri(PROFILE_URL);
                            var timeoutTask = Task.Delay(20000);
                            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                            string imageUrl = "";
                            if (completedTask == tcs.Task)
                            {
                                imageUrl = await tcs.Task;
                            }
                            else
                            {
                                Debug.WriteLine("🖼️ [ProfileImage] Timeout na busca da imagem");
                            }
                            if (!string.IsNullOrEmpty(imageUrl) && imageUrl.StartsWith("http") && !imageUrl.StartsWith("ERROR:"))
                            {
                                _ = Task.Run(async () => await DownloadAndSaveImageAsync(imageUrl));
                            }
                            else
                            {
                                Debug.WriteLine("🖼️ [ProfileImage] URL inválida ou vazia, usando iniciais");
                                SetInitialsAsFallback();
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"🖼️ [ProfileImage] Erro no processamento: {ex.Message}");
                            SetInitialsAsFallback();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"🖼️ [ProfileImage] Erro geral na UI thread: {ex.Message}");
                        SetInitialsAsFallback();
                    }
                    finally
                    {
                        if (imageWebView != null)
                        {
                            try { await CleanupImageWebViewAsync(imageWebView); }
                            catch (Exception cleanupEx) { Debug.WriteLine($"🖼️ [ProfileImage] Erro no cleanup: {cleanupEx.Message}"); }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"🖼️ [ProfileImage] Erro geral: {ex.Message}");
                SetInitialsAsFallback();
            }
        }

        private async Task<WebView2> CreateImageWebViewAsync()
        {
            if (RootGrid == null) return null;
            var imageWebView = new WebView2 { Visibility = Visibility.Collapsed, Width = 800, Height = 600 };
            RootGrid.Children.Add(imageWebView);
            await Task.Delay(100);
            try
            {
                bool initialized = false;
                Exception lastException = null;
                if (App.WebViewEnvironment != null && !initialized)
                {
                    try
                    {
                        await imageWebView.EnsureCoreWebView2Async(App.WebViewEnvironment);
                        await Task.Delay(500);
                        if (imageWebView.CoreWebView2 != null) initialized = true;
                    }
                    catch (Exception ex) { lastException = ex; }
                }
                if (!initialized)
                {
                    try
                    {
                        await imageWebView.EnsureCoreWebView2Async();
                        await Task.Delay(500);
                        if (imageWebView.CoreWebView2 != null) initialized = true;
                    }
                    catch (Exception ex) { lastException = ex; }
                }
                if (!initialized)
                {
                    Debug.WriteLine($"🖼️ [ProfileImage] Erro ao inicializar WebView: {lastException?.Message}");
                    RootGrid.Children.Remove(imageWebView);
                    return null;
                }
                Debug.WriteLine("🖼️ [ProfileImage] WebView criado com sucesso");
                return imageWebView;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"🖼️ [ProfileImage] Erro ao inicializar WebView: {ex.Message}");
                if (RootGrid.Children.Contains(imageWebView))
                {
                    RootGrid.Children.Remove(imageWebView);
                }
                return null;
            }
        }

        private async Task CleanupImageWebViewAsync(WebView2 imageWebView)
        {
            if (imageWebView == null) return;
            var tcs = new TaskCompletionSource<bool>();
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (RootGrid != null && RootGrid.Children.Contains(imageWebView))
                    {
                        RootGrid.Children.Remove(imageWebView);
                        Debug.WriteLine("🖼️ [ProfileImage] WebView removido com sucesso");
                    }
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"🖼️ [ProfileImage] Erro no cleanup: {ex.Message}");
                    tcs.SetResult(false);
                }
            });
            await tcs.Task;
        }

        private async Task DownloadAndSaveImageAsync(string imageUrl)
        {
            try
            {
                Debug.WriteLine($"🖼️ [Download] Iniciando download da imagem: {imageUrl}");
                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(15);
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                    var response = await httpClient.GetAsync(imageUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        var imageBytes = await response.Content.ReadAsByteArrayAsync();
                        Debug.WriteLine($"🖼️ [Download] Bytes baixados: {imageBytes.Length}");
                        if (imageBytes.Length > 100)
                        {
                            var localFolder = ApplicationData.Current.LocalFolder;
                            var imageFile = await localFolder.CreateFileAsync(PROFILE_IMAGE_FILENAME, CreationCollisionOption.ReplaceExisting);
                            await FileIO.WriteBytesAsync(imageFile, imageBytes);
                            Debug.WriteLine("🖼️ [Download] Imagem salva, carregando bitmap...");
                            await DispatcherQueue.TryEnqueueAsync(async () =>
                            {
                                try
                                {
                                    using (var stream = await imageFile.OpenReadAsync())
                                    {
                                        var bitmap = new BitmapImage();
                                        await bitmap.SetSourceAsync(stream);
                                        _cachedProfileImage = bitmap;
                                        if (_profilePicture != null)
                                        {
                                            _profilePicture.ProfilePicture = _cachedProfileImage;
                                            Debug.WriteLine("🖼️ [Download] ✅ Imagem de perfil atualizada com sucesso");
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"🖼️ [Download] Erro ao carregar bitmap: {ex.Message}");
                                    string currentName = _currentProfile?.Aluno ?? "";
                                    bool hasValidName = !string.IsNullOrWhiteSpace(currentName) && currentName != "Faça login para obter os dados" && currentName != "Faça login para exibir os dados." && currentName != "Faça Login para exibir os Dados";
                                    if (hasValidName) SetInitialsAsFallback();
                                    else { _profilePicture.Initials = ""; _profilePicture.DisplayName = ""; }
                                }
                            });
                        }
                        else
                        {
                            Debug.WriteLine("🖼️ [Download] Imagem muito pequena, usando fallback apropriado");
                            string currentName = _currentProfile?.Aluno ?? "";
                            bool hasValidName = !string.IsNullOrWhiteSpace(currentName) && currentName != "Faça login para obter os dados" && currentName != "Faça login para exibir os dados." && currentName != "Faça Login para exibir os Dados";
                            if (hasValidName) SetInitialsAsFallback();
                            else await DispatcherQueue.TryEnqueueAsync(async () => { _profilePicture.Initials = ""; _profilePicture.DisplayName = ""; });
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"🖼️ [Download] Falha no download: {response.StatusCode}");
                        string currentName = _currentProfile?.Aluno ?? "";
                        bool hasValidName = !string.IsNullOrWhiteSpace(currentName) && currentName != "Faça login para obter os dados" && currentName != "Faça login para exibir os dados." && currentName != "Faça Login para exibir os Dados";
                        if (hasValidName) SetInitialsAsFallback();
                        else await DispatcherQueue.TryEnqueueAsync(async () => { _profilePicture.Initials = ""; _profilePicture.DisplayName = ""; });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"🖼️ [Download] Erro no download/salvamento da imagem: {ex.Message}");
                string currentName = _currentProfile?.Aluno ?? "";
                bool hasValidName = !string.IsNullOrWhiteSpace(currentName) && currentName != "Faça login para obter os dados" && currentName != "Faça login para exibir os dados." && currentName != "Faça Login para exibir os Dados";
                if (hasValidName) SetInitialsAsFallback();
                else try { await DispatcherQueue.TryEnqueueAsync(async () => { _profilePicture.Initials = ""; _profilePicture.DisplayName = ""; }); } catch (Exception dispatchEx) { Debug.WriteLine($"🖼️ [Download] Erro ao definir ícone genérico: {dispatchEx.Message}"); }
            }
        }

        private void CreateProfileMenuItem()
        {
            _profilePicture = new PersonPicture() { Width = 28, Height = 28, Margin = new Thickness(2, 4, 2, 4), DisplayName = "" };
            _profileMenuItem = new NavigationViewItem() { Tag = "profile_header", IsExpanded = false };
            var stackPanel = new StackPanel() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            stackPanel.Children.Add(_profilePicture);
            var progressRing = new ProgressRing() { Width = 16, Height = 16, Margin = new Thickness(8, 0, 8, 0), Visibility = Visibility.Collapsed, IsIndeterminate = true };
            var textBlock = new TextBlock() { Text = "Faça login para obter os dados", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 200 };
            stackPanel.Children.Add(progressRing);
            stackPanel.Children.Add(textBlock);
            _profileMenuItem.Content = stackPanel;
            _profileMenuItem.Tapped += ProfileMenuItem_Tapped;
            NavView.MenuItems.Insert(0, _profileMenuItem);
        }

        private void SetInitialsAsFallback()
        {
            try
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        if (_profilePicture != null)
                        {
                            _profilePicture.ProfilePicture = null;
                            string currentName = _currentProfile?.Aluno ?? "";
                            bool hasValidName = !string.IsNullOrWhiteSpace(currentName) && currentName != "Faça login para obter os dados" && currentName != "Faça login para exibir os dados." && currentName != "Faça Login para exibir os Dados";
                            if (hasValidName)
                            {
                                _profilePicture.Initials = GetInitials(currentName);
                                _profilePicture.DisplayName = currentName;
                                Debug.WriteLine($"🖼️ [Initials] ✅ Iniciais definidas: {_profilePicture.Initials}");
                            }
                            else
                            {
                                _profilePicture.Initials = "";
                                _profilePicture.DisplayName = "";
                                Debug.WriteLine("🖼️ [Initials] ✅ Ícone genérico exibido (sem nome válido)");
                            }
                        }
                        else
                        {
                            Debug.WriteLine("🖼️ [Initials] ❌ _profilePicture é null ao definir iniciais");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"🖼️ [Initials] ❌ Erro interno ao definir iniciais: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"🖼️ [Initials] ❌ Erro ao enfileirar definição de iniciais: {ex.Message}");
            }
        }

        private async Task UpdateProfileUIAsync(UserProfileData profileData)
        {
            await DispatcherQueue.TryEnqueueAsync(async () =>
            {
                if (_profileMenuItem.Content is StackPanel stackPanel && stackPanel.Children[2] is TextBlock textBlock)
                {
                    textBlock.Text = profileData.Aluno ?? "Faça login para obter os dados";
                    textBlock.Visibility = Visibility.Visible;
                }
                _profileMenuItem.MenuItems.Clear();
                var fields = new List<(string Label, string Value)> { ("Matrícula", profileData.Matrícula ?? "--"), ("Unidade", profileData.Unidade ?? "--"), ("Período", profileData.Período ?? "--"), ("Sala", profileData.Sala ?? "--"), ("Grau", profileData.Grau ?? "--"), ("Série/Ano", profileData.SérieAno ?? "--"), ("Nº chamada", profileData.NúmeroChamada ?? "--") };
                foreach (var (label, value) in fields)
                {
                    var subItemPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 4, 0, 4) };
                    var labelText = new TextBlock { Text = $"{label}: ", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                    var valueText = new TextBlock { Text = value };
                    var textColor = RootGrid.ActualTheme == ElementTheme.Dark ? new SolidColorBrush(Microsoft.UI.Colors.White) : new SolidColorBrush(Microsoft.UI.Colors.Black);
                    labelText.Foreground = textColor;
                    valueText.Foreground = textColor;
                    subItemPanel.Children.Add(labelText);
                    subItemPanel.Children.Add(valueText);
                    _profileMenuItem.MenuItems.Add(subItemPanel);
                }
                if (!IsOffline)
                {
                    var updateButton = new Button { Content = "Atualizar", Margin = new Thickness(20, 12, 20, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
                    updateButton.Click += async (s, e) => { if (!_isLoadingProfile && !IsOffline) await UpdateProfileDataAsync(); };
                    _profileMenuItem.MenuItems.Add(updateButton);
                }
                if (_cachedProfileImage == null && _profilePicture.ProfilePicture == null)
                {
                    string currentName = profileData.Aluno ?? "";
                    bool hasValidName = !string.IsNullOrWhiteSpace(currentName) && currentName != "Faça login para obter os dados" && currentName != "Faça login para exibir os dados." && currentName != "Faça Login para exibir os Dados";
                    if (hasValidName)
                    {
                        _profilePicture.Initials = GetInitials(currentName);
                        _profilePicture.DisplayName = currentName;
                        Debug.WriteLine($"🔄 [UpdateUI] Iniciais definidas: {_profilePicture.Initials}");
                    }
                    else
                    {
                        _profilePicture.Initials = "";
                        _profilePicture.DisplayName = "";
                        Debug.WriteLine("🔄 [UpdateUI] Ícone genérico mantido");
                    }
                }
            });
        }

        private async Task SaveProfileDataAsync(UserProfileData profileData)
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                var json = JsonSerializer.Serialize(profileData, new JsonSerializerOptions { WriteIndented = true });
                var file = await localFolder.CreateFileAsync(PROFILE_DATA_FILENAME, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, json);
            }
            catch (Exception ex) { Debug.WriteLine($"Error saving profile data: {ex.Message}"); }
        }

        private UserProfileData GetDefaultProfileData() => new UserProfileData { Aluno = "Faça login para exibir os dados.", Matrícula = "--", Unidade = "--", Período = "--", Sala = "--", Grau = "--", SérieAno = "--", NúmeroChamada = "--" };

        private bool IsProfileDataValid(UserProfileData profile)
        {
            if (profile == null) return false;
            return new[] { profile.Aluno, profile.Matrícula, profile.Unidade, profile.SérieAno }.Count(f => !string.IsNullOrWhiteSpace(f) && f != "--") >= 2;
        }

        private string GetInitials(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName) || fullName == "Faça login para obter os dados") return "?";
            var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpper();
            return $"{parts[0][0]}{parts[parts.Length - 1][0]}".ToUpper();
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
        {
            this.Activated -= MainWindow_Activated;
            var hwnd = WindowNative.GetWindowHandle(this);
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                var hIconSmall = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, hIconSmall);
                var hIconBig = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, hIconBig);
            }
        }

        private void UpdateTitleBarTheme(IntPtr hWnd)
        {
            try
            {
                int darkMode = RootGrid.ActualTheme == ElementTheme.Dark ? 1 : 0;
                DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            }
            catch (Exception ex) { Console.WriteLine($"Erro ao atualizar tema da barra: {ex.Message}"); }
        }

        private async Task<bool> FileExistsAsync(StorageFolder folder, string fileName)
        {
            try
            {
                var item = await folder.TryGetItemAsync(fileName);
                return item != null;
            }
            catch { return false; }
        }

        private const string ExtractProfileScript = @"
        (function() {
            try {
                function cleanValue(text) { return text.replace(/\s+/g, ' ').trim(); }
                function extractDesktopData() {
                    const container = document.querySelector('.popover-body .mt-2');
                    if (!container) return null;
                    const items = container.querySelectorAll('p');
                    const labels = ['Aluno', 'Matrícula', 'Unidade', 'Período', 'Sala', 'Grau', 'Série/Ano', 'Nº chamada'];
                    const result = {};
                    items.forEach((item, index) => {
                        if (index < labels.length) {
                            const text = item.textContent;
                            const colonIndex = text.indexOf(':');
                            const value = colonIndex !== -1 ? text.substring(colonIndex + 1).trim() : text.trim();
                            result[labels[index]] = cleanValue(value);
                        }
                    });
                    return result;
                }
                function extractMobileData() {
                    const items = document.querySelectorAll('.navbar-nav.d-lg-none li.nav-item');
                    const result = {};
                    const labels = ['Aluno', 'Matrícula', 'Unidade', 'Período', 'Sala', 'Grau', 'Série/Ano', 'Nº chamada'];
                    items.forEach((item, index) => {
                        if (index < labels.length) {
                            const text = item.textContent.trim();
                            const colonIndex = text.indexOf(':');
                            const value = colonIndex !== -1 ? text.substring(colonIndex + 1).trim() : text.trim();
                            result[labels[index]] = cleanValue(value);
                        }
                    });
                    return result;
                }
                function extractNewProfileStructure() {
                    const result = {};
                    const alunoElement = document.querySelector('h1.card-title');
                    if (alunoElement) { result['Aluno'] = cleanValue(alunoElement.textContent); }
                    const fields = document.querySelectorAll('.card-body .row.mb-2');
                    fields.forEach(field => {
                        const labelElement = field.querySelector('.col-md-3');
                        const valueElement = field.querySelector('.col-md-9');
                        if (labelElement && valueElement) {
                            const label = cleanValue(labelElement.textContent).replace(':', '');
                            const value = cleanValue(valueElement.textContent);
                            result[label] = value;
                        }
                    });
                    return result;
                }
                const data = extractDesktopData() || extractMobileData() || extractNewProfileStructure();
                return JSON.stringify(data || {});
            } catch(e) {
                return JSON.stringify({ error: 'JS_ERROR: ' + e.message });
            }
        })();";
        #endregion
    }

    public class UserProfileData
    {
        public string Aluno { get; set; }
        public string Matrícula { get; set; }
        public string Unidade { get; set; }
        public string Período { get; set; }
        public string Sala { get; set; }
        public string Grau { get; set; }
        [JsonPropertyName("Série/Ano")]
        public string SérieAno { get; set; }
        [JsonPropertyName("Nº chamada")]
        public string NúmeroChamada { get; set; }
    }
}
