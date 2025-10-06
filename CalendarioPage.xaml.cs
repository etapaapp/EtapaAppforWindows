using HtmlAgilityPack;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
using Windows.Storage;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml.Navigation;

namespace EtapaApp
{
    public sealed partial class CalendarioPage : Page
    {
        private const string BaseUrl = "https://areaexclusiva.colegioetapa.com.br";
        private const string ProvasUrl = "/provas/datas";
        private const string UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_7_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.4 Safari/605.1.15";
        private const string COLUMN_WIDTH_SETTINGS_KEY = "CalendarioPage_ColumnWidth";
        private const double DEFAULT_COLUMN_WIDTH = 400.0;
        private const string PROVAS_CACHE_PREFIX = "provas_cache_mes_";
        private const string PROVAS_TIMESTAMP_PREFIX = "provas_timestamp_mes_";
        private const string DETALHES_CACHE_PREFIX = "detalhes_cache_";
        private const string DETALHES_TIMESTAMP_PREFIX = "detalhes_timestamp_";
        private List<ProvaItem> _todasAsProvas = new List<ProvaItem>();
        private readonly ObservableCollection<ProvaItem> _provasVisiveis = new ObservableCollection<ProvaItem>();
        private enum FiltroTipo { Todos, Provas, Recuperacoes }
        private FiltroTipo _filtroAtual = FiltroTipo.Todos;
        private bool _isResizing = false;
        private double _initialPointerPosition = 0;
        private double _initialColumnWidth = DEFAULT_COLUMN_WIDTH;
        private Grid _divisorGrid;

        public CalendarioPage()
        {
            this.InitializeComponent();

            // Habilita o cache da página para manter o estado ao trocar de aba.
            this.NavigationCacheMode = NavigationCacheMode.Required;

            ProvasListView.ItemsSource = _provasVisiveis;
            this.ActualThemeChanged += CalendarioPage_ActualThemeChanged;
        }

        private void CalendarioPage_ActualThemeChanged(FrameworkElement sender, object args)
        {
            UpdateDivisorColors();
            if (DetalhesGrid.Visibility == Visibility.Visible && ProvasListView.SelectedItem is ProvaItem selectedProva)
            {
                _ = this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, async () =>
                {
                    await CarregarMateriaAsync(selectedProva.Link);
                });
            }
        }

        private void UpdateDivisorColors()
        {
            if (_divisorGrid != null)
            {
                var isDarkTheme = this.ActualTheme == ElementTheme.Dark ||
                                 (this.ActualTheme == ElementTheme.Default &&
                                  Application.Current.RequestedTheme == ApplicationTheme.Dark);
                if (!_isResizing)
                {
                    _divisorGrid.Background = new SolidColorBrush(isDarkTheme ?
                        ColorHelper.FromArgb(255, 64, 64, 64) :
                        ColorHelper.FromArgb(255, 200, 200, 200));
                }
            }
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadColumnWidthAsync();

            if (MesComboBox.ItemsSource == null)
            {
                var meses = new Dictionary<int, string>();
                for (int i = 1; i <= 12; i++)
                {
                    string nomeMes = CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(i);
                    meses.Add(i, char.ToUpper(nomeMes[0]) + nomeMes.Substring(1));
                }
                MesComboBox.ItemsSource = meses;
                MesComboBox.SelectedValuePath = "Key";
                MesComboBox.DisplayMemberPath = "Value";

                if (MesComboBox.SelectedValue == null)
                {
                    MesComboBox.SelectedValue = DateTime.Now.Month;
                }
            }

            await InitializeMateriaWebView();
        }

