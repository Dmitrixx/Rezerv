using Microsoft.Maui.Controls;
using Microsoft.Maui.Networking;
using System.Globalization;
namespace NureTimetable.Maui;
public partial class MainPage : ContentPage
{
    private const string CistHomeUrl = "https://cist.nure.ua/ias/app/tt/f?p=778:2";
    private bool _isNavigating;
    public MainPage()
    {
        InitializeComponent();
        DateLabel.Text = DateTime.Now.ToString("dddd, d MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU"));
        UpdateConnectionStatus();
    }
    private void UpdateConnectionStatus()
    {
        StatusLabel.Text = Connectivity.Current.NetworkAccess == NetworkAccess.Internet
            ? "Подключение к сайту расписания ХНУРЭ"
            : "Нет подключения к интернету. Проверьте Wi-Fi или мобильную сеть.";
    }
    private async void OnRefreshClicked(object sender, EventArgs e)
    {
        if (_isNavigating) return;
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            await DisplayAlert("Нет интернета", "Подключитесь к Wi-Fi или мобильному интернету и повторите попытку.", "ОК");
            return;
        }
        CistWebView.Reload();
    }
    private void OnBackClicked(object sender, EventArgs e)
    {
        if (CistWebView.CanGoBack) CistWebView.GoBack();
    }
    private void OnForwardClicked(object sender, EventArgs e)
    {
        if (CistWebView.CanGoForward) CistWebView.GoForward();
    }
    private void OnHomeClicked(object sender, EventArgs e)
    {
        CistWebView.Source = CistHomeUrl;
    }
    private void OnWebViewNavigating(object? sender, WebNavigatingEventArgs e)
    {
        _isNavigating = true;
        StatusLabel.Text = "Загрузка страницы CIST…";
    }
    private void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        _isNavigating = false;
        StatusLabel.Text = e.Result == WebNavigationResult.Success
            ? "Сайт CIST открыт. Выберите группу или преподавателя на странице."
            : "Не удалось открыть CIST. Проверьте интернет и нажмите «Обновить».";
    }
    protected override bool OnBackButtonPressed()
    {
        if (CistWebView.CanGoBack)
        {
            CistWebView.GoBack();
            return true;
        }
        return base.OnBackButtonPressed();
    }
}