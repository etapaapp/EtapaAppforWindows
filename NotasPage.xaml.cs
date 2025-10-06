using HtmlAgilityPack;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
using Windows.Storage;

namespace EtapaApp
{
    public sealed partial class NotasPage : Page
    {
        private const string URL_NOTAS = "https://areaexclusiva.colegioetapa.com.br/provas/notas";
        private const string AndroidUserAgent =
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_7_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.4 Safari/605.1.15";
        // Chaves para armazenamento offline
        private const string CACHE_FILENAME = "notas_cache.html";
        private const string CACHE_TIMESTAMP_FILENAME = "notas_timestamp.txt";

        // Cores das células da tabela (fixas)
        private readonly SolidColorBrush ColorSuccess = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 40, 167, 69));
        private readonly SolidColorBrush ColorWarning = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 255, 193, 7));
        private readonly SolidColorBrush ColorDanger = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 220, 53, 69));
        private readonly SolidColorBrush ColorWhite = new SolidColorBrush(Microsoft.UI.Colors.White);
        private readonly SolidColorBrush ColorBlack = new SolidColorBrush(Microsoft.UI.Colors.Black);

        // Cache da última tabela HTML para recriar quando o tema muda
        private string _lastHtmlTable = null;

        // Propriedades para cores do sistema (dinâmicas)
        private SolidColorBrush ColorOnSurface => (SolidColorBrush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        private SolidColorBrush ColorHeaderBg => (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        private SolidColorBrush ColorHeaderText => (SolidColorBrush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
        private SolidColorBrush ColorFundoCartao => (SolidColorBrush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];

        public NotasPage()
        {
            InitializeComponent();

            // Registrar para mudanças de tema
            this.ActualThemeChanged += OnThemeChanged;

            ShowLoadingIndicator(true);
            _ = LoadNotasAsync();
        }

        private void OnThemeChanged(FrameworkElement sender, object args)
        {
            Debug.WriteLine("Tema alterado - recriando tabela");

            // Se temos dados em cache, recriar a tabela com as novas cores
            if (!string.IsNullOrEmpty(_lastHtmlTable))
            {
                try
                {
                    ParseAndBuildTable(_lastHtmlTable);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Erro ao recriar tabela após mudança de tema: {ex.Message}");
                }
            }
        }

        private async Task LoadNotasAsync()
        {
            try
            {
                ShowLoadingIndicator(true);
                StatusBar.IsOpen = false;
                bool online = IsOnline();
                string html = null;

                Debug.WriteLine($"Status de conexão: {(online ? "Online" : "Offline")}");

                // 1. Tentar carregar online se estiver conectado
                if (online)
                {
                    try
                    {
                        Debug.WriteLine("Tentando carregar notas online...");
                        html = await FetchNotasHtmlAsync();
                        Debug.WriteLine($"HTML recebido: {html?.Length ?? 0} caracteres");

                        if (IsValidTableHtml(html))
                        {
                            Debug.WriteLine("HTML válido recebido online. Salvando no cache...");
                            await SaveCacheAsync(html);
                            _lastHtmlTable = html; // Cache para mudanças de tema
                            ParseAndBuildTable(html);
                            return;
                        }
                        else
                        {
                            Debug.WriteLine("HTML online não contém tabela válida");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Falha ao carregar online: {ex.Message}");
                    }
                }

                // 2. Se online falhou ou estamos offline, tentar cache
                Debug.WriteLine("Tentando carregar do cache...");
                html = await LoadCachedHtmlAsync();

                if (!string.IsNullOrEmpty(html))
                {
                    Debug.WriteLine($"HTML do cache: {html.Length} caracteres");

                    if (IsValidTableHtml(html))
                    {
                        Debug.WriteLine("Cache válido encontrado. Exibindo dados salvos...");
                        _lastHtmlTable = html; // Cache para mudanças de tema
                        ParseAndBuildTable(html);

                        var cacheAge = await GetCacheAgeAsync();
                        string ageText = cacheAge.HasValue ? $" (salvo há {FormatCacheAge(cacheAge.Value)})" : "";

                        if (online)
                        {
                            ShowStatus($"Usando dados salvos{ageText}", "Você está deslogado", InfoBarSeverity.Warning);
                        }
                        else
                        {
                            ShowStatus($"Modo offline{ageText}", "Dados podem estar desatualizados", InfoBarSeverity.Informational);
                        }
                        return;
                    }
                    else
                    {
                        Debug.WriteLine("Cache encontrado, mas HTML não é válido");
                    }
                }
                else
                {
                    Debug.WriteLine("Cache não encontrado ou vazio");
                }
                // 3. Se nada funcionou
                Debug.WriteLine("Nenhum método de carregamento funcionou");
                if (online)
                {
                    ShowStatus("Não foi possível carregar as notas", "Erro de conexão", InfoBarSeverity.Error);
                }
                else
                {
                    ShowStatus("Sem conexão e sem dados salvos", "Conecte-se à internet", InfoBarSeverity.Error);
                }

                NotasGrid.Children.Clear();
                NotasGrid.RowDefinitions.Clear();
                NotasGrid.ColumnDefinitions.Clear();
                _lastHtmlTable = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro inesperado: {ex}");
                ShowStatus($"Erro inesperado: {ex.Message}", "Erro crítico", InfoBarSeverity.Error);
            }
            finally
            {
                ShowLoadingIndicator(false);
            }
        }

        private bool IsValidTableHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                Debug.WriteLine("HTML nulo ou vazio");
                return false;
            }

            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var table = doc.DocumentNode.SelectSingleNode("//table");
                if (table == null)
                {
                    Debug.WriteLine("Tabela não encontrada no parse HTML");
                    return false;
                }

                var allCells = table.SelectNodes(".//td | .//th");
                bool hasContent = allCells != null && allCells.Count > 0;

                Debug.WriteLine($"Validação da tabela: {allCells?.Count ?? 0} células encontradas");
                return hasContent;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro na validação do HTML: {ex.Message}");
                return html.Contains("<table") && html.Length > 1000;
            }
        }

        private void ShowStatus(string message, string title, InfoBarSeverity severity)
        {
            StatusBar.Title = title;
            StatusBar.Message = message;
            StatusBar.Severity = severity;
            StatusBar.IsOpen = true;
        }

        private bool IsOnline()
        {
            try
            {
                var connectionProfile = NetworkInformation.GetInternetConnectionProfile();
                return connectionProfile != null &&
                    connectionProfile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao verificar conexão: {ex.Message}");
                return false;
            }
        }

        private async Task SaveCacheAsync(string html)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(html)) return;

                var localFolder = ApplicationData.Current.LocalFolder;
                var htmlFile = await localFolder.CreateFileAsync(CACHE_FILENAME, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(htmlFile, html);

                var timestampFile = await localFolder.CreateFileAsync(CACHE_TIMESTAMP_FILENAME, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(timestampFile, DateTime.Now.ToBinary().ToString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao salvar cache: {ex.Message}");
            }
        }

        private async Task<string> LoadCachedHtmlAsync()
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                var files = await localFolder.GetFilesAsync();
                if (files.Any(f => f.Name == CACHE_FILENAME))
                {
                    var htmlFile = await localFolder.GetFileAsync(CACHE_FILENAME);
                    return await FileIO.ReadTextAsync(htmlFile);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao carregar cache: {ex.Message}");
            }
            return null;
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
                        return DateTime.Now - DateTime.FromBinary(timestamp);
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
            if (age.TotalDays >= 1) return $"{(int)age.TotalDays} dia(s)";
            if (age.TotalHours >= 1) return $"{(int)age.TotalHours} hora(s)";
            if (age.TotalMinutes >= 1) return $"{(int)age.TotalMinutes} minuto(s)";
            return "poucos segundos";
        }

        private void ShowLoadingIndicator(bool show)
        {
            LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task<string> FetchNotasHtmlAsync()
        {
            var webView = WebViewPage.CurrentWebView;
            if (webView?.CoreWebView2 == null) throw new Exception("WebView não está disponível");

            var cookies = await webView.CoreWebView2.CookieManager.GetCookiesAsync(URL_NOTAS);
            var cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Cookie", cookieHeader);
            httpClient.DefaultRequestHeaders.Add("User-Agent", AndroidUserAgent);
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            var response = await httpClient.GetAsync(URL_NOTAS);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private void ParseAndBuildTable(string html)
        {
            try
            {
                if (string.IsNullOrEmpty(html)) throw new Exception("HTML está vazio");
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                var table = doc.DocumentNode.SelectSingleNode("//table");
                if (table == null) throw new Exception("Tabela não encontrada no HTML");
                BuildTableFromHtml(table);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERRO em ParseAndBuildTable: {ex.Message}");
                throw;
            }
        }

        // *** NOVA FUNÇÃO AUXILIAR ***
        private string ExtractNotaFromCell(HtmlNode cell)
        {
            if (cell == null) return "--";

            var notaContainer = cell.SelectNodes(".//div[contains(@class, 'd-flex') and contains(@class, 'flex-column')]")
                                    ?.FirstOrDefault(div =>
                                        div.SelectSingleNode(".//span[contains(@class, 'font-weight-bold')]")
                                           ?.InnerText.Trim().Equals("Nota", StringComparison.OrdinalIgnoreCase) ?? false
                                    );

            if (notaContainer != null)
            {
                return WebUtility.HtmlDecode(notaContainer.InnerText.Replace("Nota", "", StringComparison.OrdinalIgnoreCase).Trim());
            }

            return WebUtility.HtmlDecode(cell.InnerText.Trim());
        }

        private void BuildTableFromHtml(HtmlNode table)
        {
            NotasGrid.Children.Clear();
            NotasGrid.RowDefinitions.Clear();
            NotasGrid.ColumnDefinitions.Clear();

            var headers = table.SelectNodes(".//thead//tr[1]//th") ?? table.SelectNodes(".//tr[th][1]//th");
            if (headers == null || headers.Count == 0) throw new Exception("Cabeçalhos da tabela não encontrados");

            // Ignorar a coluna "Matéria" (primeira coluna)
            for (int i = 1; i < headers.Count; i++)
            {
                NotasGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            NotasGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 1; i < headers.Count; i++)
            {
                var headerText = WebUtility.HtmlDecode(headers[i].InnerText.Trim());
                var headerCell = CreateCell(headerText, true);
                Grid.SetRow(headerCell, 0);
                Grid.SetColumn(headerCell, i - 1);
                NotasGrid.Children.Add(headerCell);
            }

            var dataRows = table.SelectNodes(".//tbody//tr");
            if (dataRows == null || dataRows.Count == 0) return;

            int rowIndex = 1;
            var notaCols = Math.Max(0, headers.Count - 2);
            var sums = new double[notaCols];
            var counts = new int[notaCols];

            foreach (var row in dataRows)
            {
                var cells = row.SelectNodes(".//td | .//th");
                if (cells == null || cells.Count < 2) continue;

                NotasGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // Começar do 1 para ignorar a coluna "Matéria"
                for (int j = 1; j < Math.Min(cells.Count, headers.Count); j++)
                {
                    // *** LÓGICA DE EXTRAÇÃO ATUALIZADA AQUI ***
                    string cellText = (j >= 2)
                        ? ExtractNotaFromCell(cells[j])
                        : WebUtility.HtmlDecode(cells[j].InnerText.Trim());

                    var cell = CreateCell(cellText, j == 0); // j==0 é a coluna de matéria (cabeçalho da linha)

                    if (j >= 2)
                    {
                        var cssClass = cells[j].GetAttributeValue("class", "");
                        ApplyCellStyling(cell, cssClass);

                        var colIndex = j - 2;
                        if (colIndex < notaCols && double.TryParse(cellText, NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
                        {
                            sums[colIndex] += value;
                            counts[colIndex]++;
                        }
                    }

                    Grid.SetRow(cell, rowIndex);
                    Grid.SetColumn(cell, j - 1);
                    NotasGrid.Children.Add(cell);
                }
                rowIndex++;
            }

            if (notaCols > 0)
            {
                NotasGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var avgLabelCell = CreateCell("Média", true);
                Grid.SetRow(avgLabelCell, rowIndex);
                Grid.SetColumn(avgLabelCell, 0);
                NotasGrid.Children.Add(avgLabelCell);

                for (int k = 0; k < notaCols; k++)
                {
                    string avgValue = counts[k] > 0 ? (sums[k] / counts[k]).ToString("F2", CultureInfo.InvariantCulture) : "--";
                    var avgCell = CreateCell(avgValue, true);
                    Grid.SetRow(avgCell, rowIndex);
                    Grid.SetColumn(avgCell, k + 1); // +1 porque a primeira coluna é o label "Média"
                    NotasGrid.Children.Add(avgCell);
                }
            }
        }

        private Border CreateCell(string text, bool isHeader)
        {
            var textBlock = new TextBlock
            {
                Text = text,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = isHeader ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                FontSize = isHeader ? 14 : 13,
                Foreground = isHeader ? ColorHeaderText : ColorOnSurface,
                Padding = new Thickness(12, 8, 12, 8),
                TextWrapping = TextWrapping.Wrap
            };

            var borderBrush = isHeader ? ColorHeaderBg : (SolidColorBrush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];

            return new Border
            {
                Child = textBlock,
                Background = isHeader ? ColorHeaderBg : ColorFundoCartao,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(0)
            };
        }

        private void ApplyCellStyling(Border cell, string cssClass)
        {
            if (!(cell.Child is TextBlock textBlock)) return;

            if (cssClass.Contains("bg-success"))
            {
                cell.Background = ColorSuccess;
                textBlock.Foreground = ColorWhite;
                cell.CornerRadius = new CornerRadius(4);
            }
            else if (cssClass.Contains("bg-warning"))
            {
                cell.Background = ColorWarning;
                textBlock.Foreground = ColorBlack;
                cell.CornerRadius = new CornerRadius(4);
            }
            else if (cssClass.Contains("bg-danger"))
            {
                cell.Background = ColorDanger;
                textBlock.Foreground = ColorWhite;
                cell.CornerRadius = new CornerRadius(4);
            }
        }

        protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            this.ActualThemeChanged -= OnThemeChanged;
            base.OnNavigatedFrom(e);
        }
    }
}
