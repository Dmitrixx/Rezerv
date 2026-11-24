using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;

namespace NureTimetable.Maui;

public partial class MainPage : ContentPage
{
    private readonly LocalStore _store = new();
    private readonly CistService _cist;
    private List<TimetableEntity> _entities = new();
    private List<TimetableEvent> _events = new();
    private List<LessonPreference> _preferences = new();
    private UserSettings _settings = new();
    private DateTime _selectedDate = DateTime.Today;
    private string _tab = "schedule";
    private string _searchText = "";
    private string _typeFilter = "";
    private bool _busy;
    private Label? _timerLabel;
    private Label? _statusLabel;
    private VerticalStackLayout? _pageHost;
    private Button? _refreshButton;
    private readonly Color _brand = Color.FromArgb("#2457A6");
    private readonly Color _ink = Color.FromArgb("#172033");
    private readonly Color _muted = Color.FromArgb("#64748B");
    private readonly Color _surface = Colors.White;

    public MainPage()
    {
        InitializeComponent();
        _cist = new CistService(_store);
        BuildShell();
        Dispatcher.StartTimer(TimeSpan.FromMinutes(1), () =>
        {
            if (_timerLabel != null && _tab == "schedule") _timerLabel.Text = GetCountdownText();
            return true;
        });
    }

