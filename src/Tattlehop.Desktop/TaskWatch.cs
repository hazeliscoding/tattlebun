using Tattlehop.Windows;

namespace Tattlehop.Desktop;

/// <summary>Lists scheduled tasks every minute, off the agent's path: a listing takes too long for the hook.</summary>
internal static class TaskWatch
{
    public static void Start() => new Thread(() =>
    {
        while (true)
        {
            try
            {
                using var store = new ReceiptStore(ReceiptStore.DefaultPath);
                store.RecordScheduledTasks(ScheduledTasks.List);
            }
            catch (Exception)
            {
                // A missed listing only widens the next one's interval.
            }

            Thread.Sleep(TimeSpan.FromMinutes(1));
        }
    })
    {
        IsBackground = true,
        Name = "Scheduled tasks",
    }.Start();
}
