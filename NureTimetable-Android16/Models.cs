using System.Text.Json.Serialization;

namespace NureTimetable.Maui;

public enum EntityKind { Group = 1, Teacher = 2, Room = 3 }

public sealed class TimetableEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public EntityKind Type { get; set; }
    public bool IsSelected { get; set; } = true;
    public DateTime? LastUpdated { get; set; }

    [JsonIgnore]
    public string KindText => Type switch
    {
        EntityKind.Group => "Группа",
        EntityKind.Teacher => "Преподаватель",
        _ => "Аудитория"
    };

    [JsonIgnore]
    public string StorageKey => $"{(int)Type}_{Id}";

    public bool SameAs(TimetableEntity? other) =>
        other is not null && Id == other.Id && Type == other.Type;
}

public sealed class TimetableEvent
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Lesson { get; set; } = "";
    public string LessonLongName { get; set; } = "";
    public string Type { get; set; } = "";
    public string Room { get; set; } = "";
    public string Description { get; set; } = "";
    public string EntityKey { get; set; } = "";
    public List<string> Teachers { get; set; } = new();
    public List<string> Groups { get; set; } = new();

    [JsonIgnore]
    public string TimeText => $"{Start:HH:mm}–{End:HH:mm}";

    [JsonIgnore]
    public string LessonKey => (Lesson ?? "").Trim().ToUpperInvariant();

    [JsonIgnore]
    public string EventKey => $"{Start:O}|{LessonKey}|{Type}|{Room}";
}

public sealed class LessonPreference
{
    public string EntityKey { get; set; } = "";
    public string LessonKey { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool Hidden { get; set; }
    public HashSet<string> HiddenTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> HiddenTeachers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class UserSettings
{
    public string Language { get; set; } = "ru";
    public string Theme { get; set; } = "system";
    public string ViewMode { get; set; } = "week";
    public string TimeZoneId { get; set; } = "Europe/Kyiv";
    public bool AutoUpdate { get; set; } = true;
    public DateTime LastForegroundSync { get; set; } = DateTime.MinValue;
    public bool ShowPastLessons { get; set; } = true;
}

public sealed class CachedCatalog
{
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
    public List<TimetableEntity> Groups { get; set; } = new();
    public List<TimetableEntity> Teachers { get; set; } = new();
    public List<TimetableEntity> Rooms { get; set; } = new();
}

public sealed class LessonStatistic
{
    public string Lesson { get; set; } = "";
    public int Total { get; set; }
    public int Remaining { get; set; }
    public DateTime? NextEvent { get; set; }
    public List<string> Teachers { get; set; } = new();
    public int Lectures { get; set; }
    public int Practices { get; set; }
    public int Labs { get; set; }
}
