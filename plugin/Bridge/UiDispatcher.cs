using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using Newtonsoft.Json.Linq;
using OpenRhinoMcp.Core;
using Rhino;

namespace OpenRhinoMcp.Bridge
{
    /// <summary>Runs work on Rhino's UI thread and blocks the calling socket thread until done.</summary>
    public static class UiDispatcher
    {
        public static JToken Run(Func<JToken> work, int timeoutMs)
        {
            JToken result = null;
            Exception error = null;
            var done = new ManualResetEventSlim(false);

            Action action = () =>
            {
                try { result = work(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            };

            RhinoApp.InvokeOnUiThread(action);

            if (!done.Wait(timeoutMs))
                throw new RpcException("timeout",
                    $"Rhino didn't finish within {timeoutMs / 1000} s. It may be showing a dialog, waiting for input in a command, " +
                    "or the operation is still running (it may still complete). Check rhino_command_history.");

            if (error != null)
            {
                if (error is RpcException) throw error;
                ExceptionDispatchInfo.Capture(error).Throw();
            }
            return result;
        }
    }
}
