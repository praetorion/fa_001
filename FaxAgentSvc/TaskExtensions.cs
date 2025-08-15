using log4net;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FaxAgentSvc
{
    static class TaskExtensions
    {
        public static TaskCompletionSource<T> StartNewTimer<T>(this Func<T> func, TimeSpan timeout, ILog log)
        {
            var tcs = new TaskCompletionSource<T>();
            var timer = new Timer(callback =>
            {
                try
                {
                    if (!tcs.Task.IsCompleted)
                        func();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, null, TimeSpan.FromMilliseconds(0), timeout);
            tcs.Task.ContinueWith(action => timer.Dispose());
            return tcs;
        }

        public static void Wait(this Func<Channel, bool> func, Channel channel, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<bool>();
            var timer = new Timer(callback =>
            {
                try
                {
                    if (!func(channel))
                        tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, null, TimeSpan.FromMilliseconds(0), timeout);
            tcs.Task.ContinueWith(action => timer.Dispose());
        }
    }
}