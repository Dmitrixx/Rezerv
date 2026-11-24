using System.Text.Json;

namespace NureTimetable.Maui;

public sealed class LocalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _root = FileSystem.AppDataDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string EntitiesPath => Path.Combine(_root, "saved_entities.json");
    private string SettingsPath => Path.Combine(_root, "user_settings.json");
    private string CatalogPath => Path.Combine(_root, "catalog_cache.json");
    private string PreferencesPath => Path.Combine(_root, "lesson_preferences.json");

    private string SchedulePath(TimetableEntity entity) =>
        Path.Combine(_root, $"schedule_{entity.StorageKey}.json");

    public async Task<List<TimetableEntity>> LoadEntitiesAsync()
    {
        var items = await ReadAsync<List<TimetableEntity>>(EntitiesPath) ?? new();
        return items.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task SaveEntitiesAsync(List<TimetableEntity> items) => WriteAsync(EntitiesPath, items);

    public async Task<UserSettings> LoadSettingsAsync() =>
        await ReadAsync<UserSettings>(SettingsPath) ?? new UserSettings();

    public Task SaveSettingsAsync(UserSettings settings) => WriteAsync(SettingsPath, settings);

    public async Task<List<TimetableEvent>> LoadScheduleAsync(TimetableEntity entity) =>
        await ReadAsync<List<TimetableEvent>>(SchedulePath(entity)) ?? new();

    public Task SaveScheduleAsync(TimetableEntity entity, List<TimetableEvent> events) =>
        WriteAsync(SchedulePath(entity), events.OrderBy(e => e.Start).ToList());

    public async Task<CachedCatalog?> LoadCatalogAsync() =>
        await ReadAsync<CachedCatalog>(CatalogPath);

    public Task SaveCatalogAsync(CachedCatalog catalog) => WriteAsync(CatalogPath, catalog);

    public async Task<List<LessonPreference>> LoadPreferencesAsync() =>
        await ReadAsync<List<LessonPreference>>(PreferencesPath) ?? new();

    public Task SavePreferencesAsync(List<LessonPreference> preferences) =>
        WriteAsync(PreferencesPath, preferences);

    public async Task<LessonPreference> GetPreferenceAsync(string entityKey, string lessonKey)
    {
        var items = await LoadPreferencesAsync();
        return items.FirstOrDefault(p => p.EntityKey == entityKey &&
            p.LessonKey.Equals(lessonKey, StringComparison.OrdinalIgnoreCase))
            ?? new LessonPreference { EntityKey = entityKey, LessonKey = lessonKey };
    }

    public async Task SavePreferenceAsync(LessonPreference preference)
    {
        var items = await LoadPreferencesAsync();
        var index = items.FindIndex(p => p.EntityKey == preference.EntityKey &&
            p.LessonKey.Equals(preference.LessonKey, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) items[index] = preference;
        else items.Add(preference);
        await SavePreferencesAsync(items);
    }

    public async Task<List<TimetableEvent>> LoadSelectedEventsAsync(List<TimetableEntity> entities)
    {
        var selected = entities.Where(e => e.IsSelected).ToList();
        var result = new List<TimetableEvent>();
        foreach (var entity in selected)
        {
            var events = await LoadScheduleAsync(entity);
            foreach (var ev in events) ev.EntityKey = entity.StorageKey;
            result.AddRange(events);
        }
        return result.OrderBy(e => e.Start).ToList();
    }

    private async Task<T?> ReadAsync<T>(string path)
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(path)) return default;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
        finally { _gate.Release(); }
    }

    private async Task WriteAsync<T>(string path, T value)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_root);
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions);
            File.Move(temp, path, true);
        }
        finally { _gate.Release(); }
    }
}
