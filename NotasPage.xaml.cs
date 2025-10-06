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
                bool usedCache = false;

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
                            // Removida a mensagem de sucesso
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
                        usedCache = true;

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
                        return; // Importante: sair aqui quando cache funciona
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

                // Limpar a grid se não temos dados
                NotasGrid.Children.Clear();
                NotasGrid.RowDefinitions.Clear();
                NotasGrid.ColumnDefinitions.Clear();
                _lastHtmlTable = null; // Limpar cache
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
                // Validação básica - verifica se tem estrutura mínima de tabela
                bool hasTable = html.Contains("<table") && html.Contains("</table>");

                if (!hasTable)
                {
                    Debug.WriteLine("Não encontrou tags de tabela");
                    return false;
                }

                // Validação mais detalhada com HtmlAgilityPack
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var table = doc.DocumentNode.SelectSingleNode("//table");
                if (table == null)
                {
                    Debug.WriteLine("Tabela não encontrada no parse HTML");
                    return false;
                }

                // Verifica se tem pelo menos alguns elementos de tabela
                var allCells = table.SelectNodes(".//td | .//th");
                bool hasContent = allCells != null && allCells.Count > 0;

                Debug.WriteLine($"Validação da tabela: {allCells?.Count ?? 0} células encontradas");
                Debug.WriteLine($"HTML é válido: {hasContent}");

                return hasContent;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro na validação do HTML: {ex.Message}");
                // Em caso de erro na validação, assumir que é válido se tem tag table
                bool fallbackValid = html.Contains("<table") && html.Length > 1000; // HTML mínimo esperado
                Debug.WriteLine($"Usando validação fallback: {fallbackValid}");
                return fallbackValid;
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
                bool isConnected = connectionProfile != null &&
                    connectionProfile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
                Debug.WriteLine($"Status de conexão verificado: {isConnected}");
                return isConnected;
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
                if (string.IsNullOrWhiteSpace(html))
                {
                    Debug.WriteLine("Tentativa de salvar HTML vazio no cache");
                    return;
                }

                Debug.WriteLine($"Salvando cache em arquivo: {html.Length} caracteres");

                var localFolder = ApplicationData.Current.LocalFolder;

                // Salvar HTML
                var htmlFile = await localFolder.CreateFileAsync(CACHE_FILENAME, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(htmlFile, html);

                // Salvar timestamp
                var timestampFile = await localFolder.CreateFileAsync(CACHE_TIMESTAMP_FILENAME, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(timestampFile, DateTime.Now.ToBinary().ToString());

                Debug.WriteLine("Cache salvo com sucesso em arquivos");
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

                Debug.WriteLine("Verificando se arquivo de cache existe...");

                var files = await localFolder.GetFilesAsync();
                bool cacheExists = files.Any(f => f.Name == CACHE_FILENAME);

                Debug.WriteLine($"Arquivo de cache existe: {cacheExists}");

                if (cacheExists)
                {
                    var htmlFile = await localFolder.GetFileAsync(CACHE_FILENAME);
                    string html = await FileIO.ReadTextAsync(htmlFile);

                    Debug.WriteLine($"Cache carregado do arquivo: {html?.Length ?? 0} caracteres");

                    if (!string.IsNullOrEmpty(html))
                    {
                        Debug.WriteLine("Cache carregado com sucesso");
                        return html;
                    }
                    else
                    {
                        Debug.WriteLine("Arquivo de cache está vazio");
                    }
                }
                else
                {
                    Debug.WriteLine("Arquivo de cache não encontrado");
                }
            }
            catch (FileNotFoundException)
            {
                Debug.WriteLine("Arquivo de cache não existe");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao carregar cache: {ex.Message}");
            }

            Debug.WriteLine("Retornando null - cache não disponível");
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

        private void ShowLoadingIndicator(bool show)
        {
            LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task<string> FetchNotasHtmlAsync()
        {
            try
            {
                var webView = WebViewPage.CurrentWebView;
                if (webView?.CoreWebView2 == null)
                {
                    Debug.WriteLine("WebView não disponível");
                    throw new Exception("WebView não está disponível");
                }

                var cookies = await webView.CoreWebView2.CookieManager.GetCookiesAsync(URL_NOTAS);
                var cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                Debug.WriteLine($"Cookies encontrados: {cookies.Count}");

                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("Cookie", cookieHeader);
                httpClient.DefaultRequestHeaders.Add("User-Agent", AndroidUserAgent);
                httpClient.Timeout = TimeSpan.FromSeconds(30); // Aumentado timeout

                var response = await httpClient.GetAsync(URL_NOTAS);
                response.EnsureSuccessStatusCode();

                string html = await response.Content.ReadAsStringAsync();
                Debug.WriteLine($"HTML baixado com sucesso: {html?.Length ?? 0} caracteres");

                return html;
            }
            catch (HttpRequestException ex)
            {
                Debug.WriteLine($"Erro HTTP ao buscar HTML: {ex.Message}");
                throw new Exception($"Erro de rede: {ex.Message}");
            }
            catch (TaskCanceledException ex)
            {
                Debug.WriteLine($"Timeout ao buscar HTML: {ex.Message}");
                throw new Exception("Tempo limite excedido");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro inesperado ao buscar HTML: {ex.Message}");
                throw new Exception($"Falha ao acessar o servidor: {ex.Message}");
            }
        }

        private void ParseAndBuildTable(string html)
        {
            try
            {
                Debug.WriteLine($"=== INÍCIO ParseAndBuildTable ===");
                Debug.WriteLine($"HTML length: {html?.Length ?? 0}");

                if (string.IsNullOrEmpty(html))
                {
                    throw new Exception("HTML está vazio");
                }

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // Encontra a tabela principal
                var table = doc.DocumentNode.SelectSingleNode("//table");
                if (table == null)
                {
                    Debug.WriteLine("Tabela não encontrada no HTML");
                    Debug.WriteLine($"HTML snippet: {html.Substring(0, Math.Min(500, html.Length))}");
                    throw new Exception("Tabela não encontrada no HTML");
                }

                Debug.WriteLine("Tabela encontrada, construindo interface...");
                BuildTableFromHtml(table);
                Debug.WriteLine("=== FIM ParseAndBuildTable (SUCESSO) ===");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"=== ERRO ParseAndBuildTable: {ex.Message} ===");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        private void BuildTableFromHtml(HtmlNode table)
        {
            Debug.WriteLine($"=== INÍCIO BuildTableFromHtml ===");

            NotasGrid.Children.Clear();
            NotasGrid.RowDefinitions.Clear();
            NotasGrid.ColumnDefinitions.Clear();

            // Obter cabeçalhos corretamente - apenas da primeira linha do thead
            var headers = table.SelectNodes(".//thead//tr[1]//th");

            if (headers == null || headers.Count == 0)
            {
                Debug.WriteLine("Tentativa alternativa: procurar primeira linha com th");
                var firstRowWithTh = table.SelectSingleNode(".//tr[th]");
                if (firstRowWithTh != null)
                {
                    headers = firstRowWithTh.SelectNodes(".//th");
                }
            }

            if (headers == null || headers.Count == 0)
            {
                Debug.WriteLine("ERRO: Nenhum cabeçalho encontrado");
                throw new Exception("Cabeçalhos da tabela não encontrados");
            }

            Debug.WriteLine($"Encontrados {headers.Count} cabeçalhos");

            // Criar colunas apenas para os cabeçalhos reais
            for (int i = 0; i < headers.Count; i++)
            {
                NotasGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(220)
                });
            }

            // Adicionar linha de cabeçalho
            NotasGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < headers.Count; i++)
            {
                var headerText = WebUtility.HtmlDecode(headers[i].InnerText.Trim());
                Debug.WriteLine($"Cabeçalho {i}: {headerText}");
                var headerCell = CreateCell(headerText, true);
                Grid.SetRow(headerCell, 0);
                Grid.SetColumn(headerCell, i);
                NotasGrid.Children.Add(headerCell);
            }

            // Processar apenas as linhas do tbody
            var dataRows = table.SelectNodes(".//tbody//tr");
            if (dataRows == null || dataRows.Count == 0)
            {
                Debug.WriteLine("AVISO: Nenhuma linha de dados encontrada no tbody");
                return;
            }

            Debug.WriteLine($"Processando {dataRows.Count} linhas de dados do tbody");

            int rowIndex = 1;
            var notaCols = Math.Max(0, headers.Count - 2); // Colunas de notas (excluindo Matéria e Código)
            var sums = new double[notaCols];
            var counts = new int[notaCols];

            foreach (var row in dataRows)
            {
                var cells = row.SelectNodes(".//td | .//th");

                if (cells == null || cells.Count < 2)
                {
                    Debug.WriteLine($"Linha ignorada: poucas células ({cells?.Count ?? 0})");
                    continue;
                }

                NotasGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                for (int j = 0; j < Math.Min(cells.Count, headers.Count); j++)
                {
                    var cellNode = cells[j];
                    string cellText;

                    // =========================================================================
                    // INÍCIO DA LÓGICA CORRIGIDA para extrair apenas a nota
                    // =========================================================================
                    // Procura pelo container que agrupa os dados da nota (tem um span com o texto "Nota")
                    var notaContainerNode = cellNode.SelectSingleNode(".//div[contains(@class, 'd-flex') and .//span[contains(., 'Nota')]]");

                    if (notaContainerNode != null)
                    {
                        // Dentro do container, encontra o primeiro 'flex-column', que é sempre o da nota.
                        var notaColumnNode = notaContainerNode.SelectSingleNode(".//div[contains(@class, 'd-flex flex-column')]");
                        if (notaColumnNode != null)
                        {
                            // A nota é o último nó dentro desta coluna (pode ser um <span> ou um nó de texto).
                            // Iteramos de trás para frente para ignorar nós de texto de espaço em branco.
                            HtmlNode valueNode = notaColumnNode.LastChild;
                            while (valueNode != null && valueNode.NodeType == HtmlNodeType.Text && string.IsNullOrWhiteSpace(valueNode.InnerText))
                            {
                                valueNode = valueNode.PreviousSibling;
                            }

                            cellText = valueNode != null
                                ? WebUtility.HtmlDecode(valueNode.InnerText.Trim())
                                : "--"; // Fallback se não encontrar o valor
                        }
                        else
                        {
                            cellText = "--"; // Fallback se não encontrar a estrutura de coluna
                        }
                    }
                    else
                    {
                        // Lógica antiga para células simples (Matéria, Código)
                        cellText = WebUtility.HtmlDecode(cellNode.InnerText.Trim());
                    }
                    // =========================================================================
                    // FIM DA LÓGICA CORRIGIDA
                    // =========================================================================

                    var cell = CreateCell(cellText, false);

                    // Aplicar estilo para células de nota (a partir da terceira coluna - índice 2)
                    if (j >= 2)
                    {
                        var cssClass = cells[j].GetAttributeValue("class", "");
                        ApplyCellStyling(cell, cssClass);

                        // Calcular média para colunas de notas
                        if (cellText != "--")
                        {
                            var colIndex = j - 2;
                            if (colIndex < notaCols && double.TryParse(cellText, NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
                            {
                                sums[colIndex] += value;
                                counts[colIndex]++;
                            }
                        }
                    }

                    Grid.SetRow(cell, rowIndex);
                    Grid.SetColumn(cell, j);
                    NotasGrid.Children.Add(cell);
                }
                rowIndex++;
            }

            // Adicionar linha de média
            if (notaCols > 0)
            {
                NotasGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var avgLabelCell = CreateCell("Média", true);
                Grid.SetRow(avgLabelCell, rowIndex);
                Grid.SetColumn(avgLabelCell, 0);
                NotasGrid.Children.Add(avgLabelCell);

                if (headers.Count > 1)
                {
                    var avgCodeCell = CreateCell("--", true);
                    Grid.SetRow(avgCodeCell, rowIndex);
                    Grid.SetColumn(avgCodeCell, 1);
                    NotasGrid.Children.Add(avgCodeCell);
                }

                for (int k = 0; k < notaCols; k++)
                {
                    string avgValue = "--";
                    if (counts[k] > 0)
                    {
                        avgValue = (sums[k] / counts[k]).ToString("F2", CultureInfo.InvariantCulture);
                    }
                    var avgCell = CreateCell(avgValue, true);
                    Grid.SetRow(avgCell, rowIndex);
                    Grid.SetColumn(avgCell, k + 2);
                    NotasGrid.Children.Add(avgCell);
                }
            }

            Debug.WriteLine($"=== FIM BuildTableFromHtml: {rowIndex} linhas, {headers.Count} colunas ===");
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
                // Sempre usar as propriedades dinâmicas para cores do sistema
                Foreground = isHeader ? ColorHeaderText : ColorOnSurface,
                Padding = new Thickness(12, 8, 12, 8),
                TextWrapping = TextWrapping.Wrap
            };

            // Sempre usar as propriedades dinâmicas para cores do sistema
            var borderBrush = isHeader
                ? (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : (SolidColorBrush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];

            return new Border
            {
                Child = textBlock,
                // Sempre usar as propriedades dinâmicas para cores do sistema
                Background = isHeader ? ColorHeaderBg : ColorFundoCartao,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(0)
            };
        }

        private void ApplyCellStyling(Border cell, string cssClass)
        {
            if (!(cell.Child is TextBlock textBlock)) return;

            // Cores de destaque (verde, amarelo, vermelho) são fixas e não seguem o tema
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

        // Limpar recursos quando a página for destruída
        protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            this.ActualThemeChanged -= OnThemeChanged;
            base.OnNavigatedFrom(e);
        }
    }
}

