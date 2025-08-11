using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Networking.Connectivity;
using System.Diagnostics;
using Windows.System;
using Windows.Storage.Pickers;
using System.Runtime.InteropServices;
using HttpClient = System.Net.Http.HttpClient;
using HttpStatusCode = System.Net.HttpStatusCode;
using Microsoft.UI.Xaml.Navigation; // Adicionado para NavigationCacheMode

namespace EtapaApp
{
    public sealed partial class ProvasAntigasPage : Page
    {
        private const string GITHUB_PAT = "xxxxxx";
        private const string GITHUB_API_BASE = "https://api.github.com";
        private const string REPO_OWNER = "etapaapp";
        private const string REPO_NAME = "schooltests";
        private const string BaseUrl = "https://areaexclusiva.colegioetapa.com.br";

        public class RepoItem
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public string Path { get; set; }
            public string DownloadUrl { get; set; }
            public string TypeDescription => Type == "dir" ? "Pasta" : GetFileTypeDescription();

            private string GetFileTypeDescription()
            {
                var extension = System.IO.Path.GetExtension(Name)?.ToLower();
                return extension switch
                {
                    ".pdf" => "PDF",
                    ".doc" or ".docx" => "Word",
                    ".xls" or ".xlsx" => "Excel",
                    ".ppt" or ".pptx" => "PowerPoint",
                    ".txt" => "Texto",
                    ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" => "Imagem",
                    ".mp4" or ".avi" or ".mov" or ".wmv" => "Vídeo",
                    ".mp3" or ".wav" or ".wma" => "Áudio",
                    ".zip" or ".rar" or ".7z" => "Arquivo",
                    _ => "Arquivo"
                };
            }
        }

        private List<RepoItem> _allItems = new List<RepoItem>();
        private string _currentPath = "";
        private Stack<string> _navigationHistory = new Stack<string>();

        public ProvasAntigasPage()
        {
            this.InitializeComponent();

            // --- SOLUÇÃO PERSISTÊNCIA DE TELA ---
            // Habilita o cache da página para manter o estado ao trocar de aba.
            this.NavigationCacheMode = NavigationCacheMode.Required;
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            // Carrega os itens apenas se a lista estiver vazia (primeira carga)
            if (_allItems.Count == 0)
            {
                await CheckAuthenticationAndLoadAsync();
            }
        }

        private async Task CheckAuthenticationAndLoadAsync()
        {
            ShowLoadingState();

            try
            {
                if (!IsOnline())
                {
                    ShowOfflineState();
                    return;
                }

                bool isAuthenticated = await CheckAuthenticationAsync();
                if (!isAuthenticated)
                {
                    ShowUnauthenticatedState();
                    return;
                }

                await LoadItemsAsync(_currentPath);
            }
            catch (Exception ex)
            {
                ShowErrorState("Erro inesperado", ex.Message);
            }
        }

        private void ShowLoadingState()
        {
            LoadingRing.IsActive = true;
            StatusBar.IsOpen = false;
            ItemsListView.Visibility = Visibility.Collapsed;
            EmptyFolderPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            TopToolbar.Visibility = Visibility.Visible;
        }

        private void ShowOfflineState()
        {
            LoadingRing.IsActive = false;
            ItemsListView.Visibility = Visibility.Collapsed;
            EmptyFolderPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            TopToolbar.Visibility = Visibility.Collapsed;

            ErrorTitleText.Text = "Você está offline.";
            ErrorMessageText.Text = "Conecte-se à internet para acessar as provas.";
        }

        private void ShowUnauthenticatedState()
        {
            LoadingRing.IsActive = false;
            ItemsListView.Visibility = Visibility.Collapsed;
            EmptyFolderPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            TopToolbar.Visibility = Visibility.Collapsed;

            ErrorTitleText.Text = "Autenticação necessária.";
            ErrorMessageText.Text = "Faça login no aplicativo para acessar as provas.";
        }

        private void ShowErrorState(string title, string message)
        {
            LoadingRing.IsActive = false;
            ItemsListView.Visibility = Visibility.Collapsed;
            EmptyFolderPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            TopToolbar.Visibility = Visibility.Visible;

            ErrorTitleText.Text = title;
            ErrorMessageText.Text = message;
        }

        private void ShowContentState()
        {
            LoadingRing.IsActive = false;
            ErrorPanel.Visibility = Visibility.Collapsed;
            TopToolbar.Visibility = Visibility.Visible;
        }

        private bool IsOnline()
        {
            var connectionProfile = NetworkInformation.GetInternetConnectionProfile();
            return connectionProfile != null &&
                   connectionProfile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
        }

        private async Task<bool> CheckAuthenticationAsync()
        {
            try
            {
                string url = $"{BaseUrl}/provas/notas";
                string html = await FetchHtmlAsync(url);

                bool hasTable = html.Contains("<table") &&
                               html.Contains("</table>") &&
                               html.Contains("Matéria");

                return hasTable;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro na autenticação: {ex.Message}");
                return false;
            }
        }

