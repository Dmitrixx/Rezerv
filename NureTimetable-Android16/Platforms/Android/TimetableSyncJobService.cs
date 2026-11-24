using Android.App;
using Android.App.Job;
using Android.Content;
using NureTimetable.Maui;

namespace NureTimetable.Maui;

[Service(
    Name = "ua.nure.timetable.mobile.TimetableSyncJobService",
    Permission = "android.permission.BIND_JOB_SERVICE",
    Exported = false)]
public sealed class TimetableSyncJobService : JobService
{
    private CancellationTokenSource? _cancellation;

    public override bool OnStartJob(JobParameters? parameters)
    {
        if (parameters == null) return false;
        _cancellation = new CancellationTokenSource();
        _ = RunSyncAsync(parameters, _cancellation.Token);
        return true;
    }

    private async Task RunSyncAsync(JobParameters parameters, CancellationToken cancellationToken)
    {
        try
        {
            var store = new LocalStore();
            var settings = await store.LoadSettingsAsync();
            if (!settings.AutoUpdate) return;

            var entities = await store.LoadEntitiesAsync();
            var selected = entities.Where(e => e.IsSelected).ToList();
            var cist = new CistService(store);
            foreach (var entity in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var from = DateTime.Today.AddDays(-7);
                    var to = from.AddDays(89);
                    var events = await cist.GetScheduleAsync(entity, from, to, cancellationToken);
                    foreach (var ev in events) ev.EntityKey = entity.StorageKey;
                    await store.SaveScheduleAsync(entity, events);
                    entity.LastUpdated = DateTime.Now;
                }
                catch (OperationCanceledException) { throw; }
                catch { /* Keep the last good offline cache on a CIST error. */ }
            }

            settings.LastForegroundSync = DateTime.Now;
            await store.SaveEntitiesAsync(entities);
            await store.SaveSettingsAsync(settings);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            JobFinished(parameters, false);
        }
    }

    public override bool OnStopJob(JobParameters? parameters)
    {
        _cancellation?.Cancel();
        return true;
    }
}
