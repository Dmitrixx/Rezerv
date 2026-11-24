using Android.App;
using Android.App.Job;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Microsoft.Maui;

namespace NureTimetable.Maui;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation
        | ConfigChanges.UiMode | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SchedulePeriodicSync();
    }

    private void SchedulePeriodicSync()
    {
        try
        {
            var component = new ComponentName(this, Java.Lang.Class.FromType(typeof(TimetableSyncJobService)));
            var job = new JobInfo.Builder(18204, component)
                .SetRequiredNetworkType(NetworkType.Any)
                .SetPersisted(true)
                .SetPeriodic(12 * 60 * 60 * 1000L)
                .Build();
            var scheduler = GetSystemService(JobSchedulerService) as JobScheduler;
            scheduler?.Schedule(job);
        }
        catch
        {
            // Foreground update remains available if Android blocks background scheduling.
        }
    }
}