        private async Task<string> FetchHtmlAsync(string url)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; EtapaApp/1.0)");

            var cookies = await GetSessionCookies();
            if (!string.IsNullOrEmpty(cookies))
            {
                client.DefaultRequestHeaders.Add("Cookie", cookies);
            }

            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private async Task<string> GetSessionCookies()
        {
            try
            {
                return await GetCookiesAsync(new Uri(BaseUrl));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static async Task<string> GetCookiesAsync(Uri uri)
        {
            if (WebViewPage.CurrentWebView == null)
                return null;

            try
            {
                await WebViewPage.CurrentWebView.EnsureCoreWebView2Async();
                if (WebViewPage.CurrentWebView.CoreWebView2 == null)
                    return null;

                var cookieManager = WebViewPage.CurrentWebView.CoreWebView2.CookieManager;
                var webViewCookies = await cookieManager.GetCookiesAsync(uri.ToString());

                if (webViewCookies == null || webViewCookies.Count == 0)
                    return null;

                return string.Join("; ", webViewCookies.Select(c => $"{c.Name}={c.Value}"));
            }
            catch
            {
                return null;
            }
        }

        private async Task LoadItemsAsync(string path)
        {
            ShowLoadingState();

            try
            {
                var items = await FetchRepoItemsAsync(path);
                _allItems = items;
                UpdateListView(items);
                BackButton.IsEnabled = _navigationHistory.Count > 0;

                ShowContentState();

                if (items.Count == 0)
                {
                    EmptyFolderPanel.Visibility = Visibility.Visible;
                    ItemsListView.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ItemsListView.Visibility = Visibility.Visible;
                    EmptyFolderPanel.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                ShowErrorState("Erro ao carregar", ex.Message);
            }
        }

        private async Task<List<RepoItem>> FetchRepoItemsAsync(string path)
        {
            string apiUrl = $"{GITHUB_API_BASE}/repos/{REPO_OWNER}/{REPO_NAME}/contents/{path}";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "EtapaApp");
            client.DefaultRequestHeaders.Add("Authorization", $"token {GITHUB_PAT}");

            var response = await client.GetAsync(apiUrl);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new List<RepoItem>();
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            return ParseGitHubResponse(json);
        }

        private List<RepoItem> ParseGitHubResponse(string json)
        {
            var items = new List<RepoItem>();

            try
            {
                using var document = JsonDocument.Parse(json);
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    items.Add(new RepoItem
                    {
                        Name = element.GetProperty("name").GetString() ?? "",
                        Type = element.GetProperty("type").GetString() ?? "",
                        Path = element.GetProperty("path").GetString() ?? "",
                        DownloadUrl = element.TryGetProperty("download_url", out var dl) ?
                                       dl.GetString() ?? "" : ""
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro no parse: {ex.Message}");
            }

            return items;
        }

        private void UpdateListView(List<RepoItem> items)
        {
            ItemsListView.ItemsSource = items
                .OrderBy(item => item.Type == "dir" ? 0 : 1)
                .ThenBy(item => item.Name)
                .ToList();
        }

        private async void ItemsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not RepoItem item) return;

            if (item.Type == "dir")
            {
                _navigationHistory.Push(_currentPath);
                _currentPath = item.Path;
                await LoadItemsAsync(_currentPath);
            }
            else
            {
                await DownloadFileAsync(item);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private async Task DownloadFileAsync(RepoItem item)
        {
            try
            {
                var folderPicker = new FolderPicker();
                folderPicker.SuggestedStartLocation = PickerLocationId.Downloads;
                folderPicker.FileTypeFilter.Add("*");

                IntPtr hwnd = GetForegroundWindow();
                WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

                var folder = await folderPicker.PickSingleFolderAsync();
                if (folder == null) return;

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "EtapaApp");

                var response = await client.GetAsync(item.DownloadUrl);
                if (response.IsSuccessStatusCode)
                {
                    var fileBytes = await response.Content.ReadAsByteArrayAsync();
                    var file = await folder.CreateFileAsync(item.Name, CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteBytesAsync(file, fileBytes);

                    ShowSuccess($"'{item.Name}' baixado com sucesso!");
                    await Launcher.LaunchFolderAsync(folder);
                }
                else
                {
                    ShowDownloadError("Falha no download. Tente novamente.");
                }
            }
            catch (Exception ex)
            {
                ShowDownloadError($"Erro: {ex.Message}");
            }
        }

        private async void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_navigationHistory.Count > 0)
            {
                _currentPath = _navigationHistory.Pop();
                await LoadItemsAsync(_currentPath);
            }
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                var query = sender.Text.ToLower();
                UpdateListView(_allItems.Where(item =>
                    item.Name.ToLower().Contains(query)).ToList());
            }
        }

        private void ShowDownloadError(string message)
        {
            StatusBar.IsOpen = true;
            StatusBar.Title = "Erro";
            StatusBar.Message = message;
            StatusBar.Severity = InfoBarSeverity.Error;
        }

        private void ShowSuccess(string message)
        {
            StatusBar.IsOpen = true;
            StatusBar.Title = "Sucesso";
            StatusBar.Message = message;
            StatusBar.Severity = InfoBarSeverity.Success;
        }
    }
}