    private void BuildShell()
    {
        Root.RowDefinitions.Clear();
        Root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Root.RowDefinitions.Add(new RowDefinition(GridLength.Star));

        var header = new Grid
        {
            Padding = new Thickness(16, 12),
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            BackgroundColor = _surface
        };
        var titleStack = new VerticalStackLayout { Spacing = 1 };
        titleStack.Add(new Label { Text = "NURE TIMETABLE", FontSize = 19, FontAttributes = FontAttributes.Bold, TextColor = _ink });
        _statusLabel = new Label { Text = "Расписание ХНУРЭ", FontSize = 12, TextColor = _muted };
        titleStack.Add(_statusLabel);
        header.Add(titleStack, 0, 0);
        _refreshButton = MakeButton("↻", _brand, Colors.White, async () => await RefreshSchedulesAsync(), 42);
        header.Add(_refreshButton, 1, 0);
        Root.Add(header, 0, 0);

        var nav = new Grid
        {
            Padding = new Thickness(10, 8),
            ColumnSpacing = 6,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)
            },
            BackgroundColor = _surface
        };
        string[] tabs = { "Расписание", "Мои", "Занятия", "Настройки" };
        string[] keys = { "schedule", "entities", "lessons", "settings" };
        for (int i = 0; i < tabs.Length; i++)
        {
            string key = keys[i];
            var b = MakeButton(tabs[i], key == _tab ? _brand : Color.FromArgb("#E9EEF6"),
                key == _tab ? Colors.White : _ink, () => SwitchTab(key), 40);
            b.FontSize = 12;
            nav.Add(b, i, 0);
        }
        Root.Add(nav, 0, 1);

        _pageHost = new VerticalStackLayout { Spacing = 10, Padding = new Thickness(12, 12, 12, 24) };
        var contentScroll = new ScrollView { Content = _pageHost };
        Root.Add(contentScroll, 0, 2);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadLocalStateAsync();
        await RefreshViewAsync();
        if (_settings.AutoUpdate && Connectivity.Current.NetworkAccess == NetworkAccess.Internet &&
            (DateTime.Now - _settings.LastForegroundSync).TotalHours >= 12)
            await RefreshSchedulesAsync(silent: true);
    }

    private async Task LoadLocalStateAsync()
    {
        _settings = await _store.LoadSettingsAsync();
        _entities = await _store.LoadEntitiesAsync();
        _preferences = await _store.LoadPreferencesAsync();
        ApplyTheme();
        _events = await _store.LoadSelectedEventsAsync(_entities);
        foreach (var ev in _events)
        {
            if (string.IsNullOrEmpty(ev.EntityKey))
            {
                var candidate = _entities.FirstOrDefault(e => e.IsSelected);
                if (candidate != null) ev.EntityKey = candidate.StorageKey;
            }
        }
    }

    private void ApplyTheme()
    {
        Application.Current!.UserAppTheme = _settings.Theme switch
        {
            "dark" => AppTheme.Dark,
            "light" => AppTheme.Light,
            _ => AppTheme.Unspecified
        };
    }

    private async void SwitchTab(string tab)
    {
        _tab = tab;
        await RefreshViewAsync();
        BuildShell();
    }

    private async Task RefreshViewAsync()
    {
        if (_pageHost == null) return;
        _pageHost.Clear();
        switch (_tab)
        {
            case "schedule": await BuildScheduleViewAsync(); break;
            case "entities": await BuildEntitiesViewAsync(); break;
            case "lessons": await BuildLessonsViewAsync(); break;
            case "settings": BuildSettingsView(); break;
        }
        UpdateHeaderStatus();
    }

    private void UpdateHeaderStatus()
    {
        if (_statusLabel == null) return;
        int count = _entities.Count(e => e.IsSelected);
        _statusLabel.Text = count == 0
            ? "Добавьте группу, преподавателя или аудиторию"
            : $"{count} расписаний выбрано · {_events.Count} занятий в кэше";
    }

    private async Task BuildScheduleViewAsync()
    {
        if (_pageHost == null) return;
        var selected = _entities.Where(e => e.IsSelected).ToList();
        var entityRow = new HorizontalStackLayout { Spacing = 8 };
        foreach (var entity in selected)
        {
            var chip = MakeButton(entity.Name, _brand, Colors.White, async () =>
            {
                foreach (var e in _entities) e.IsSelected = e.SameAs(entity);
                await SaveEntitiesAsync();
                await ReloadEventsAsync();
                await RefreshViewAsync();
            }, 38);
            chip.FontSize = 12;
            entityRow.Add(chip);
        }
        var addButton = MakeButton("+ Добавить", Color.FromArgb("#E9EEF6"), _brand, AddEntityAsync, 38);
        addButton.FontSize = 12;
        entityRow.Add(addButton);
        _pageHost.Add(new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = entityRow });

        var dateRow = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            ColumnSpacing = 8
        };
        dateRow.Add(MakeButton("‹", Color.FromArgb("#E9EEF6"), _ink, () => ChangeDate(-1), 38), 0, 0);
        var dateText = _settings.ViewMode switch
        {
            "day" => _selectedDate.ToString("dd MMMM yyyy", CultureInfo.GetCultureInfo(_settings.Language == "uk" ? "uk-UA" : "ru-RU")),
            "month" => _selectedDate.ToString("MMMM yyyy", CultureInfo.GetCultureInfo(_settings.Language == "uk" ? "uk-UA" : "ru-RU")),
            _ => WeekCaption(_selectedDate)
        };
        dateRow.Add(new Label { Text = dateText, FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = _ink, VerticalTextAlignment = TextAlignment.Center, HorizontalTextAlignment = TextAlignment.Center }, 1, 0);
        dateRow.Add(MakeButton("›", Color.FromArgb("#E9EEF6"), _ink, () => ChangeDate(1), 38), 2, 0);
        _pageHost.Add(dateRow);

        var modes = new HorizontalStackLayout { Spacing = 6 };
        foreach (var (label, mode) in new[] { ("День", "day"), ("Неделя", "week"), ("Месяц", "month") })
        {
            var button = MakeButton(label, _settings.ViewMode == mode ? _brand : Color.FromArgb("#E9EEF6"),
                _settings.ViewMode == mode ? Colors.White : _ink, async () =>
                {
                    _settings.ViewMode = mode;
                    await _store.SaveSettingsAsync(_settings);
                    await RefreshViewAsync();
                }, 36);
            button.FontSize = 12;
            modes.Add(button);
        }
        modes.Add(MakeButton("Сегодня", Color.FromArgb("#E9EEF6"), _ink, async () =>
        {
            _selectedDate = DateTime.Today;
            await RefreshViewAsync();
        }, 36));
        _pageHost.Add(modes);

        _timerLabel = new Label
        {
            Text = GetCountdownText(),
            TextColor = _brand,
            FontAttributes = FontAttributes.Bold,
            FontSize = 14,
            Padding = new Thickness(12, 8),
            BackgroundColor = Color.FromArgb("#EAF1FF")
        };
        _pageHost.Add(new Border
        {
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Content = _timerLabel,
            BackgroundColor = Color.FromArgb("#EAF1FF")
        });

        var filterRow = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            ColumnSpacing = 8
        };
        var search = new SearchBar
        {
            Placeholder = "Поиск по предмету, аудитории или преподавателю",
            Text = _searchText,
            BackgroundColor = _surface,
            HeightRequest = 44
        };
        search.TextChanged += async (_, e) =>
        {
            _searchText = e.NewTextValue ?? "";
            await RebuildEventsAsync();
        };
        filterRow.Add(search, 0, 0);
        filterRow.Add(MakeButton(_typeFilter.Length == 0 ? "Фильтр" : _typeFilter, Color.FromArgb("#E9EEF6"), _ink, ChooseFilterAsync, 42), 1, 0);
        _pageHost.Add(filterRow);

        var events = await GetVisibleEventsAsync();
        if (selected.Count == 0)
        {
            _pageHost.Add(EmptyCard("Пока нет сохранённых расписаний", "Добавь группу, преподавателя или аудиторию, чтобы загрузить расписание."));
            return;
        }
        if (events.Count == 0)
        {
            bool hasCache = _events.Count > 0;
            _pageHost.Add(EmptyCard(
                hasCache ? "На выбранную дату занятий нет" : "Расписание пока не загружено",
                hasCache ? "Перейди к другой дате или нажми «Обновить»." : "Нажми «Обновить» или выбери «+ Добавить» для поиска группы."));
            return;
        }

        var eventsLayout = new VerticalStackLayout { Spacing = 10 };
        foreach (var day in events.GroupBy(e => e.Start.Date).OrderBy(g => g.Key))
        {
            eventsLayout.Add(new Label
            {
                Text = day.Key.ToString("dddd, d MMMM", CultureInfo.GetCultureInfo(_settings.Language == "uk" ? "uk-UA" : "ru-RU")),
                FontSize = 15,
                FontAttributes = FontAttributes.Bold,
                TextColor = _ink,
                Margin = new Thickness(2, 8, 0, 0)
            });
            foreach (var ev in day.OrderBy(e => e.Start))
                eventsLayout.Add(CreateEventCard(ev));
        }
        _pageHost.Add(eventsLayout);
        _pageHost.Add(MakeButton("Экспортировать выбранные расписания в календарь (.ics)", Color.FromArgb("#E9EEF6"), _brand, ExportCalendarAsync, 44));
    }

    private async Task<List<TimetableEvent>> GetVisibleEventsAsync()
    {
        var (from, to) = GetDateRange();
        var result = new List<TimetableEvent>();
        foreach (var ev in _events)
        {
            if (ev.Start.Date < from || ev.Start.Date > to) continue;
            if (!_settings.ShowPastLessons && ev.End < DateTime.Now) continue;
            if (_typeFilter.Length > 0 && !ev.Type.Equals(_typeFilter, StringComparison.CurrentCultureIgnoreCase)) continue;
            if (_searchText.Length > 0)
            {
                string combined = $"{ev.Lesson} {ev.Room} {string.Join(" ", ev.Teachers)} {string.Join(" ", ev.Groups)}";
                if (!combined.Contains(_searchText, StringComparison.CurrentCultureIgnoreCase)) continue;
            }
            var pref = _preferences.FirstOrDefault(p => p.EntityKey == ev.EntityKey &&
                p.LessonKey.Equals(ev.LessonKey, StringComparison.OrdinalIgnoreCase));
            if (pref is not null)
            {
                if (pref.Hidden || pref.HiddenTypes.Contains(ev.Type)) continue;
                if (ev.Teachers.Any(t => pref.HiddenTeachers.Contains(t))) continue;
            }
            result.Add(ev);
        }
        return result.OrderBy(e => e.Start).ToList();
    }

    private (DateTime From, DateTime To) GetDateRange()
    {
        if (_settings.ViewMode == "day") return (_selectedDate.Date, _selectedDate.Date);
        if (_settings.ViewMode == "month")
        {
            var first = new DateTime(_selectedDate.Year, _selectedDate.Month, 1);
            return (first, first.AddMonths(1).AddDays(-1));
        }
        int diff = ((int)_selectedDate.DayOfWeek + 6) % 7;
        var monday = _selectedDate.Date.AddDays(-diff);
        return (monday, monday.AddDays(6));
    }

    private string WeekCaption(DateTime date)
    {
        int diff = ((int)date.DayOfWeek + 6) % 7;
        var monday = date.Date.AddDays(-diff);
        return $"{monday:dd.MM} – {monday.AddDays(6):dd.MM.yyyy}";
    }

    private async Task ChangeDate(int direction)
    {
        _selectedDate = _settings.ViewMode switch
        {
            "day" => _selectedDate.AddDays(direction),
            "month" => _selectedDate.AddMonths(direction),
            _ => _selectedDate.AddDays(direction * 7)
        };
        await RefreshViewAsync();
    }

    private async Task RebuildEventsAsync()
    {
        if (_tab == "schedule") await RefreshViewAsync();
    }

    private View CreateEventCard(TimetableEvent ev)
    {
        var title = new Label { Text = ev.Lesson, FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = _ink };
        var type = new Label { Text = string.IsNullOrWhiteSpace(ev.Type) ? "Занятие" : ev.Type, FontSize = 12, TextColor = _brand, FontAttributes = FontAttributes.Bold };
        var time = new Label { Text = $"{ev.TimeText}   {ev.Room}", FontSize = 13, TextColor = _muted };
        var detail = new Label
        {
            Text = ev.Teachers.Count > 0 ? string.Join(", ", ev.Teachers) :
                ev.Groups.Count > 0 ? string.Join(", ", ev.Groups) : ev.Description,
            FontSize = 12,
            TextColor = _muted,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 2
        };
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 12 };
        row.Add(new Border
        {
            WidthRequest = 5,
            BackgroundColor = EventColor(ev.Type),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 3 }
        }, 0, 0);
        var stack = new VerticalStackLayout { Spacing = 3 };
        stack.Add(title);
        stack.Add(type);
        stack.Add(time);
        if (!string.IsNullOrWhiteSpace(detail.Text)) stack.Add(detail);
        row.Add(stack, 1, 0);
        var border = new Border
        {
            Padding = new Thickness(12, 10),
            BackgroundColor = _surface,
            Stroke = Color.FromArgb("#E1E7EF"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            Content = row
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await OpenEventDetailsAsync(ev);
        border.GestureRecognizers.Add(tap);
        return border;
    }

    private Color EventColor(string type)
    {
        string value = (type ?? "").ToLowerInvariant();
        if (value.Contains("лк") || value.Contains("лек")) return Color.FromArgb("#3B82F6");
        if (value.Contains("пз") || value.Contains("практ")) return Color.FromArgb("#16A34A");
        if (value.Contains("лб") || value.Contains("лаб")) return Color.FromArgb("#D97706");
        return Color.FromArgb("#8B5CF6");
    }

    private string GetCountdownText()
    {
        var now = DateTime.Now;
        var current = _events.FirstOrDefault(e => e.Start <= now && e.End > now);
        if (current != null)
        {
            var left = current.End - now;
            return $"Сейчас идёт: {current.Lesson} · до конца {FormatRemaining(left)}";
        }
        var next = _events.Where(e => e.Start > now).OrderBy(e => e.Start).FirstOrDefault();
        if (next == null) return "Ближайших занятий в загруженном расписании нет";
        return $"До следующей пары «{next.Lesson}» — {FormatRemaining(next.Start - now)}";
    }

    private static string FormatRemaining(TimeSpan value)
    {
        if (value.TotalMinutes < 1) return "менее минуты";
        if (value.TotalDays >= 1) return $"{(int)value.TotalDays} дн. {value.Hours} ч.";
        if (value.TotalHours >= 1) return $"{(int)value.TotalHours} ч. {value.Minutes} мин.";
        return $"{Math.Max(1, value.Minutes)} мин.";
    }

    private async Task BuildEntitiesViewAsync()
    {
        if (_pageHost == null) return;
        _pageHost.Add(new Label { Text = "Мои расписания", FontSize = 21, FontAttributes = FontAttributes.Bold, TextColor = _ink });
        _pageHost.Add(new Label { Text = "Отметь расписания, которые нужно показывать одновременно. Сохранённые данные доступны без интернета.", FontSize = 13, TextColor = _muted });
        _pageHost.Add(MakeButton("+ Добавить группу / преподавателя / аудиторию", _brand, Colors.White, AddEntityAsync, 46));

        if (_entities.Count == 0)
        {
            _pageHost.Add(EmptyCard("Список пуст", "Добавь первое расписание."));
            return;
        }
        foreach (var entity in _entities.OrderBy(e => e.Name))
        {
            var row = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) },
                ColumnSpacing = 8
            };
            var nameStack = new VerticalStackLayout { Spacing = 1 };
            nameStack.Add(new Label { Text = entity.Name, FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = _ink });
            nameStack.Add(new Label { Text = $"{entity.KindText} · ID {entity.Id}", FontSize = 11, TextColor = _muted });
            row.Add(nameStack, 0, 0);
            var toggle = new Switch { IsToggled = entity.IsSelected, VerticalOptions = LayoutOptions.Center };
            toggle.Toggled += async (_, e) =>
            {
                entity.IsSelected = e.Value;
                await SaveEntitiesAsync();
                await ReloadEventsAsync();
                await RefreshViewAsync();
            };
            row.Add(toggle, 1, 0);
            row.Add(MakeButton("Удалить", Color.FromArgb("#FEE2E2"), Color.FromArgb("#B91C1C"), async () =>
            {
                bool yes = await DisplayAlert("Удалить расписание", $"Удалить «{entity.Name}» из списка?", "Удалить", "Отмена");
                if (!yes) return;
                _entities.RemoveAll(e => e.SameAs(entity));
                await SaveEntitiesAsync();
                await ReloadEventsAsync();
                await RefreshViewAsync();
            }, 38), 2, 0);
            _pageHost.Add(new Border
            {
                Padding = 12,
                Stroke = Color.FromArgb("#E1E7EF"),
                StrokeThickness = 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
                BackgroundColor = _surface,
                Content = row
            });
        }
        _pageHost.Add(MakeButton("Обновить отмеченные расписания", Color.FromArgb("#E9EEF6"), _brand, RefreshSchedulesAsync, 44));
    }

    private async Task BuildLessonsViewAsync()
    {
        if (_pageHost == null) return;
        _pageHost.Add(new Label { Text = "Занятия и статистика", FontSize = 21, FontAttributes = FontAttributes.Bold, TextColor = _ink });
        if (_events.Count == 0)
        {
            _pageHost.Add(EmptyCard("Нет загруженных занятий", "Сначала загрузи хотя бы одно расписание."));
            return;
        }

        foreach (var group in _events.GroupBy(e => e.Lesson).OrderBy(g => g.Key))
        {
            var list = group.OrderBy(e => e.Start).ToList();
            int remaining = list.Count(e => e.Start > DateTime.Now);
            var next = list.FirstOrDefault(e => e.Start > DateTime.Now);
            var teachers = list.SelectMany(e => e.Teachers).Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(s => s).ToList();
            int lectures = list.Count(e => e.Type.Contains("лк", StringComparison.OrdinalIgnoreCase) || e.Type.Contains("лек", StringComparison.OrdinalIgnoreCase));
            int practices = list.Count(e => e.Type.Contains("пз", StringComparison.OrdinalIgnoreCase) || e.Type.Contains("практ", StringComparison.OrdinalIgnoreCase));
            int labs = list.Count(e => e.Type.Contains("лб", StringComparison.OrdinalIgnoreCase) || e.Type.Contains("лаб", StringComparison.OrdinalIgnoreCase));
            var entityKey = list.First().EntityKey;
            var pref = await _store.GetPreferenceAsync(entityKey, group.Key.Trim().ToUpperInvariant());
            var info = new VerticalStackLayout { Spacing = 4 };
            info.Add(new Label { Text = group.Key, FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = _ink });
            info.Add(new Label { Text = $"Всего: {list.Count} · Осталось: {remaining}", FontSize = 13, TextColor = _muted });
            info.Add(new Label { Text = $"Лекции: {lectures} · Практика: {practices} · Лабораторные: {labs}", FontSize = 12, TextColor = _muted });
            info.Add(new Label { Text = $"Следующее: {(next == null ? "—" : next.Start.ToString("dd.MM.yyyy HH:mm"))}", FontSize = 12, TextColor = _muted });
            info.Add(new Label { Text = $"Преподаватели: {(teachers.Count == 0 ? "не указаны" : string.Join(", ", teachers))}", FontSize = 12, TextColor = _muted });
            if (!string.IsNullOrWhiteSpace(pref.Notes))
                info.Add(new Label { Text = "Заметка: " + pref.Notes, FontSize = 12, TextColor = _brand, MaxLines = 2 });
            var buttons = new HorizontalStackLayout { Spacing = 8 };
            buttons.Add(MakeButton("Заметка", Color.FromArgb("#E9EEF6"), _brand, async () => await EditLessonPreferenceAsync(list[0]), 36));
            buttons.Add(MakeButton(pref.Hidden ? "Показать" : "Скрыть занятие", pref.Hidden ? Color.FromArgb("#DCFCE7") : Color.FromArgb("#FEE2E2"),
                pref.Hidden ? Color.FromArgb("#166534") : Color.FromArgb("#B91C1C"), async () =>
                {
                    pref.Hidden = !pref.Hidden;
                    await _store.SavePreferenceAsync(pref);
                    _preferences = await _store.LoadPreferencesAsync();
                    await RefreshViewAsync();
                }, 36));
            info.Add(buttons);
            _pageHost.Add(new Border
            {
                Padding = 12,
                Stroke = Color.FromArgb("#E1E7EF"),
                StrokeThickness = 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
                BackgroundColor = _surface,
                Content = info
            });
        }
    }

    private void BuildSettingsView()
    {
        if (_pageHost == null) return;
        _pageHost.Add(new Label { Text = "Настройки", FontSize = 21, FontAttributes = FontAttributes.Bold, TextColor = _ink });
        AddSettingsRow("Тема оформления", _settings.Theme switch { "dark" => "Тёмная", "light" => "Светлая", _ => "Как в системе" },
            async () =>
            {
                var answer = await DisplayActionSheet("Тема", "Отмена", null, "Как в системе", "Светлая", "Тёмная");
                _settings.Theme = answer switch { "Тёмная" => "dark", "Светлая" => "light", _ => "system" };
                ApplyTheme();
                await SaveSettingsAsync();
                await RefreshViewAsync();
            });
        AddSettingsRow("Язык", _settings.Language.ToUpperInvariant(), async () =>
        {
            var answer = await DisplayActionSheet("Язык интерфейса", "Отмена", null, "Русский", "Українська", "English");
            _settings.Language = answer switch { "Українська" => "uk", "English" => "en", _ => "ru" };
            await SaveSettingsAsync();
            await RefreshViewAsync();
        });
        var auto = new Switch { IsToggled = _settings.AutoUpdate };
        auto.Toggled += async (_, e) => { _settings.AutoUpdate = e.Value; await SaveSettingsAsync(); };
        _pageHost.Add(SettingsSwitchRow("Автообновление при открытии (не чаще 12 часов)", auto));

        var past = new Switch { IsToggled = _settings.ShowPastLessons };
        past.Toggled += async (_, e) => { _settings.ShowPastLessons = e.Value; await SaveSettingsAsync(); await RefreshViewAsync(); };
        _pageHost.Add(SettingsSwitchRow("Показывать прошедшие занятия", past));

        AddSettingsRow("Часовой пояс", _settings.TimeZoneId, async () =>
        {
            var answer = await DisplayActionSheet("Часовой пояс", "Отмена", null, "Europe/Kyiv", "Europe/Warsaw", "Europe/Berlin", "UTC", "Системный");
            _settings.TimeZoneId = answer == "Системный" || string.IsNullOrWhiteSpace(answer) ? TimeZoneInfo.Local.Id : answer;
            await SaveSettingsAsync();
        });
        _pageHost.Add(MakeButton("Войти в дистанционное обучение ХНУРЭ", Color.FromArgb("#E9EEF6"), _brand,
            async () => await Browser.Default.OpenAsync("https://dl.nure.ua/login/index.php"), 46));
        _pageHost.Add(MakeButton("Открыть официальный сайт расписания", Color.FromArgb("#E9EEF6"), _brand,
            async () => await Browser.Default.OpenAsync("https://cist.nure.ua/ias/app/tt/f?p=778:2"), 46));
        _pageHost.Add(MakeButton("Экспорт расписания в файл календаря", _brand, Colors.White, ExportCalendarAsync, 46));
        _pageHost.Add(new Label { Text = "Nure Timetable Android 16 Edition · данные расписания предоставляются CIST ХНУРЭ.", FontSize = 11, TextColor = _muted, Margin = new Thickness(0, 10) });
    }

    private void AddSettingsRow(string title, string value, Func<Task> action)
    {
        var button = new Button
        {
            Text = $"{title}\n{value}",
            BackgroundColor = _surface,
            TextColor = _ink,
            CornerRadius = 12,
            HorizontalOptions = LayoutOptions.Fill,
            HeightRequest = 58,
            FontSize = 13,
            Padding = 12
        };
        button.Clicked += async (_, _) => await action();
        _pageHost?.Add(button);
    }

    private View SettingsSwitchRow(string title, Switch toggle)
    {
        var grid = new Grid
        {
            Padding = 12,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            BackgroundColor = _surface
        };
        grid.Add(new Label { Text = title, TextColor = _ink, FontSize = 13, VerticalTextAlignment = TextAlignment.Center }, 0, 0);
        grid.Add(toggle, 1, 0);
        return new Border
        {
            Stroke = Color.FromArgb("#E1E7EF"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Content = grid
        };
    }

    private async Task OpenEventDetailsAsync(TimetableEvent ev)
    {
        var entity = _entities.FirstOrDefault(e => e.StorageKey == ev.EntityKey) ?? _entities.FirstOrDefault(e => e.IsSelected);
        if (entity == null) return;
        var pref = await _store.GetPreferenceAsync(entity.StorageKey, ev.LessonKey);
        var page = new ContentPage { Title = ev.Lesson, BackgroundColor = Color.FromArgb("#F4F6FA"), Padding = 18 };
        var body = new VerticalStackLayout { Spacing = 10 };
        body.Add(new Label { Text = ev.Lesson, FontSize = 21, FontAttributes = FontAttributes.Bold, TextColor = _ink });
        body.Add(new Label { Text = $"{ev.Start:dddd, dd.MM.yyyy} · {ev.TimeText}", FontSize = 14, TextColor = _muted });
        body.Add(new Label { Text = $"Тип: {ev.Type}\nАудитория: {ev.Room}\nПреподаватели: {(ev.Teachers.Count > 0 ? string.Join(", ", ev.Teachers) : "не указаны")}\nГруппы: {(ev.Groups.Count > 0 ? string.Join(", ", ev.Groups) : entity.Name)}",
            FontSize = 14, TextColor = _ink });
        if (!string.IsNullOrWhiteSpace(ev.Description))
            body.Add(new Label { Text = "Описание: " + ev.Description, FontSize = 12, TextColor = _muted });

        body.Add(new Label { Text = "Заметка к занятию", FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = _ink });
        var notes = new Editor { Text = pref.Notes, Placeholder = "Добавить заметку…", AutoSize = EditorAutoSizeOption.TextChanges, HeightRequest = 110, BackgroundColor = _surface, TextColor = _ink };
        body.Add(notes);
        var hideLesson = new Switch { IsToggled = pref.Hidden };
        body.Add(SettingsSwitchRow("Скрыть все занятия по этому предмету", hideLesson));
        body.Add(MakeButton("Скрыть этот тип занятия", Color.FromArgb("#FEE2E2"), Color.FromArgb("#B91C1C"), async () =>
        {
            if (string.IsNullOrWhiteSpace(ev.Type)) return;
            if (pref.HiddenTypes.Contains(ev.Type)) pref.HiddenTypes.Remove(ev.Type);
            else pref.HiddenTypes.Add(ev.Type);
            await _store.SavePreferenceAsync(pref);
            _preferences = await _store.LoadPreferencesAsync();
            await Navigation.PopModalAsync();
            await RefreshViewAsync();
        }, 42));
        foreach (var teacher in ev.Teachers.Distinct())
        {
            body.Add(MakeButton($"Скрыть / показать: {teacher}", Color.FromArgb("#E9EEF6"), _brand, async () =>
            {
                if (pref.HiddenTeachers.Contains(teacher)) pref.HiddenTeachers.Remove(teacher);
                else pref.HiddenTeachers.Add(teacher);
                await _store.SavePreferenceAsync(pref);
                _preferences = await _store.LoadPreferencesAsync();
                await Navigation.PopModalAsync();
                await RefreshViewAsync();
            }, 42));
        }
        body.Add(MakeButton("Сохранить", _brand, Colors.White, async () =>
        {
            pref.Notes = notes.Text ?? "";
            pref.Hidden = hideLesson.IsToggled;
            await _store.SavePreferenceAsync(pref);
            _preferences = await _store.LoadPreferencesAsync();
            await Navigation.PopModalAsync();
            await RefreshViewAsync();
        }, 46));
        body.Add(MakeButton("Закрыть", Color.FromArgb("#E9EEF6"), _ink, async () => await Navigation.PopModalAsync(), 42));
        page.Content = new ScrollView { Content = body };
        await Navigation.PushModalAsync(new NavigationPage(page));
    }

    private async Task EditLessonPreferenceAsync(TimetableEvent ev)
    {
        await OpenEventDetailsAsync(ev);
    }

    private async Task ChooseFilterAsync()
    {
        var types = _events.Select(e => e.Type).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(s => s).ToList();
        var options = new List<string> { "Все типы" };
        options.AddRange(types);
        options.Add("Очистить поиск");
        var answer = await DisplayActionSheet("Фильтр по типу занятия", "Отмена", null, options.ToArray());
        if (answer == "Все типы") _typeFilter = "";
        else if (answer == "Очистить поиск") { _typeFilter = ""; _searchText = ""; }
        else if (!string.IsNullOrEmpty(answer)) _typeFilter = answer;
        await RefreshViewAsync();
    }

    private async Task AddEntityAsync()
    {
        var page = new EntitySearchPage(_cist);
        await Navigation.PushModalAsync(new NavigationPage(page));
        var entity = await page.SelectionTask;
        if (entity == null) return;
        if (_entities.Any(e => e.SameAs(entity)))
        {
            await DisplayAlert("Уже добавлено", "Это расписание уже есть в списке.", "ОК");
            return;
        }
        if (_entities.Count == 0) entity.IsSelected = true;
        else entity.IsSelected = true;
        _entities.ForEach(e => e.IsSelected = false);
        _entities.Add(entity);
        await SaveEntitiesAsync();
        await RefreshSchedulesAsync(silent: true, onlyEntity: entity);
        await ReloadEventsAsync();
        await RefreshViewAsync();
    }

    private async Task RefreshSchedulesAsync(bool silent = false, TimetableEntity? onlyEntity = null)
    {
        if (_busy) return;
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            if (!silent) await DisplayAlert("Нет интернета", "Показываю сохранённое расписание. Для обновления подключись к интернету.", "ОК");
            return;
        }
        var target = onlyEntity != null ? new List<TimetableEntity> { onlyEntity } : _entities.Where(e => e.IsSelected).ToList();
        if (target.Count == 0)
        {
            if (!silent) await DisplayAlert("Нет выбранных расписаний", "Отметь хотя бы одно расписание на вкладке «Мои».", "ОК");
            return;
        }
        _busy = true;
        if (_refreshButton != null) _refreshButton.IsEnabled = false;
        if (_statusLabel != null) _statusLabel.Text = "Загружаю CIST…";
        try
        {
            foreach (var entity in target)
            {
                try
                {
                    var from = DateTime.Today.AddDays(-7);
                    var to = from.AddDays(89);
                    var events = await _cist.GetScheduleAsync(entity, from, to);
                    foreach (var ev in events) ev.EntityKey = entity.StorageKey;
                    await _store.SaveScheduleAsync(entity, events);
                    entity.LastUpdated = DateTime.Now;
                }
                catch (Exception ex)
                {
                    if (!silent)
                        await DisplayAlert($"Не удалось обновить {entity.Name}", ex.Message, "ОК");
                }
            }
            _settings.LastForegroundSync = DateTime.Now;
            await SaveEntitiesAsync();
            await SaveSettingsAsync();
            await ReloadEventsAsync();
        }
        finally
        {
            _busy = false;
            if (_refreshButton != null) _refreshButton.IsEnabled = true;
            UpdateHeaderStatus();
            if (_tab == "schedule") await RefreshViewAsync();
        }
    }

    private async Task ReloadEventsAsync()
    {
        _events = await _store.LoadSelectedEventsAsync(_entities);
        foreach (var ev in _events)
        {
            if (string.IsNullOrEmpty(ev.EntityKey))
            {
                var source = _entities.FirstOrDefault(e => e.IsSelected);
                if (source != null) ev.EntityKey = source.StorageKey;
            }
        }
        _preferences = await _store.LoadPreferencesAsync();
    }

    private async Task SaveEntitiesAsync() => await _store.SaveEntitiesAsync(_entities);
    private async Task SaveSettingsAsync() => await _store.SaveSettingsAsync(_settings);

    private async Task ExportCalendarAsync()
    {
        var events = _events.Where(e => e.Start >= DateTime.Now.AddDays(-1)).OrderBy(e => e.Start).ToList();
        if (events.Count == 0)
        {
            await DisplayAlert("Нет событий", "Сначала загрузите расписание.", "ОК");
            return;
        }
        var sb = new StringBuilder();
        sb.AppendLine("BEGIN:VCALENDAR");
        sb.AppendLine("VERSION:2.0");
        sb.AppendLine("PRODID:-//NureTimetable//Android 16//RU");
        foreach (var ev in events)
        {
            sb.AppendLine("BEGIN:VEVENT");
            sb.AppendLine("UID:" + Guid.NewGuid().ToString("N") + "@nuretimetable");
            sb.AppendLine("DTSTAMP:" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'"));
            sb.AppendLine("DTSTART:" + ev.Start.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'"));
            sb.AppendLine("DTEND:" + ev.End.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'"));
            sb.AppendLine("SUMMARY:" + EscapeIcs(ev.Lesson + " " + ev.Type));
            sb.AppendLine("LOCATION:" + EscapeIcs(ev.Room));
            string description = string.Join(", ", ev.Teachers);
            if (description.Length > 0) sb.AppendLine("DESCRIPTION:" + EscapeIcs(description));
            sb.AppendLine("END:VEVENT");
        }
        sb.AppendLine("END:VCALENDAR");
        string path = Path.Combine(FileSystem.CacheDirectory, "nure-timetable.ics");
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
        await Share.Default.RequestAsync(new ShareFileRequest("Расписание ХНУРЭ", new ShareFile(path)));
    }

    private static string EscapeIcs(string value) => (value ?? "")
        .Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,")
        .Replace("\r\n", "\\n").Replace("\n", "\\n");

    private View EmptyCard(string title, string subtitle)
    {
        var stack = new VerticalStackLayout { Spacing = 5, Padding = 16 };
        stack.Add(new Label { Text = title, FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = _ink });
        stack.Add(new Label { Text = subtitle, FontSize = 13, TextColor = _muted });
        return new Border
        {
            Padding = 4,
            Stroke = Color.FromArgb("#E1E7EF"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            BackgroundColor = _surface,
            Content = stack
        };
    }

    private Button MakeButton(string text, Color background, Color foreground, Func<Task> action, double height)
    {
        var button = new Button
        {
            Text = text,
            BackgroundColor = background,
            TextColor = foreground,
            CornerRadius = 10,
            HeightRequest = height,
            Padding = new Thickness(12, 4),
            FontSize = 14,
            HorizontalOptions = LayoutOptions.Fill
        };
        button.Clicked += async (_, _) =>
        {
            if (!button.IsEnabled) return;
            button.IsEnabled = false;
            try { await action(); }
            catch (Exception ex) { await DisplayAlert("Ошибка", ex.Message, "ОК"); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private Button MakeButton(string text, Color background, Color foreground, Action action, double height)
    {
        return MakeButton(text, background, foreground, () => { action(); return Task.CompletedTask; }, height);
    }
}

public sealed class EntitySearchPage : ContentPage
{
    private readonly CistService _service;
    private readonly VerticalStackLayout _results = new() { Spacing = 6 };
    private EntityKind _kind = EntityKind.Group;
    private readonly TaskCompletionSource<TimetableEntity?> _selection = new();
    private bool _loading;

    public Task<TimetableEntity?> SelectionTask => _selection.Task;

    public EntitySearchPage(CistService service)
    {
        _service = service;
        Title = "Добавить расписание";
        BackgroundColor = Color.FromArgb("#F4F6FA");
        var root = new VerticalStackLayout { Padding = 14, Spacing = 10 };
        root.Add(new Label { Text = "Выбери тип расписания", FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#172033") });
        var picker = new Picker { Title = "Тип", ItemsSource = new List<string> { "Группы", "Преподаватели", "Аудитории" }, SelectedIndex = 0 };
        picker.SelectedIndexChanged += async (_, _) =>
        {
            _kind = picker.SelectedIndex switch { 1 => EntityKind.Teacher, 2 => EntityKind.Room, _ => EntityKind.Group };
            await SearchAsync("");
        };
        root.Add(picker);
        var search = new SearchBar { Placeholder = "Начни вводить название или ID", BackgroundColor = Colors.White };
        search.TextChanged += async (_, e) => await SearchAsync(e.NewTextValue ?? "");
        root.Add(search);
        root.Add(new Label { Text = "Результаты поиска CIST", FontSize = 13, TextColor = Color.FromArgb("#64748B") });
        root.Add(_results);
        root.Add(new Button
        {
            Text = "Добавить вручную по ID",
            BackgroundColor = Color.FromArgb("#E9EEF6"),
            TextColor = Color.FromArgb("#2457A6"),
            CornerRadius = 10,
            HeightRequest = 44
        });
        var manual = (Button)root[^1];
        manual.Clicked += async (_, _) => await AddManualAsync();
        var cancel = new Button { Text = "Отмена", BackgroundColor = Color.FromArgb("#FEE2E2"), TextColor = Color.FromArgb("#B91C1C"), CornerRadius = 10, HeightRequest = 42 };
        cancel.Clicked += async (_, _) => await CancelAsync();
        root.Add(cancel);
        Content = new ScrollView { Content = root };
        _ = SearchAsync("");
    }

    private async Task SearchAsync(string query)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            _results.Clear();
            _results.Add(new ActivityIndicator { IsRunning = true, Color = Color.FromArgb("#2457A6") });
            var entities = await _service.SearchEntitiesAsync(_kind, query);
            _results.Clear();
            if (entities.Count == 0)
            {
                _results.Add(new Label
                {
                    Text = "Список CIST не загрузился или ничего не найдено. Можно добавить по ID вручную.",
                    TextColor = Color.FromArgb("#64748B"),
                    FontSize = 13
                });
            }
            foreach (var entity in entities.Take(100))
            {
                var b = new Button
                {
                    Text = entity.Name + "   ·   ID " + entity.Id,
                    BackgroundColor = Colors.White,
                    TextColor = Color.FromArgb("#172033"),
                    CornerRadius = 10,
                    HorizontalOptions = LayoutOptions.Fill,
                    HeightRequest = 42,
                    FontSize = 13
                };
                b.Clicked += async (_, _) => await SelectAsync(entity);
                _results.Add(b);
            }
        }
        catch (Exception ex)
        {
            _results.Clear();
            _results.Add(new Label { Text = ex.Message, TextColor = Colors.Red });
        }
        finally { _loading = false; }
    }

    private async Task AddManualAsync()
    {
        string? idText = await DisplayPromptAsync("ID объекта CIST", "Введите числовой ID группы, преподавателя или аудитории:", "Далее", "Отмена", keyboard: Keyboard.Numeric);
        if (!long.TryParse(idText, out long id) || id <= 0) return;
        string? name = await DisplayPromptAsync("Название", "Как подписать расписание?", "Добавить", "Отмена");
        if (string.IsNullOrWhiteSpace(name)) return;
        await SelectAsync(new TimetableEntity { Id = id, Name = name.Trim(), Type = _kind });
    }

    private async Task SelectAsync(TimetableEntity entity)
    {
        if (_selection.TrySetResult(entity))
            await Navigation.PopModalAsync();
    }

    private async Task CancelAsync()
    {
        _selection.TrySetResult(null);
        await Navigation.PopModalAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        _selection.TrySetResult(null);
        return base.OnBackButtonPressed();
    }
}