        private async Task InitializeMateriaWebView()
        {
            try
            {
                if (MateriaWebView.CoreWebView2 == null)
                {
                    Debug.WriteLine("Inicializando WebView2 para detalhes...");
                    // --- CORREÇÃO: Voltando à lógica de inicialização original que funcionava ---
                    var env = await CoreWebView2Environment.CreateAsync();
                    await MateriaWebView.EnsureCoreWebView2Async(env);
                    MateriaWebView.DefaultBackgroundColor = Colors.Transparent;
                    Debug.WriteLine("WebView2 para detalhes inicializado com sucesso");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro na inicialização do WebView2: {ex.Message}");
            }
        }

        private async Task LoadColumnWidthAsync()
        {
            try
            {
                var localSettings = ApplicationData.Current.LocalSettings;
                if (localSettings.Values.ContainsKey(COLUMN_WIDTH_SETTINGS_KEY))
                {
                    var savedWidth = (double)localSettings.Values[COLUMN_WIDTH_SETTINGS_KEY];
                    var grid = (Grid)this.Content;
                    if (grid != null && grid.ColumnDefinitions.Count > 0)
                    {
                        var minWidth = 320;
                        var maxWidth = 800;
                        savedWidth = Math.Max(minWidth, Math.Min(maxWidth, savedWidth));
                        grid.ColumnDefinitions[0].Width = new GridLength(savedWidth);
                        _initialColumnWidth = savedWidth;
                    }
                }
                else
                {
                    var grid = (Grid)this.Content;
                    if (grid != null && grid.ColumnDefinitions.Count > 0)
                    {
                        grid.ColumnDefinitions[0].Width = new GridLength(DEFAULT_COLUMN_WIDTH);
                        _initialColumnWidth = DEFAULT_COLUMN_WIDTH;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Erro ao carregar largura da coluna: {ex.Message}");
                var grid = (Grid)this.Content;
                if (grid != null && grid.ColumnDefinitions.Count > 0)
                {
                    grid.ColumnDefinitions[0].Width = new GridLength(DEFAULT_COLUMN_WIDTH);
                    _initialColumnWidth = DEFAULT_COLUMN_WIDTH;
                }
            }
        }

        private async Task SaveColumnWidthAsync(double width)
        {
            try
            {
                var localSettings = ApplicationData.Current.LocalSettings;
                localSettings.Values[COLUMN_WIDTH_SETTINGS_KEY] = width;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Erro ao salvar largura da coluna: {ex.Message}");
            }
        }

        private bool IsOnline()
        {
            var connectionProfile = NetworkInformation.GetInternetConnectionProfile();
            return connectionProfile != null &&
                   connectionProfile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
        }

        private async void MesComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MesComboBox.SelectedValue != null)
            {
                _todasAsProvas.Clear();
                await CarregarProvasAsync();
            }
        }

        private void Filtro_Click(object sender, RoutedEventArgs e)
        {
            var itemClicado = sender as ToggleMenuFlyoutItem;
            if (itemClicado == null) return;

            FiltroTodos.IsChecked = itemClicado == FiltroTodos;
            FiltroProvas.IsChecked = itemClicado == FiltroProvas;
            FiltroRecuperacoes.IsChecked = itemClicado == FiltroRecuperacoes;

            if (itemClicado == FiltroProvas) _filtroAtual = FiltroTipo.Provas;
            else if (itemClicado == FiltroRecuperacoes) _filtroAtual = FiltroTipo.Recuperacoes;
            else _filtroAtual = FiltroTipo.Todos;

            AplicarFiltro();
        }

        private async Task CarregarProvasAsync()
        {
            if (MesComboBox.SelectedValue == null) return;

            LoadingRing.IsActive = true;
            SemProvasTextBlock.Visibility = Visibility.Collapsed;
            ProvasListView.Visibility = Visibility.Collapsed;
            _provasVisiveis.Clear();
            StatusBar.IsOpen = false;

            try
            {
                var mes = (int)MesComboBox.SelectedValue;
                bool online = IsOnline();
                string html = null;
                Debug.WriteLine($"Carregando provas - Mês: {mes}, Online: {online}");

                if (online)
                {
                    try
                    {
                        Debug.WriteLine("Tentando carregar provas online...");
                        html = await FetchProvasHtmlAsync(mes);
                        if (IsValidProvasHtml(html))
                        {
                            Debug.WriteLine("HTML válido recebido online. Salvando no cache...");
                            await SaveProvasCacheAsync(mes, html);
                            ParseProvasFromHtml(html);
                            AplicarFiltro();
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Falha ao carregar online: {ex.Message}");
                    }
                }

                Debug.WriteLine("Tentando carregar do cache...");
                html = await LoadProvasCacheAsync(mes);
                if (!string.IsNullOrEmpty(html) && IsValidProvasHtml(html))
                {
                    Debug.WriteLine("Cache válido encontrado. Exibindo dados salvos...");
                    ParseProvasFromHtml(html);
                    AplicarFiltro();
                    return;
                }

                Debug.WriteLine("Nenhum método de carregamento funcionou");
                if (online)
                {
                    throw new Exception("Você está deslogado. Por favor, faça o login novamente na página inicial.");
                }
                else
                {
                    throw new Exception("Você está offline. Verifique sua conexão com a internet.");
                }
            }
            catch (Exception ex)
            {
                StatusBar.IsOpen = true;
                StatusBar.Title = "Erro";
                StatusBar.Message = ex.Message;
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        private async Task<string> FetchProvasHtmlAsync(int mes)
        {
            string url = $"{BaseUrl}{ProvasUrl}?mes%5B%5D={mes}";
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", UserAgent);
            var cookies = await GetCookiesAsync(new Uri(BaseUrl));
            if (!string.IsNullOrEmpty(cookies))
            {
                request.Headers.Add("Cookie", cookies);
            }
            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private bool IsValidProvasHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return false;
            try
            {
                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(html);
                var table = htmlDoc.DocumentNode.SelectSingleNode("//table");
                var alertNode = htmlDoc.DocumentNode.SelectSingleNode("//div[contains(@class, 'alert-info')]");
                return table != null || alertNode != null;
            }
            catch { return false; }
        }

        private async Task SaveProvasCacheAsync(int mes, string html)
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                var htmlFile = await localFolder.CreateFileAsync($"{PROVAS_CACHE_PREFIX}{mes}.html", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(htmlFile, html);
                var timestampFile = await localFolder.CreateFileAsync($"{PROVAS_TIMESTAMP_PREFIX}{mes}.txt", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(timestampFile, DateTime.Now.ToBinary().ToString());
                Debug.WriteLine($"Cache das provas salvo para mês {mes}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao salvar cache das provas: {ex.Message}");
            }
        }

        private async Task<string> LoadProvasCacheAsync(int mes)
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                var htmlFile = await localFolder.GetFileAsync($"{PROVAS_CACHE_PREFIX}{mes}.html");
                return await FileIO.ReadTextAsync(htmlFile);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao carregar cache das provas: {ex.Message}");
            }
            return null;
        }

        private void ParseProvasFromHtml(string html)
        {
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);
            _todasAsProvas.Clear();

            var alertNode = htmlDoc.DocumentNode.SelectSingleNode("//div[contains(@class, 'alert-info')]");
            if (alertNode != null)
            {
                return;
            }

            var table = htmlDoc.DocumentNode.SelectSingleNode("//table");
            if (table != null)
            {
                var rows = table.SelectNodes(".//tbody/tr") ?? table.SelectNodes(".//tr");
                if (rows != null)
                {
                    foreach (var row in rows)
                    {
                        if (row.SelectNodes(".//th[@scope='col']") != null) continue;
                        var cells = row.SelectNodes(".//th[not(@scope='col')] | .//td");
                        if (cells != null && cells.Count >= 5)
                        {
                            string data = WebUtility.HtmlDecode(cells[0].InnerText.Trim());
                            string codigo = WebUtility.HtmlDecode(cells[1].InnerText).Replace("\n", "").Replace("\t", "").Trim();
                            var linkNode = cells[1].SelectSingleNode(".//a");
                            string link = string.Empty;
                            if (linkNode != null)
                            {
                                string href = linkNode.GetAttributeValue("href", "");
                                link = href.StartsWith("http") ? href : BaseUrl + href;
                                if (link.Contains("?")) link = link.Split('?')[0];
                            }
                            string tipo = WebUtility.HtmlDecode(cells[2].InnerText.Trim());
                            string conjuntoRaw = WebUtility.HtmlDecode(cells[3].InnerText.Trim());
                            string conjunto = !string.IsNullOrEmpty(conjuntoRaw) ? $"{conjuntoRaw}° conjunto" : "";
                            string materia = WebUtility.HtmlDecode(cells[4].InnerText.Trim());
                            _todasAsProvas.Add(new ProvaItem
                            {
                                Data = data,
                                Codigo = codigo,
                                Link = link,
                                Tipo = tipo,
                                Conjunto = conjunto,
                                Materia = materia
                            });
                        }
                    }
                }
            }
        }

        private void AplicarFiltro()
        {
            _provasVisiveis.Clear();
            IEnumerable<ProvaItem> provasFiltradas = _todasAsProvas;

            switch (_filtroAtual)
            {
                case FiltroTipo.Provas:
                    provasFiltradas = _todasAsProvas.Where(p => !p.Tipo.Contains("rec", StringComparison.OrdinalIgnoreCase));
                    break;
                case FiltroTipo.Recuperacoes:
                    provasFiltradas = _todasAsProvas.Where(p => p.Tipo.Contains("rec", StringComparison.OrdinalIgnoreCase));
                    break;
            }

            foreach (var prova in provasFiltradas)
            {
                _provasVisiveis.Add(prova);
            }

            if (_provasVisiveis.Any())
            {
                ProvasListView.Visibility = Visibility.Visible;
                SemProvasTextBlock.Visibility = Visibility.Collapsed;
            }
            else
            {
                ProvasListView.Visibility = Visibility.Collapsed;
                SemProvasTextBlock.Visibility = Visibility.Visible;
                SemProvasTextBlock.Text = "Nenhuma prova encontrada para os filtros selecionados.";
            }
        }

        private async void ProvasListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedProva = ProvasListView.SelectedItem as ProvaItem;
            if (selectedProva == null || string.IsNullOrEmpty(selectedProva.Link))
            {
                DetalhesGrid.Visibility = Visibility.Collapsed;
                PlaceholderDetalhes.Visibility = Visibility.Visible;
                return;
            }
            await CarregarMateriaAsync(selectedProva.Link);
        }

