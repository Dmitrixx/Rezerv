using System.Net;
using System.Text.Json;
using Microsoft.Maui.Storage;

namespace NureTimetable.Maui;

public sealed class MoodleService
{
    private const string BaseUrl = "https://dl.nure.ua";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(35) };
    private const string TokenKey = "nure_moodle_token";
    private const string LoginKey = "nure_moodle_login";
    private const string PasswordKey = "nure_moodle_password";

    public async Task<MoodleAccount> AuthenticateAsync(string login, string password, CancellationToken ct = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = login,
            ["password"] = password,
            ["service"] = "moodle_mobile_app"
        });
        using var response = await Http.PostAsync(BaseUrl + "/login/token.php", content, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        using var tokenDoc = JsonDocument.Parse(json);
        var root = tokenDoc.RootElement;
        if (!root.TryGetProperty("token", out var tokenValue))
        {
            string message = root.TryGetProperty("error", out var error) ? error.GetString() ?? "Не удалось войти" :
                root.TryGetProperty("errorcode", out var code) ? code.GetString() ?? "Не удалось войти" : "Сервер не вернул токен";
            throw new InvalidOperationException(WebUtility.HtmlDecode(message));
        }
        string token = tokenValue.GetString() ?? throw new InvalidOperationException("Пустой токен Moodle");
        using var siteDoc = await GetSiteInfoAsync(token, ct);
        var site = siteDoc.RootElement;
        int resolvedUserId = 0;
        if (site.TryGetProperty("userid", out var idValue))
            idValue.TryGetInt32(out resolvedUserId);
        var account = new MoodleAccount
        {
            Login = login,
            FullName = site.TryGetProperty("fullname", out var full) ? full.GetString() ?? login : login,
            UserId = resolvedUserId
        };

        await SecureStorage.SetAsync(TokenKey, token);
        await SecureStorage.SetAsync(LoginKey, login);
        await SecureStorage.SetAsync(PasswordKey, password);
        return account;
    }

    public async Task<bool> HasSessionAsync()
    {
        return !string.IsNullOrWhiteSpace(await SecureStorage.GetAsync(TokenKey));
    }

    public async Task<string?> GetLoginAsync() => await SecureStorage.GetAsync(LoginKey);

    public async Task LogoutAsync()
    {
        SecureStorage.Remove(TokenKey);
        SecureStorage.Remove(LoginKey);
        SecureStorage.Remove(PasswordKey);
    }

    public async Task<List<MoodleCourse>> GetCoursesAsync(int userId, CancellationToken ct = default)
    {
        using var doc = await CallAsync("core_enrol_get_users_courses",
            new Dictionary<string, string> { ["userid"] = userId.ToString(), ["returnusercount"] = "0" }, ct);
        var courses = new List<MoodleCourse>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return courses;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            courses.Add(new MoodleCourse
            {
                Id = Int(item, "id"),
                ShortName = Str(item, "shortname"),
                FullName = Str(item, "fullname"),
                Summary = Str(item, "summary"),
                StartDateUtc = UtcDate(item, "startdate"),
                EndDateUtc = UtcDate(item, "enddate")
            });
        }
        return courses.Where(c => c.Id > 0).ToList();
    }

    public async Task<MoodleCourse?> FindCourseForLessonAsync(
        int userId, string lessonShortName, string lessonFullName, CancellationToken ct = default)
    {
        var courses = await GetCoursesAsync(userId, ct);
        string fullName = Simplify(lessonFullName);
        var matches = courses.Where(course =>
            (!string.IsNullOrWhiteSpace(lessonShortName) &&
                (course.ShortName.Contains($":{lessonShortName}:", StringComparison.OrdinalIgnoreCase) ||
                 course.ShortName.Equals(lessonShortName, StringComparison.OrdinalIgnoreCase))) ||
            (fullName.Length > 0 && fullName.StartsWith(Simplify(course.FullName), StringComparison.Ordinal)))
            .ToList();

        if (matches.Count == 0 && !string.IsNullOrWhiteSpace(lessonShortName))
        {
            string shortName = Simplify(lessonShortName);
            matches = courses.Where(course =>
                Simplify(course.FullName).Contains(shortName, StringComparison.Ordinal))
                .ToList();
        }

        if (matches.Count > 1)
        {
            var now = DateTime.UtcNow;
            var ongoing = matches.Where(course =>
                (course.StartDateUtc == null || course.StartDateUtc <= now) &&
                (course.EndDateUtc == null || course.EndDateUtc > now)).ToList();
            if (ongoing.Count > 0) matches = ongoing;
        }

        return matches.FirstOrDefault();
    }

    public async Task<string?> GetAttendanceUrlAsync(int courseId, CancellationToken ct = default)
    {
        var modules = await GetCourseContentsAsync(courseId, ct: ct);
        return modules.FirstOrDefault(module =>
            module.ModName.Equals("attendance", StringComparison.OrdinalIgnoreCase))?.Url;
    }

    private static string Simplify(string value) =>
        new((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static DateTime? UtcDate(JsonElement item, string propertyName)
    {
        if (item.TryGetProperty(propertyName, out var value) &&
            value.TryGetInt64(out long seconds) && seconds > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        return null;
    }

    public async Task<List<MoodleModule>> GetCourseContentsAsync(int courseId, CancellationToken ct = default)
    {
        using var doc = await CallAsync("core_course_get_contents",
            new Dictionary<string, string> { ["courseid"] = courseId.ToString() }, ct);
        var modules = new List<MoodleModule>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return modules;
        foreach (var section in doc.RootElement.EnumerateArray())
        {
            if (!section.TryGetProperty("modules", out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var module in list.EnumerateArray())
            {
                var model = new MoodleModule
                {
                    Name = Str(module, "name"),
                    ModName = Str(module, "modname"),
                    Url = Str(module, "url"),
                    Description = Str(module, "description")
                };
                if (module.TryGetProperty("contents", out var contents) && contents.ValueKind == JsonValueKind.Array)
                {
                    foreach (var file in contents.EnumerateArray())
                    {
                        string url = Str(file, "fileurl");
                        if (!string.IsNullOrWhiteSpace(url)) model.Urls.Add(url);
                    }
                }
                modules.Add(model);
            }
        }
        return modules;
    }

    public async Task<List<MoodleCalendarEvent>> GetUpcomingEventsAsync(CancellationToken ct = default)
    {
        using var doc = await CallAsync("core_calendar_get_calendar_upcoming_view", new(), ct);
        var result = new List<MoodleCalendarEvent>();
        if (!doc.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in events.EnumerateArray())
        {
            result.Add(new MoodleCalendarEvent
            {
                Name = Str(item, "name"),
                Description = Str(item, "description"),
                Url = Str(item, "url"),
                StartTime = item.TryGetProperty("timestart", out var stamp) && stamp.TryGetInt64(out long value) ? value : 0
            });
        }
        return result;
    }

    private async Task<JsonDocument> GetSiteInfoAsync(string token, CancellationToken ct)
    {
        string url = BuildUrl(token, "core_webservice_get_site_info", new());
        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<JsonDocument> CallAsync(string function, Dictionary<string, string> args, CancellationToken ct)
    {
        string token = await SecureStorage.GetAsync(TokenKey) ?? throw new InvalidOperationException("Сначала войдите в DL NURE.");
        string url = BuildUrl(token, function, args);
        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("exception", out var ex))
        {
            string message = doc.RootElement.TryGetProperty("message", out var msg) ? msg.GetString() ?? ex.ToString() : ex.ToString();
            doc.Dispose();
            throw new InvalidOperationException("Ошибка DL NURE: " + WebUtility.HtmlDecode(message));
        }
        return doc;
    }

    private static string BuildUrl(string token, string function, Dictionary<string, string> args)
    {
        var query = new List<string>
        {
            "moodlewsrestformat=json",
            "wstoken=" + Uri.EscapeDataString(token),
            "wsfunction=" + Uri.EscapeDataString(function)
        };
        query.AddRange(args.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        return BaseUrl + "/webservice/rest/server.php?" + string.Join("&", query);
    }

    private static int Int(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.TryGetInt32(out int result) ? result : 0;

    private static string Str(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString() : "";
}

public sealed class MoodleAccount
{
    public string Login { get; set; } = "";
    public string FullName { get; set; } = "";
    public int UserId { get; set; }
}

public sealed class MoodleCourse
{
    public int Id { get; set; }
    public string ShortName { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Summary { get; set; } = "";
    public DateTime? StartDateUtc { get; set; }
    public DateTime? EndDateUtc { get; set; }
}

public sealed class MoodleModule
{
    public string Name { get; set; } = "";
    public string ModName { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Urls { get; set; } = new();
}

public sealed class MoodleCalendarEvent
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Url { get; set; } = "";
    public long StartTime { get; set; }
}
