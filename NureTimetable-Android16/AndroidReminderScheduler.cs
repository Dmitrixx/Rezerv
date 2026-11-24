#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using System.Security.Cryptography;
using System.Text;

namespace NureTimetable.Maui;

public static class AndroidReminderScheduler
{
    public const string ChannelId = "nure_timetable_lessons";

    public static void Schedule(IEnumerable<TimetableEvent> events, int reminderMinutes, string? timeZoneId)
    {
        var context = Android.App.Application.Context;
        var alarm = context.GetSystemService(Context.AlarmService) as AlarmManager;
        if (alarm == null) return;

        EnsureNotificationChannel(context);
        var cistZone = GetCistTimeZone();

        foreach (var ev in events.Where(e => e.Start > DateTime.Now.AddDays(-1)).Take(500))
        {
            int id = StableId(ev.EntityKey + "|" + ev.EventKey);
            var intent = new Intent(context, typeof(LessonReminderReceiver));
            intent.PutExtra("notification_id", id);
            intent.PutExtra("title", ev.Lesson);
            intent.PutExtra("body", $"{ev.Type} · {ev.Room} · {ev.Start:dd.MM HH:mm}");

            var pending = PendingIntent.GetBroadcast(
                context, id, intent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            alarm.Cancel(pending);
            if (reminderMinutes < 0) continue;

            DateTime eventLocal = DateTime.SpecifyKind(ev.Start, DateTimeKind.Unspecified);
            DateTime fireLocal = eventLocal.AddMinutes(-reminderMinutes);
            DateTime fireUtc;
            try { fireUtc = TimeZoneInfo.ConvertTimeToUtc(fireLocal, cistZone); }
            catch { fireUtc = fireLocal.ToUniversalTime(); }

            long when = new DateTimeOffset(fireUtc).ToUnixTimeMilliseconds();
            if (when <= Java.Lang.JavaSystem.CurrentTimeMillis()) continue;

            if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
                alarm.SetAndAllowWhileIdle(AlarmType.RtcWakeup, when, pending);
            else
                alarm.Set(AlarmType.RtcWakeup, when, pending);
        }
    }

    private static void EnsureNotificationChannel(Context context)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var manager = context.GetSystemService(Context.NotificationService) as NotificationManager;
        if (manager == null) return;
        var channel = new NotificationChannel(
            ChannelId, "Напоминания о занятиях", NotificationImportance.Default)
        {
            Description = "Напоминания перед началом занятий по расписанию ХНУРЭ"
        };
        manager.CreateNotificationChannel(channel);
    }

    private static TimeZoneInfo GetCistTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv"); }
        catch
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Kiev"); }
            catch { return TimeZoneInfo.Local; }
        }
    }

    private static int StableId(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""));
        return BitConverter.ToInt32(bytes, 0) & 0x7fffffff;
    }
}
#endif
