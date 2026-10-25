using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.TaskScheduler;
using Windows.Win32.System.Variant;
using TaskScheduler = Windows.Win32.System.TaskScheduler.TaskScheduler;

namespace Tattlebun.Windows;

/// <summary>
/// Lists scheduled tasks through the Task Scheduler's COM API, the only way to read them unelevated: its registry
/// cache and System32\Tasks deny access. A listing takes over 100 ms, so the tray app takes it, never the hook.
/// </summary>
public static class ScheduledTasks
{
    private const int TaskEnumHidden = 1;

    /// <summary>Every task this user can see, by its path.</summary>
    public static unsafe Dictionary<string, string> List()
    {
        // S_FALSE and RPC_E_CHANGED_MODE both mean COM is already set up on this thread.
        var initialised = PInvoke.CoInitializeEx(COINIT.COINIT_MULTITHREADED);
        ITaskService* service = null;
        try
        {
            TaskScheduler.CreateInstance(out service).ThrowOnFailure();
            service->Connect(default, default, default, default);
            var root = Marshal.StringToBSTR(@"\");
            ITaskFolder* folder;
            try
            {
                service->GetFolder(new BSTR(root), &folder);
            }
            finally
            {
                Marshal.FreeBSTR(root);
            }

            var tasks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Walk(folder, tasks);
            return tasks;
        }
        finally
        {
            if (service is not null)
            {
                service->Release();
            }

            if (initialised.Succeeded)
            {
                PInvoke.CoUninitialize();
            }
        }
    }

    private static unsafe void Walk(ITaskFolder* folder, Dictionary<string, string> tasks)
    {
        try
        {
            IRegisteredTaskCollection* collection;
            folder->GetTasks(TaskEnumHidden, &collection);
            try
            {
                for (var i = 1; i <= collection->Count; i++)
                {
                    IRegisteredTask* task;
                    collection->get_Item(Index(i), &task);
                    var path = task->Path;
                    tasks[new string(path.Value)] = "1";
                    Marshal.FreeBSTR((nint)path.Value);
                    task->Release();
                }
            }
            finally
            {
                collection->Release();
            }

            ITaskFolderCollection* folders;
            folder->GetFolders(0, &folders);
            try
            {
                for (var i = 1; i <= folders->Count; i++)
                {
                    ITaskFolder* child;
                    folders->get_Item(Index(i), &child);
                    Walk(child, tasks);
                }
            }
            finally
            {
                folders->Release();
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Windows refuses some of its own folders to an unelevated user every time, so skipping them gives the
            // same list each minute. Any other failure ends the whole listing, which then isn't recorded: a partial
            // list would show its missing tasks as removed, and blame whoever happened to be running.
        }
        finally
        {
            folder->Release();
        }
    }

    private static VARIANT Index(int i)
    {
        var index = default(VARIANT);
        index.vt = VARENUM.VT_I4;
        index.lVal = i;
        return index;
    }
}
