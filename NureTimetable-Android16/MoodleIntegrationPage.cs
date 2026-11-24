using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace NureTimetable.Maui;

public sealed class MoodleIntegrationPage : ContentPage
{
    private readonly LocalStore _store;
    private readonly MoodleService _moodle = new();
    private readonly VerticalStackLayout _body = new() { Spacing = 10, Padding = 14 };
    private Entry? _login;
    private Entry? _password;
    private Label? _status;
    private UserSettings _settings = new();

    public MoodleIntegrationPage(LocalStore store)
    {
        _store = store;
        Title = "DL NURE";
        BackgroundColor = Color.FromArgb("#F4F6FA");
        Content = new ScrollView { Content = _body };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _settings = await _store.LoadSettingsAsync();
        BuildPage();
        bool hasSession = await _moodle.HasSessionAsync();
        if (hasSession && _settings.MoodleUserId > 0)
            _status!.Text = $"Вход выполнен: {_settings.MoodleFullName} (ID {_settings.MoodleUserId})";
    }

    private void BuildPage()
    {
        _body.Clear();
        _body.Add(new Label { Text = "Интеграция с дистанционным обучением ХНУРЭ", FontSize = 19, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#172033") });
        _body.Add(new Label
        {
            Text = "Авторизация выполняется через официальный Moodle web service DL NURE. Пароль хранится только в защищённом хранилище Android.",
            FontSize = 13, TextColor = Color.FromArgb("#64748B")
        });
        _login = new Entry { Placeholder = "Логин DL NURE", Text = _settings.MoodleLogin, BackgroundColor = Colors.White };
        _password = new Entry { Placeholder = "Пароль DL NURE", IsPassword = true, BackgroundColor = Colors.White };
        _body.Add(_login);
        _body.Add(_password);
        _status = new Label { Text = "Не авторизовано", FontSize = 13, TextColor = Color.FromArgb("#64748B") };
        _body.Add(_status);
        _body.Add(MakeButton("Войти в DL NURE", async () => await LoginAsync(), true));
        _body.Add(MakeButton("Загрузить мои курсы", async () => await LoadCoursesAsync(), false));
        _body.Add(MakeButton("События календаря Moodle", async () => await LoadCalendarAsync(), false));
        _body.Add(MakeButton("Выйти из DL NURE", async () => await LogoutAsync(), false));
        _body.Add(MakeButton("Открыть сайт DL NURE", async () => await Browser.Default.OpenAsync("https://dl.nure.ua"), false));
    }

    private Button MakeButton(string text, Func<Task> action, bool primary)
    {
        var b = new Button
        {
            Text = text,
            HeightRequest = 44,
            CornerRadius = 10,
            BackgroundColor = primary ? Color.FromArgb("#2457A6") : Color.FromArgb("#E9EEF6"),
            TextColor = primary ? Colors.White : Color.FromArgb("#2457A6")
        };
        b.Clicked += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await action(); }
            catch (Exception ex) { await DisplayAlert("DL NURE", ex.Message, "ОК"); }
            finally { b.IsEnabled = true; }
        };
        return b;
    }

    private async Task LoginAsync()
    {
        string login = _login?.Text?.Trim() ?? "";
        string password = _password?.Text ?? "";
        if (login.Length == 0 || password.Length == 0)
        {
            await DisplayAlert("DL NURE", "Введи логин и пароль.", "ОК");
            return;
        }
        _status!.Text = "Проверка логина…";
        var account = await _moodle.AuthenticateAsync(login, password);
        _settings.MoodleLogin = account.Login;
        _settings.MoodleFullName = account.FullName;
        _settings.MoodleUserId = account.UserId;
        await _store.SaveSettingsAsync(_settings);
        _password!.Text = "";
        _status.Text = $"Вход выполнен: {account.FullName} (ID {account.UserId})";
        await DisplayAlert("DL NURE", "Авторизация прошла успешно.", "ОК");
    }

    private async Task LoadCoursesAsync()
    {
        if (_settings.MoodleUserId <= 0)
            throw new InvalidOperationException("Сначала войди в DL NURE.");
        _status!.Text = "Загрузка списка курсов…";
        var courses = await _moodle.GetCoursesAsync(_settings.MoodleUserId);
        _status.Text = $"Найдено курсов: {courses.Count}";
        _body.Add(new Label { Text = "Мои курсы", FontSize = 17, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#172033") });
        foreach (var course in courses.OrderBy(c => c.FullName))
        {
            var b = new Button
            {
                Text = string.IsNullOrWhiteSpace(course.ShortName) ? course.FullName : $"{course.ShortName} — {course.FullName}",
                BackgroundColor = Colors.White,
                TextColor = Color.FromArgb("#172033"),
                CornerRadius = 10,
                FontSize = 13,
                HorizontalOptions = LayoutOptions.Fill
            };
            b.Clicked += async (_, _) => await OpenCourseAsync(course);
            _body.Add(b);
        }
    }

    private async Task OpenCourseAsync(MoodleCourse course)
    {
        var modules = await _moodle.GetCourseContentsAsync(course.Id);
        if (modules.Count == 0)
        {
            await DisplayAlert(course.FullName, "В курсе пока нет доступных элементов.", "ОК");
            return;
        }
        foreach (var module in modules)
        {
            string link = module.Url;
            if (string.IsNullOrWhiteSpace(link)) link = module.Urls.FirstOrDefault() ?? "";
            if (string.IsNullOrWhiteSpace(link)) continue;
            var b = new Button
            {
                Text = $"{module.Name} · {module.ModName}",
                BackgroundColor = Color.FromArgb("#E9EEF6"),
                TextColor = Color.FromArgb("#2457A6"),
                CornerRadius = 9
            };
            b.Clicked += async (_, _) => await Browser.Default.OpenAsync(link);
            _body.Add(b);
        }
    }

    private async Task LoadCalendarAsync()
    {
        var events = await _moodle.GetUpcomingEventsAsync();
        if (events.Count == 0)
        {
            await DisplayAlert("Календарь Moodle", "Предстоящих событий нет.", "ОК");
            return;
        }
        foreach (var ev in events.OrderBy(e => e.StartTime))
        {
            var time = ev.StartTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(ev.StartTime).ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "";
            var b = new Button
            {
                Text = $"{time}  {ev.Name}",
                BackgroundColor = Colors.White,
                TextColor = Color.FromArgb("#172033"),
                CornerRadius = 9,
                FontSize = 13
            };
            if (!string.IsNullOrWhiteSpace(ev.Url))
                b.Clicked += async (_, _) => await Browser.Default.OpenAsync(ev.Url);
            _body.Add(b);
        }
    }

    private async Task LogoutAsync()
    {
        await _moodle.LogoutAsync();
        _settings.MoodleLogin = "";
        _settings.MoodleFullName = "";
        _settings.MoodleUserId = 0;
        await _store.SaveSettingsAsync(_settings);
        _status!.Text = "Вы вышли из DL NURE";
        if (_password != null) _password.Text = "";
    }
}
