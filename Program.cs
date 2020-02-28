/* Copyright (C) 2015-2020, Manuel Meitinger
 * 
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 2 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace Aufbauwerk.Asterisk.BLF
{
    internal static class Program
    {
        private static readonly Service _service = new Service();
        private static CancellationTokenSource _cancellationSource;

        private class Service : ServiceBase
        {
            internal Service()
            {
                // specify the name and inform SCM that the service can be stopped
                ServiceName = "AsteriskBLF";
                CanStop = true;
            }

            protected override void OnStart(string[] args)
            {
                // set a non-zero exit code to indicate failure in case something goes wrong and start
                ExitCode = ~0;
                Program.Start();
            }

            protected override void OnStop()
            {
                // stop and reset the exit code to indicate success
                Program.Stop();
                ExitCode = 0;
            }
        }

        private static void Exit(Exception e, int defaultExitCode)
        {
            var exitCode = e == null ? 0 : Marshal.GetHRForException(e);
            Environment.Exit(exitCode == 0 ? defaultExitCode : exitCode);
        }

        private static void HandleUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try { LogEvent(EventLogEntryType.Error, $"Unhandled exception: {e.ExceptionObject}"); } catch { }
            if (e.IsTerminating) Exit(e.ExceptionObject as Exception, -2147418113 /* E_UNEXPECTED */);
        }

        private static void HandleUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            try { LogEvent(EventLogEntryType.Error, $"Unobserved task exception: {e.Exception}"); } catch { }
            if (!e.Observed) Exit(e.Exception, -2147483640 /* E_FAIL */);
        }

        private static void HandleEndOfServerTask(Task task, CancellationTokenSource cancellationSource)
        {
            if (cancellationSource.IsCancellationRequested) return; // to be expected

            Exception exception;
            if (task.IsCanceled) exception = new TaskCanceledException();
            else if (task.IsFaulted) exception = task.Exception;
            else exception = null;

            try { LogEvent(EventLogEntryType.Error, $"A server task ended unexpectedly: {exception}"); } catch { }
            Exit(exception, -2147483641 /* E_ABORT */);
        }

        private static void Start()
        {
            var cancellationSource = new CancellationTokenSource();
            Task.Factory.ContinueWhenAny(
                Settings.Instance.Servers.Select(s => Server.RunAsync(s, cancellationSource.Token)).ToArray(),
                task => HandleEndOfServerTask(task, cancellationSource));
            _cancellationSource = cancellationSource; // static variable must not be captured
        }

        private static void Stop()
        {
            _cancellationSource.Cancel();
            _cancellationSource.Dispose();
        }

        public static void Main()
        {
            // handle all uncaught exceptions
            AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
            TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;

#if DEBUG
            do
            {
                Console.WriteLine("Starting...");
                Start();
                Console.WriteLine("Started. Press ENTER to stop.");
                if (Console.ReadLine() == null) break;
                Console.WriteLine("Stopping...");
                Stop();
                Console.WriteLine("Stopped. Press ENTER to start.");
            }
            while (Console.ReadLine() != null);
#else
            ServiceBase.Run(_service);
#endif
        }

        internal static void LogEvent(EventLogEntryType type, string message)
        {
#if DEBUG
            Console.WriteLine($"{type}: {message}");
#else
            _service.EventLog.WriteEntry(message, type);
#endif
        }
    }
}
