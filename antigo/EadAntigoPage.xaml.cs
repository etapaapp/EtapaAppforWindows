using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
using System.Linq;

namespace EtapaApp
{
    public sealed partial class EadAntigoPage : Page
    {
        private const string AUTH_CHECK_URL = "https://areaexclusiva.colegioetapa.com.br/provas/notas";
        private const string TARGET_URL = "xxxxxx";
        private const string BaseUrl = "https://areaexclusiva.colegioetapa.com.br";

        public EadAntigoPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            await CheckAuthenticationAndNavigateAsync();
        }

        private async Task CheckAuthenticationAndNavigateAsync()
        {
            ShowLoadingState();

            try
            {
                if (!IsOnline())
                {
                    ShowErrorUI("Voc� est� offline.", "Conecte-se � internet para acessar os EADs.");
                    return;
                }

                bool isAuthenticated = await CheckAuthenticationAsync();
                if (!isAuthenticated)
                {
                    ShowErrorUI("Autentica��o necess�ria.", "Fa�a login no aplicativo para acessar os EADs.");
                    return;
                }

                // Se autenticado, navega diretamente para a p�gina
                NavigateToWebView();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro na verifica��o: {ex.Message}");
                ShowErrorUI("Erro de Verifica��o", "Ocorreu um erro ao verificar a autentica��o. Tente novamente.");
            }
        }

        private void ShowLoadingState()
        {
            LoadingRing.IsActive = true;
            ErrorPanel.Visibility = Visibility.Collapsed;
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
                string html = await FetchHtmlAsync(AUTH_CHECK_URL);

                // Verifica��o focada na presen�a da tabela de notas
                // A tabela cont�m a estrutura espec�fica da tabela de notas do sistema
                bool hasTable = html.Contains("<table") &&
                               html.Contains("</table>") &&
                               html.Contains("Mat�ria") &&
                               html.Contains("C�digo") &&
                               html.Contains("Conjunto");

                Debug.WriteLine($"Auth check result: {hasTable}");
                Debug.WriteLine($"Contains table: {html.Contains("<table")}");
                Debug.WriteLine($"Contains Mat�ria: {html.Contains("Mat�ria")}");
                Debug.WriteLine($"Contains C�digo: {html.Contains("C�digo")}");
                Debug.WriteLine($"Contains Conjunto: {html.Contains("Conjunto")}");

                return hasTable;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro na autentica��o: {ex.Message}");
                return false;
            }
        }

        private async Task<string> FetchHtmlAsync(string url)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; EtapaApp/1.0)");
            client.Timeout = TimeSpan.FromSeconds(30);

            // Obter cookies da WebView (mesma l�gica do ProvasAntigasPage)
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
                // Usar a mesma l�gica do ProvasAntigasPage para obter cookies
                return await GetCookiesAsync(new Uri(BaseUrl));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao obter cookies: {ex.Message}");
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
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao acessar cookies da WebView: {ex.Message}");
                return null;
            }
        }

        private void NavigateToWebView()
        {
            try
            {
                LoadingRing.IsActive = false;
                // Navega para a p�gina do WebView com a URL de destino
                Frame.Navigate(typeof(WebViewPage), TARGET_URL);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao navegar para WebView: {ex.Message}");
                ShowErrorUI("Erro de Navega��o", "N�o foi poss�vel abrir a p�gina. Tente novamente.");
            }
        }

        private void ShowErrorUI(string title, string message)
        {
            try
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    LoadingRing.IsActive = false;
                    ErrorTitleText.Text = title;
                    ErrorMessageText.Text = message;
                    ErrorPanel.Visibility = Visibility.Visible;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao mostrar UI de erro: {ex.Message}");
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            // Cleanup � autom�tico com using statements
        }
    }
}