using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NureTimetable.Maui;

public sealed class CistService
{
    private const string BaseUrl = "https://cist.nure.ua/ias/app/tt/";
    private static readonly HttpClient Http = CreateClient();
    private readonly LocalStore _store;

    public CistService(LocalStore store) => _store = store;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NureTimetable-Android16/1.1 (MAUI)");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/csv, text/html, application/json, */*");
        client.DefaultRequestHeaders.Referrer = new Uri("https://cist.nure.ua/ias/app/tt/");
        return client;
    }

    public async Task<List<TimetableEntity>> SearchEntitiesAsync(EntityKind type, string query, CancellationToken cancellationToken = default)
    {
        var catalog = await GetCatalogPartAsync(type, cancellationToken);
        query = (query ?? "").Trim();
        IEnumerable<TimetableEntity> result = catalog;
        if (query.Length > 0)
        {
            result = result.Where(e =>
                e.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                e.Id.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        return result.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).Take(500).ToList();
    }

    private async Task<List<TimetableEntity>> GetCatalogPartAsync(EntityKind type, CancellationToken ct)
    {
        var cached = await _store.LoadCatalogAsync();
        List<TimetableEntity>? current = type switch
        {
            EntityKind.Group => cached?.Groups,
            EntityKind.Teacher => cached?.Teachers,
            EntityKind.Room => cached?.Rooms,
            _ => null
        };

        if (current is { Count: > 0 } && cached!.SavedAtUtc > DateTime.UtcNow.AddHours(-24))
            return current;

        List<TimetableEntity> loaded = type switch
        {
            EntityKind.Group => await LoadGroupsAsync(ct),
            EntityKind.Teacher => await LoadTeachersAsync(ct),
            EntityKind.Room => await LoadRoomsAsync(ct),
            _ => new()
        };

        cached ??= new CachedCatalog();
        switch (type)
        {
            case EntityKind.Group: cached.Groups = loaded; break;
            case EntityKind.Teacher: cached.Teachers = loaded; break;
            case EntityKind.Room: cached.Rooms = loaded; break;
        }
        cached.SavedAtUtc = DateTime.UtcNow;
        await _store.SaveCatalogAsync(cached);
        return loaded;
    }

    private async Task<List<TimetableEntity>> LoadGroupsAsync(CancellationToken ct)
    {
        var api = await TryLoadJsonCatalogAsync("P_API_GROUP_JSON", EntityKind.Group, ct);
        if (api.Count > 0) return api;

        var facultiesHtml = await GetTextAsync("WEB_IAS_TT_AJX_GROUPS?p_id_fac=null", ct);
        var faculties = new List<(long Id, string Name)>();
        foreach (string part in facultiesHtml.Split("IAS_Change_Groups(", StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            int close = part.IndexOf(')');
            if (close < 0 || !long.TryParse(part[..close], out long facultyId)) continue;
            int gt = part.IndexOf('>');
            int lt = gt >= 0 ? part.IndexOf('<', gt + 1) : -1;
            if (gt < 0 || lt <= gt) continue;
            string name = CleanHtml(part[(gt + 1)..lt]);
            if (name.Length > 0) faculties.Add((facultyId, name));
        }

        if (faculties.Count == 0)
        {
            foreach (long fallback in new long[] { -1, 0 })
            {
                try
                {
                    var html = await GetTextAsync($"WEB_IAS_TT_AJX_GROUPS?p_id_fac={fallback}", ct);
                    faculties.AddRange(ParseFaculties(html, "IAS_Change_Groups("));
                    if (faculties.Count > 0) break;
                }
                catch { }
            }
        }

        var groups = new Dictionary<long, TimetableEntity>();
        foreach (var faculty in faculties.DistinctBy(f => f.Id))
        {
            ct.ThrowIfCancellationRequested();
            string html;
            try { html = await GetTextAsync($"WEB_IAS_TT_AJX_GROUPS?p_id_fac={faculty.Id}", ct); }
            catch { continue; }

            foreach (string part in html.Split("IAS_ADD_Group_in_List(", StringSplitOptions.RemoveEmptyEntries).Skip(0))
            {
                int cut = part.IndexOf(")\">", StringComparison.Ordinal);
                if (cut < 0) cut = part.IndexOf(')');
                if (cut < 0) continue;
                string[] info = part[..cut].Split(new[] { ',', '\'' }, StringSplitOptions.RemoveEmptyEntries);
                if (info.Length < 2 || !long.TryParse(info[1].Trim(), out long id)) continue;
                string name = CleanHtml(info[0].Trim());
                if (name.Length > 0 && !groups.ContainsKey(id))
                    groups[id] = new TimetableEntity { Id = id, Name = name, Type = EntityKind.Group };
            }
            await Task.Delay(80, ct);
        }

        return groups.Values.OrderBy(g => g.Name).ToList();
    }

    private async Task<List<TimetableEntity>> LoadTeachersAsync(CancellationToken ct)
    {
        var api = await TryLoadJsonCatalogAsync("P_API_PODR_JSON", EntityKind.Teacher, ct);
        if (api.Count > 0) return api;

        string facultyHtml = await GetTextAsync("WEB_IAS_TT_AJX_TEACHS?p_id_fac=-1&p_id_kaf=-1", ct);
        var faculties = new List<(long Id, string Name)>();
        foreach (string part in facultyHtml.Split("IAS_Change_Kaf(", StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            int comma = part.IndexOf(',');
            int gt = part.IndexOf('>');
            int lt = gt >= 0 ? part.IndexOf('<', gt + 1) : -1;
            if (comma <= 0 || !long.TryParse(part[..comma], out long id) || gt < 0 || lt <= gt) continue;
            string name = CleanHtml(part[(gt + 1)..lt]);
            if (name.Length > 0) faculties.Add((id, name));
        }

        var teachers = new Dictionary<long, TimetableEntity>();
        foreach (var faculty in faculties.DistinctBy(f => f.Id))
        {
            ct.ThrowIfCancellationRequested();
            string facultyPage;
            try { facultyPage = await GetTextAsync($"WEB_IAS_TT_AJX_TEACHS?p_id_fac={faculty.Id}&p_id_kaf=-1", ct); }
            catch { continue; }

            var departments = new List<long>();
            foreach (string part in facultyPage.Split($"IAS_Change_Kaf({faculty.Id},", StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                int close = part.IndexOf(')');
                if (close > 0 && long.TryParse(part[..close], out long depId) && depId != -1)
                    departments.Add(depId);
            }

            foreach (long department in departments.Distinct())
            {
                string departmentPage;
                try { departmentPage = await GetTextAsync($"WEB_IAS_TT_AJX_TEACHS?p_id_fac={faculty.Id}&p_id_kaf={department}", ct); }
                catch { continue; }

                foreach (string part in departmentPage.Split("IAS_ADD_Teach_in_List(", StringSplitOptions.RemoveEmptyEntries))
                {
                    int cut = part.IndexOf(")\">", StringComparison.Ordinal);
                    if (cut < 0) cut = part.IndexOf(')');
                    if (cut < 0) continue;
                    string[] info = part[..cut].Split(new[] { ',', '\'' }, StringSplitOptions.RemoveEmptyEntries);
                    if (info.Length < 2 || !long.TryParse(info[1].Trim(), out long id)) continue;
                    string name = CleanHtml(info[0].Trim());
                    if (name.Length > 0 && !teachers.ContainsKey(id))
                        teachers[id] = new TimetableEntity { Id = id, Name = name, Type = EntityKind.Teacher };
                }
                await Task.Delay(60, ct);
            }
            await Task.Delay(80, ct);
        }
        return teachers.Values.OrderBy(t => t.Name).ToList();
    }

    private async Task<List<TimetableEntity>> LoadRoomsAsync(CancellationToken ct)
    {
        var rooms = await TryLoadJsonCatalogAsync("P_API_AUDITORIES_JSON", EntityKind.Room, ct);
        if (rooms.Count > 0) return rooms;

        // Some CIST installations expose rooms only through the HTML audit catalog.
        foreach (string endpoint in new[]
        {
            "WEB_IAS_TT_AJX_AUDS?p_id_build=-1",
            "WEB_IAS_TT_AJX_AUDITORIES?p_id_build=-1",
            "WEB_IAS_TT_AJX_AUDITORIES"
        })
        {
            try
            {
                string html = await GetTextAsync(endpoint, ct);
                var parsed = ParseGenericHtmlEntities(html, EntityKind.Room);
                if (parsed.Count > 0) return parsed;
            }
            catch { }
        }
        return new();
    }

    private async Task<List<TimetableEntity>> TryLoadJsonCatalogAsync(string endpoint, EntityKind kind, CancellationToken ct)
    {
        try
        {
            string json = await GetTextAsync(endpoint, ct);
            using var doc = JsonDocument.Parse(json);
            var result = new Dictionary<long, TimetableEntity>();
            WalkJson(doc.RootElement, kind, result);
            return result.Values.OrderBy(e => e.Name).ToList();
        }
        catch { return new(); }
    }

    private static void WalkJson(JsonElement element, EntityKind kind, Dictionary<long, TimetableEntity> found)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) WalkJson(child, kind, found);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        long? id = GetLong(element, "id", "ID", "id_group", "id_sotr", "id_aud");
        string? name = GetString(element, "short_name", "shortname", "name", "Name", "auditory", "title");
        if (id is > 0 && !string.IsNullOrWhiteSpace(name))
        {
            found[id.Value] = new TimetableEntity { Id = id.Value, Name = CleanHtml(name), Type = kind };
        }
        foreach (var property in element.EnumerateObject()) WalkJson(property.Value, kind, found);
    }

    private static long? GetLong(JsonElement obj, params string[] names)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (!names.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out long n)) return n;
            if (p.Value.ValueKind == JsonValueKind.String && long.TryParse(p.Value.GetString(), out n)) return n;
        }
        return null;
    }

    private static string? GetString(JsonElement obj, params string[] names)
    {
        foreach (var p in obj.EnumerateObject())
            if (names.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                return p.Value.GetString();
        return null;
    }

    private static List<(long Id, string Name)> ParseFaculties(string html, string marker)
    {
        var result = new List<(long, string)>();
        foreach (string part in html.Split(marker, StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            int close = part.IndexOf(')');
            int gt = part.IndexOf('>');
            int lt = gt >= 0 ? part.IndexOf('<', gt + 1) : -1;
            if (close <= 0 || gt < 0 || lt <= gt || !long.TryParse(part[..close], out long id)) continue;
            string name = CleanHtml(part[(gt + 1)..lt]);
            if (name.Length > 0) result.Add((id, name));
        }
        return result;
    }

    private static List<TimetableEntity> ParseGenericHtmlEntities(string html, EntityKind kind)
    {
        var result = new Dictionary<long, TimetableEntity>();
        foreach (Match match in Regex.Matches(html,
            @"(?:IAS_ADD_[A-Za-z_]+\(|IAS_SELECT_[A-Za-z_]+\()\s*'(?<name>[^']+)'\s*,\s*(?<id>\d+)",
            RegexOptions.IgnoreCase))
        {
            if (!long.TryParse(match.Groups["id"].Value, out long id)) continue;
            string name = CleanHtml(match.Groups["name"].Value);
            if (name.Length > 0) result[id] = new TimetableEntity { Id = id, Name = name, Type = kind };
        }
        return result.Values.OrderBy(e => e.Name).ToList();
    }

    public async Task<List<TimetableEvent>> GetScheduleAsync(TimetableEntity entity, DateTime from, DateTime to, CancellationToken ct = default)
    {
        if (to.Date < from.Date) throw new ArgumentException("Дата окончания меньше даты начала.");
        if ((to.Date - from.Date).TotalDays > 90) throw new ArgumentException("CIST позволяет выгружать расписание не более чем за 90 дней.");

        string query = entity.Type switch
        {
            EntityKind.Group => $"Aid_group={entity.Id}&Aid_potok=0",
            EntityKind.Teacher => $"Aid_sotr={entity.Id}&Aid_kaf=0",
            EntityKind.Room => $"Aid_aud={entity.Id}",
            _ => throw new ArgumentOutOfRangeException(nameof(entity))
        };
        string url = BaseUrl + "WEB_IAS_TT_GNR_RASP.GEN_GROUP_POTOK_RASP" +
            $"?ATypeDoc=3&{query}&ADateStart={from:dd.MM.yyyy}&ADateEnd={to:dd.MM.yyyy}&AMultiWorkSheet=0";

        string csv = await GetTextAbsoluteAsync(url, ct);
        var events = ParseCsvTimetable(csv, entity);
        if (entity.Type == EntityKind.Group)
        {
            try
            {
                var teacherMap = await LoadGroupTeacherMetadataAsync(entity, from, to, ct);
                foreach (var ev in events)
                {
                    string key = ev.Lesson.Trim().ToUpperInvariant() + "|" + ev.Type.Trim().ToUpperInvariant();
                    if (teacherMap.TryGetValue(key, out var lessonMetadata))
                    {
                        ev.Teachers = lessonMetadata.Teachers.Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
                        ev.LessonLongName = lessonMetadata.LongName;
                    }
                    ev.Groups = new() { entity.Name };
                }
            }
            catch { }
        }
        else if (entity.Type == EntityKind.Teacher)
        {
            foreach (var ev in events) ev.Teachers = new() { entity.Name };
        }
        return events.OrderBy(e => e.Start).ToList();
    }

    private sealed class LessonMetadata
    {
        public string LongName { get; set; } = "";
        public List<string> Teachers { get; set; } = new();
    }

    private async Task<Dictionary<string, LessonMetadata>> LoadGroupTeacherMetadataAsync(TimetableEntity group, DateTime from, DateTime to, CancellationToken ct)
    {
        string url = BaseUrl + "WEB_IAS_TT_GNR_RASP.GEN_GROUP_POTOK_RASP" +
            $"?ATypeDoc=2&Aid_group={group.Id}&Aid_potok=0&ADateStart={DateTime.Today:dd.MM.yyyy}&ADateEnd={to:dd.MM.yyyy}&AMultiWorkSheet=0";
        string xml = await GetTextAbsoluteAsync(url, ct);
        var cells = Regex.Matches(xml, @"ss:Type=""String"">\s*(.*?)</Data>", RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Select(m => CleanHtml(m.Groups[1].Value))
            .ToList();
        var map = new Dictionary<string, LessonMetadata>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < cells.Count; i += 2)
        {
            string shortName = cells[i].Trim();
            string[] parts = cells[i + 1].Split(new[] { ": ", "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            string longName = parts[0].Trim();
            foreach (string line in parts.Skip(1))
            {
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (tokens.Count == 0) continue;
                string eventType = tokens[0];
                string key = shortName.ToUpperInvariant() + "|" + eventType.ToUpperInvariant();
                if (!map.TryGetValue(key, out var metadata))
                    map[key] = metadata = new LessonMetadata { LongName = longName };
                while (tokens.Count > 4 && !tokens[3].EndsWith(",", StringComparison.Ordinal))
                {
                    tokens[3] += " " + tokens[4];
                    tokens.RemoveAt(4);
                }
                if (tokens.Count < 6 || !GroupListContains(tokens[3], group.Name)) continue;
                string teacher = string.Join(" ", tokens.Skip(4).Take(3)).Trim().TrimEnd(',');
                if (teacher.Length > 0) metadata.Teachers.Add(teacher);
            }
        }
        return map;
    }

    private static bool GroupListContains(string groupsText, string searchGroup)
    {
        if (groupsText.Contains('(')) return true;
        foreach (string section in groupsText.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var groupList = section.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (groupList.Count == 0) continue;
            if (groupList[0].Trim() == searchGroup) return true;
            if (groupList.Count == 1) continue;
            int dash = groupList[0].LastIndexOf('-');
            if (dash < 0) continue;
            string prefix = groupList[0][..(dash + 1)];
            foreach (string suffix in groupList.Skip(1))
                if (prefix + suffix.Trim().TrimEnd(',') == searchGroup) return true;
        }
        return false;
    }

    private static List<TimetableEvent> ParseCsvTimetable(string csv, TimetableEntity entity)
    {
        var rows = ParseCsv(csv);
        if (rows.Count <= 1) throw new InvalidDataException("CIST вернул пустой или неизвестный CSV-файл.");
        var result = new List<TimetableEvent>();
        foreach (var row in rows.Skip(1))
        {
            if (row.Count < 5) continue;
            if (!TryDateTime(row[1], row[2], out DateTime start) ||
                !TryDateTime(row[3], row[4], out DateTime end)) continue;
            if (end < start) continue;

            string topic = row[0];
            string description = row.Count > 11 ? row[11] : "";
            foreach (string entry in topic.Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries))
            {
                string clean = entry.Trim();
                string groupPrefix = entity.Name + " - ";
                if (clean.StartsWith(groupPrefix, StringComparison.CurrentCultureIgnoreCase))
                    clean = clean[groupPrefix.Length..];

                var tokens = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (tokens.Count == 0) continue;
                int typeIndex = FindEventTypeIndex(tokens);
                string lesson;
                string type;
                string room;
                if (typeIndex > 0)
                {
                    lesson = string.Join(" ", tokens.Take(typeIndex));
                    type = tokens[typeIndex];
                    room = string.Join(" ", tokens.Skip(typeIndex + 1));
                }
                else if (tokens.Count >= 2)
                {
                    lesson = tokens[0];
                    type = tokens[1];
                    room = string.Join(" ", tokens.Skip(2));
                }
                else
                {
                    lesson = clean;
                    type = "";
                    room = "";
                }

                if (string.IsNullOrWhiteSpace(lesson)) lesson = clean;
                result.Add(new TimetableEvent
                {
                    Start = start,
                    End = end,
                    Lesson = lesson.Trim(),
                    Type = type.Trim(),
                    Room = room.Trim().TrimEnd(';', ','),
                    Description = description.Trim(),
                    Groups = entity.Type == EntityKind.Group ? new() { entity.Name } : new()
                });
            }
        }

        return result
            .GroupBy(e => e.EventKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(e => e.Start)
            .ToList();
    }

    private static int FindEventTypeIndex(List<string> tokens)
    {
        string[] known =
        {
            "лк", "пз", "лб", "конс", "зал", "іспкомб",
            "лек", "лекц", "лаб", "практ", "сем", "екз", "залік",
            "lecture", "practice", "lab"
        };
        for (int i = tokens.Count - 1; i >= 1; i--)
            if (known.Contains(tokens[i].Trim('.', ':').ToLowerInvariant())) return i;
        return -1;
    }

    private static bool TryDateTime(string date, string time, out DateTime value)
    {
        string text = date.Trim() + " " + time.Trim();
        string[] formats =
        {
            "dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy H:mm:ss",
            "dd.MM.yyyy HH:mm", "dd.MM.yyyy H:mm",
            "d.M.yyyy HH:mm:ss", "d.M.yyyy H:mm:ss",
            "d.M.yyyy HH:mm", "d.M.yyyy H:mm"
        };
        return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out value) ||
            DateTime.TryParse(text, CultureInfo.GetCultureInfo("uk-UA"), DateTimeStyles.AssumeLocal, out value);
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"'); i++;
                }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                row.Add(field.ToString()); field.Clear();
            }
            else if ((c == '\n' || c == '\r') && !quoted)
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Any(v => !string.IsNullOrWhiteSpace(v))) rows.Add(row);
                row = new List<string>();
            }
            else field.Append(c);
        }
        row.Add(field.ToString());
        if (row.Any(v => !string.IsNullOrWhiteSpace(v))) rows.Add(row);
        return rows;
    }

    private static string CleanHtml(string value)
    {
        value = Regex.Replace(value, "<[^>]+>", " ");
        value = WebUtility.HtmlDecode(value);
        return Regex.Replace(value, @"\s+", " ").Trim();
    }

    private async Task<string> GetTextAsync(string relative, CancellationToken ct) =>
        await GetTextAbsoluteAsync(BaseUrl + relative, ct);

    private static async Task<string> GetTextAbsoluteAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
        string text = Encoding.UTF8.GetString(bytes);
        if (text.Contains('�'))
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                text = Encoding.GetEncoding(1251).GetString(bytes);
            }
            catch { }
        }
        if (text.Contains("ORA-") || text.Contains("error", StringComparison.OrdinalIgnoreCase) && text.Length < 200)
            throw new InvalidDataException("CIST вернул сообщение об ошибке: " + text[..Math.Min(200, text.Length)]);
        return text;
    }
}
