using Android.App;
using Android.Content;
using Android.OS;

namespace NureTimetable.Maui;

[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class LessonReminderReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent == null) return;
        var manager = context.GetSystemService(Context.NotificationService) as NotificationManager;
        if (manager == null) return;

        int id = intent.GetIntExtra("notification_id", 0);
        string title = intent.GetStringExtra("title") ?? "Nure Timetable";
        string body = intent.GetStringExtra("body") ?? "Скоро начнётся занятие";

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu &&
            context.CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Android.Content.PM.Permission.Granted)
            return;

        var openIntent = new Intent(context, typeof(MainActivity));
        openIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var contentIntent = PendingIntent.GetActivity(
            context, id, openIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var notification = new Notification.Builder(context, AndroidReminderScheduler.ChannelId)
            .SetContentTitle(title)
            .SetContentText(body)
            .SetSmallIcon(Android.Resource.Drawable.IcDialogInfo)
            .SetContentIntent(contentIntent)
            .SetAutoCancel(true)
            .Build();

        manager.Notify(id, notification);
    }
}