        private async Task CarregarMateriaAsync(string url)
        {
            if (MateriaWebView.CoreWebView2 == null)
            {
                await InitializeMateriaWebView();
            }

            LoadingDetalhesRing.IsActive = true;
            PlaceholderDetalhes.Visibility = Visibility.Collapsed;
            DetalhesGrid.Visibility = Visibility.Collapsed;

            try
            {
                bool online = IsOnline();
                string html = null;
                string urlKey = GetUrlCacheKey(url);
                Debug.WriteLine($"Carregando detalhes - URL: {url}, Online: {online}");

                html = await LoadDetalhesCacheAsync(urlKey);
                if (!string.IsNullOrEmpty(html) && IsValidDetalhesHtml(html))
                {
                    Debug.WriteLine("Cache de detalhes válido encontrado. Exibindo dados salvos...");
                    await DisplayDetalhesFromHtml(html);
                    return;
                }

                if (online)
                {
                    try
                    {
                        Debug.WriteLine("Tentando carregar detalhes online...");
                        html = await FetchDetalhesHtmlAsync(url);
                        if (IsValidDetalhesHtml(html))
                        {
                            Debug.WriteLine("HTML de detalhes válido recebido online. Salvando no cache...");
                            await SaveDetalhesCacheAsync(urlKey, html);
                            await DisplayDetalhesFromHtml(html);
                        }
                        else
                        {
                            throw new Exception("Não foi possível carregar os detalhes da prova.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Falha ao carregar detalhes online: {ex.Message}");
                        throw new Exception("Não foi possível carregar os detalhes da prova.");
                    }
                }
                else
                {
                    throw new Exception("Você está offline. Verifique sua conexão com a internet.");
                }
            }
            catch (Exception ex)
            {
                PlaceholderDetalhes.Text = ex.Message;
                PlaceholderDetalhes.Visibility = Visibility.Visible;
            }
            finally
            {
                LoadingDetalhesRing.IsActive = false;
            }
        }

        private async Task<string> FetchDetalhesHtmlAsync(string url)
        {
            if (url.Contains("https://https://"))
            {
                url = url.Replace("https://https://", "https://");
            }
            if (!url.StartsWith("http"))
            {
                url = BaseUrl + url;
            }

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", UserAgent);
            var cookies = await GetCookiesAsync(new Uri(BaseUrl));
            if (!string.IsNullOrEmpty(cookies))
            {
                request.Headers.Add("Cookie", cookies);
            }
            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private bool IsValidDetalhesHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return false;
            try
            {
                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(html);
                bool isLoggedIn = !html.Contains("Faça login para acessar") &&
                                  !html.Contains("Acesso Restrito") &&
                                  !html.Contains("redirect");
                var contentNode = htmlDoc.DocumentNode.SelectSingleNode("//*[contains(@class, 'contato-info')]");
                return isLoggedIn && contentNode != null;
            }
            catch { return false; }
        }

        private async Task SaveDetalhesCacheAsync(string urlKey, string html)
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                var htmlFile = await localFolder.CreateFileAsync($"{DETALHES_CACHE_PREFIX}{urlKey}.html", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(htmlFile, html);
                var timestampFile = await localFolder.CreateFileAsync($"{DETALHES_TIMESTAMP_PREFIX}{urlKey}.txt", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(timestampFile, DateTime.Now.ToBinary().ToString());
                Debug.WriteLine($"Cache dos detalhes salvo para URL key: {urlKey}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao salvar cache dos detalhes: {ex.Message}");
            }
        }

        private async Task<string> LoadDetalhesCacheAsync(string urlKey)
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                var htmlFile = await localFolder.GetFileAsync($"{DETALHES_CACHE_PREFIX}{urlKey}.html");
                return await FileIO.ReadTextAsync(htmlFile);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao carregar cache dos detalhes: {ex.Message}");
            }
            return null;
        }

        private string GetUrlCacheKey(string url)
        {
            url = url.Replace("https://https://", "https://");
            string key = url.Replace("https://", "").Replace("http://", "")
                           .Replace("/", "_").Replace("?", "_").Replace("&", "_")
                           .Replace("=", "_").Replace(":", "_").Replace("#", "_");
            if (key.Length > 100)
            {
                key = key.Substring(0, 100);
            }
            return key;
        }

        private async Task DisplayDetalhesFromHtml(string html)
        {
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);
            string titulo = "Detalhes da Prova";
            var headerNode = htmlDoc.DocumentNode.SelectSingleNode("//div[contains(@class, 'card-header')]/div");
            if (headerNode != null)
            {
                string tituloRaw = WebUtility.HtmlDecode(headerNode.InnerText);
                titulo = Regex.Replace(tituloRaw, @"\s+", " ").Trim();
            }
            var contentNode = htmlDoc.DocumentNode.SelectSingleNode("//*[contains(@class, 'contato-info')]");
            string conteudoHtml = contentNode?.OuterHtml ?? "<p>Conteúdo não disponível no momento.</p>";

            var isDarkTheme = this.ActualTheme == ElementTheme.Dark ||
                             (this.ActualTheme == ElementTheme.Default &&
                              Application.Current.RequestedTheme == ApplicationTheme.Dark);
            string textColor = isDarkTheme ? "#FFFFFF" : "#000000";
            string linkColor = isDarkTheme ? "#4EC1FF" : "#0066CC";

            string htmlCompleto = $@"
            <html>
            <head>
                <meta name='viewport' content='width=device-width, initial-scale=1'>
                <style>
                    body {{
                        font-family: 'Segoe UI', system-ui, sans-serif; 
                        line-height: 1.6; 
                        margin: 20px; 
                        color: {textColor}; 
                        background-color: transparent;
                    }}
                    a {{ color: {linkColor}; text-decoration: none; }}
                    a:hover {{ text-decoration: underline; }}
                    p {{ margin-bottom: 1em; color: {textColor}; }}
                    * {{ color: {textColor} !important; }}
                </style>
            </head>
            <body>{conteudoHtml}</body>
            </html>";

            TituloMateriaTextBlock.Text = titulo;
            try
            {
                if (MateriaWebView.CoreWebView2 == null)
                {
                    await InitializeMateriaWebView();
                }
                MateriaWebView.DefaultBackgroundColor = Colors.Transparent;
                MateriaWebView.NavigateToString(htmlCompleto);
                DetalhesGrid.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao exibir detalhes: {ex.Message}");
                PlaceholderDetalhes.Text = "Erro ao carregar detalhes da prova";
                PlaceholderDetalhes.Visibility = Visibility.Visible;
            }
        }

        private static async Task<string> GetCookiesAsync(Uri uri)
        {
            if (WebViewPage.CurrentWebView == null) return null;
            try
            {
                await WebViewPage.CurrentWebView.EnsureCoreWebView2Async();
                if (WebViewPage.CurrentWebView.CoreWebView2 == null) return null;
                var cookieManager = WebViewPage.CurrentWebView.CoreWebView2.CookieManager;
                var webViewCookies = await cookieManager.GetCookiesAsync(uri.ToString());
                if (webViewCookies == null || webViewCookies.Count == 0) return null;
                return string.Join("; ", webViewCookies.Select(c => $"{c.Name}={c.Value}"));
            }
            catch { return null; }
        }

        private void Divisor_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Parent is Grid grid)
            {
                _divisorGrid = grid;
                var isDarkTheme = this.ActualTheme == ElementTheme.Dark ||
                                 (this.ActualTheme == ElementTheme.Default &&
                                  Application.Current.RequestedTheme == ApplicationTheme.Dark);
                grid.Background = new SolidColorBrush(isDarkTheme ?
                    ColorHelper.FromArgb(255, 100, 100, 100) :
                    ColorHelper.FromArgb(255, 150, 150, 150));
            }
        }

        private void Divisor_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_isResizing && sender is FrameworkElement element && element.Parent is Grid grid)
            {
                _divisorGrid = grid;
                UpdateDivisorColors();
            }
        }

        private void Divisor_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            element?.CapturePointer(e.Pointer);
            _isResizing = true;
            _initialPointerPosition = e.GetCurrentPoint(this).Position.X;
            _initialColumnWidth = ((Grid)this.Content).ColumnDefinitions[0].Width.Value;
        }

        private void Divisor_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isResizing) return;
            var currentPosition = e.GetCurrentPoint(this).Position.X;
            var deltaX = currentPosition - _initialPointerPosition;
            var newWidth = _initialColumnWidth + deltaX;
            var minWidth = 320;
            var maxWidth = Math.Max(600, this.ActualWidth - 500);
            newWidth = Math.Max(minWidth, Math.Min(maxWidth, newWidth));
            if (this.ActualWidth < 900)
            {
                var proportion = newWidth / this.ActualWidth;
                if (proportion > 0.6)
                {
                    newWidth = this.ActualWidth * 0.6;
                }
            }
            var grid = (Grid)this.Content;
            grid.ColumnDefinitions[0].Width = new GridLength(newWidth);
        }

        private async void Divisor_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            element?.ReleasePointerCapture(e.Pointer);
            _isResizing = false;
            var grid = (Grid)this.Content;
            if (grid != null && grid.ColumnDefinitions.Count > 0)
            {
                var currentWidth = grid.ColumnDefinitions[0].Width.Value;
                await SaveColumnWidthAsync(currentWidth);
            }
            if (element?.Parent is Grid parentGrid)
            {
                _divisorGrid = parentGrid;
                UpdateDivisorColors();
            }
        }
    }

    public class ProvaItem
    {
        public string Data { get; set; } = string.Empty;
        public string Codigo { get; set; } = string.Empty;
        public string Link { get; set; } = string.Empty;
        public string Tipo { get; set; } = string.Empty;
        public string Conjunto { get; set; } = string.Empty;
        public string Materia { get; set; } = string.Empty;

        // Propriedade modificada para diferenciar os tipos de recuperação.
        public string TipoBadge
        {
            get
            {
                if (Tipo.Contains("1ªrec", StringComparison.OrdinalIgnoreCase))
                {
                    return "1ª REC";
                }
                if (Tipo.Contains("2ªrec", StringComparison.OrdinalIgnoreCase))
                {
                    return "2ª REC";
                }
                // Mantém um fallback para outros tipos de "rec" se existirem.
                if (Tipo.Contains("rec", StringComparison.OrdinalIgnoreCase))
                {
                    return "REC";
                }
                return "PROVA";
            }
        }
    }

    public class TipoProvaToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string tipo)
            {
                bool isRec = tipo.Contains("rec", StringComparison.OrdinalIgnoreCase);
                var color = isRec ?
                    ColorHelper.FromArgb(255, 255, 193, 7) :
                    ColorHelper.FromArgb(255, 64, 159, 255);
                return new SolidColorBrush(color);
            }
            return new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
