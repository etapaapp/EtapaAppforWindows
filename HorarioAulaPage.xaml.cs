using HtmlAgilityPack;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
using Windows.Storage;

namespace EtapaApp
{
    public sealed partial class HorarioAulaPage : Page
    {
        private const string URL_HORARIOS = "https://areaexclusiva.colegioetapa.com.br/horarios/aulas";
        private const string AndroidUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_7_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.4 Safari/605.1.15";
        private const string TABLE_SELECTOR = "//table";

        // Cache
        private const string CACHE_FILENAME = "horarios_cache.html";
        private const string CACHE_TIMESTAMP_FILENAME = "horarios_timestamp.txt";

        // Cores dinâmicas
        private SolidColorBrush ColorOnSurface => (SolidColorBrush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        private SolidColorBrush ColorHeaderBg => (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        private SolidColorBrush ColorHeaderText => (SolidColorBrush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
        private SolidColorBrush ColorFundoCartao => (SolidColorBrush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        private SolidColorBrush ColorDestaque => (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"]; // Nova cor de destaque

        public HorarioAulaPage()
        {
            InitializeComponent();
            this.ActualThemeChanged += OnThemeChanged;
            _ = LoadHorariosAsync();
        }

        private void OnThemeChanged(FrameworkElement sender, object args)
        {
            if (!string.IsNullOrEmpty(_lastHtmlContent))
            {
                ParseAndBuildTable(_lastHtmlContent);
            }
        }

        private async Task LoadHorariosAsync()
        {
            try
            {
                ShowLoadingIndicator(true);
                StatusBar.IsOpen = false;
                bool online = IsOnline();

                // Tentar carregar online
                if (online)
                {
                    try
                    {
                        var html = await FetchHorariosHtmlAsync();
                        if (!string.IsNullOrEmpty(html))
                        {
                            var doc = new HtmlDocument();
                            doc.LoadHtml(html);

                            // Foco absoluto na tabela - ignorar qualquer outra coisa
                            var tableNode = doc.DocumentNode.SelectSingleNode(TABLE_SELECTOR);
                            if (tableNode != null)
                            {
                                await SaveCacheAsync(tableNode.OuterHtml);
                                ParseAndBuildTable(tableNode.OuterHtml);
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Falha online: {ex.Message}");
                    }
                }

                // Fallback para cache
                var cachedHtml = await LoadCacheAsync();
                if (!string.IsNullOrEmpty(cachedHtml))
                {
                    ParseAndBuildTable(cachedHtml);
                    var cacheAge = await GetCacheAgeAsync();
                    string ageText = cacheAge.HasValue ? $" (salvo há {FormatCacheAge(cacheAge.Value)})" : "";
                    ShowStatus($"Modo offline{ageText}", "Dados podem estar desatualizados", InfoBarSeverity.Informational);
                    return;
                }

                // Fallback final
                ShowStatus("Sem conexão e sem dados salvos",
                    online ? "Erro de conexão" : "Conecte-se à Internet",
                    InfoBarSeverity.Error);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro crítico: {ex}");
                ShowStatus($"Erro: {ex.Message}", "Falha inesperada", InfoBarSeverity.Error);
            }
            finally
            {
                ShowLoadingIndicator(false);
            }
        }

        private async Task SaveCacheAsync(string content)
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                var file = await localFolder.CreateFileAsync(CACHE_FILENAME, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, content);

                var timestampFile = await localFolder.CreateFileAsync(CACHE_TIMESTAMP_FILENAME, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(timestampFile, DateTime.Now.ToBinary().ToString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro no cache: {ex.Message}");
            }
        }

        private async Task<string> LoadCacheAsync()
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                var file = await localFolder.GetFileAsync(CACHE_FILENAME);
                return await FileIO.ReadTextAsync(file);
            }
            catch
            {
                return null;
            }
        }

        private async Task<TimeSpan?> GetCacheAgeAsync()
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                var files = await localFolder.GetFilesAsync();

                if (files.Any(f => f.Name == CACHE_TIMESTAMP_FILENAME))
                {
                    var timestampFile = await localFolder.GetFileAsync(CACHE_TIMESTAMP_FILENAME);
                    string timestampStr = await FileIO.ReadTextAsync(timestampFile);

                    if (long.TryParse(timestampStr, out long timestamp))
                    {
                        DateTime cacheTime = DateTime.FromBinary(timestamp);
                        return DateTime.Now - cacheTime;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao obter idade do cache: {ex.Message}");
            }
            return null;
        }

        private string FormatCacheAge(TimeSpan age)
        {
            if (age.TotalDays >= 1)
                return $"{(int)age.TotalDays} dia(s)";
            else if (age.TotalHours >= 1)
                return $"{(int)age.TotalHours} hora(s)";
            else if (age.TotalMinutes >= 1)
                return $"{(int)age.TotalMinutes} minuto(s)";
            else
                return "poucos segundos";
        }

        private bool IsOnline()
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                return profile?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            }
            catch
            {
                return false;
            }
        }

        private async Task<string> FetchHorariosHtmlAsync()
        {
            var webView = WebViewPage.CurrentWebView;
            if (webView?.CoreWebView2 == null)
                throw new Exception("WebView indisponível");

            var cookies = await webView.CoreWebView2.CookieManager.GetCookiesAsync(URL_HORARIOS);
            var cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Cookie", cookieHeader);
            httpClient.DefaultRequestHeaders.Add("User-Agent", AndroidUserAgent);
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            return await httpClient.GetStringAsync(URL_HORARIOS);
        }

        private void ShowLoadingIndicator(bool show)
        {
            LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowStatus(string message, string title, InfoBarSeverity severity)
        {
            StatusBar.Title = title;
            StatusBar.Message = message;
            StatusBar.Severity = severity;
            StatusBar.IsOpen = true;
        }

        private void ParseAndBuildTable(string html)
        {
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                var table = doc.DocumentNode.SelectSingleNode("//table");

                if (table == null) return;

                BuildTableFromHtml(table);
                _lastHtmlContent = html;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro no parsing: {ex.Message}");
            }
        }

        private void BuildTableFromHtml(HtmlNode table)
        {
            try
            {
                HorariosGrid.Children.Clear();
                HorariosGrid.RowDefinitions.Clear();
                HorariosGrid.ColumnDefinitions.Clear();

                // Extrair cabeçalhos
                var headerRow = table.SelectSingleNode(".//tr[th]");
                if (headerRow == null) return;

                var headers = headerRow.SelectNodes("th");
                if (headers == null || headers.Count == 0) return;

                // Configurar colunas
                foreach (var _ in headers)
                {
                    HorariosGrid.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(1, GridUnitType.Star),
                        MinWidth = 120
                    });
                }

                // Adicionar linha de cabeçalho
                HorariosGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (int i = 0; i < headers.Count; i++)
                {
                    var headerText = WebUtility.HtmlDecode(headers[i].InnerText.Trim());
                    var headerCell = CreateCell(headerText, true);
                    Grid.SetRow(headerCell, 0);
                    Grid.SetColumn(headerCell, i);
                    HorariosGrid.Children.Add(headerCell);
                }

                // Extrair linhas de dados (ignorando qualquer div de alerta)
                int rowIndex = 1;
                var dataRows = table.SelectNodes(".//tr[position()>1]");

                foreach (var row in dataRows)
                {
                    // Ignorar completamente linhas com alertas
                    if (row.InnerHtml.Contains("alert-info"))
                    {
                        Debug.WriteLine("Linha de alerta ignorada");
                        continue;
                    }

                    HorariosGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var cells = row.SelectNodes("td|th");

                    for (int j = 0; j < cells?.Count; j++)
                    {
                        var cellText = WebUtility.HtmlDecode(cells[j].InnerText.Trim());
                        var isRowHeader = cells[j].Name == "th";
                        var cell = CreateCell(cellText, isRowHeader);

                        // Usar cor de destaque do sistema em vez de bg-primary
                        if (cells[j].GetAttributeValue("class", "").Contains("bg-primary"))
                        {
                            cell.Background = ColorDestaque;
                            if (cell.Child is TextBlock textBlock)
                            {
                                textBlock.Foreground = ColorHeaderText;
                            }
                        }

                        Grid.SetRow(cell, rowIndex);
                        Grid.SetColumn(cell, j);
                        HorariosGrid.Children.Add(cell);
                    }
                    rowIndex++;
                }

                TableScrollViewer.Visibility = Visibility.Visible;
                MessageContainer.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro na construção da tabela: {ex.Message}");
            }
        }

        private Border CreateCell(string text, bool isHeader)
        {
            return new Border
            {
                Child = new TextBlock
                {
                    Text = text,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = isHeader ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                    FontSize = 14,
                    Foreground = isHeader ? ColorHeaderText : ColorOnSurface,
                    Padding = new Thickness(12, 8, 12, 8),
                    TextWrapping = TextWrapping.Wrap
                },
                Background = isHeader ? ColorHeaderBg : ColorFundoCartao,
                BorderThickness = new Thickness(0.5),
                CornerRadius = new CornerRadius(4)
            };
        }

        protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            this.ActualThemeChanged -= OnThemeChanged;
            base.OnNavigatedFrom(e);
        }

        // Variável para cache de tema
        private string _lastHtmlContent;
    }
}