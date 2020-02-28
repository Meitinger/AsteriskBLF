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

using Aufbauwerk.Net.Asterisk;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Aufbauwerk.Asterisk.BLF
{
    internal static class Server
    {
        public static async Task RunAsync(Settings.Server settings, CancellationToken cancellationToken)
        {
            using (var client = new ExtendedAstersikClient(settings ?? throw new ArgumentNullException(nameof(settings)), cancellationToken))
            {
                do { cancellationToken.ThrowIfCancellationRequested(); }
                while (!await TryOrWaitAsync(settings, cancellationToken, () => DoWorkAsync(client, settings, cancellationToken)));
            }
        }

        private static async Task DoWorkAsync(ExtendedAstersikClient client, Settings.Server settings, CancellationToken cancellationToken)
        {
            await client.LoginAsync();
            Program.LogEvent(EventLogEntryType.Information, $"{settings.Name}: Successfully logged in.");
            using (new DeviceStates.Forwarder(settings, await client.GetDeviceStatesAsync(), client.SetDeviceStateAsync, cancellationToken))
            {
                DeviceStates.Update(await client.GetExtensionStatesAsync());
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DeviceStates.Update(await client.GetChangedExtensionStatesAsync());
                }
            }
        }

        internal static async Task<bool> TryOrWaitAsync(Settings.Server settings, CancellationToken cancellationToken, Func<Task> task)
        {
            try { await task(); return true; }
            catch (HttpRequestException e) { Program.LogEvent(EventLogEntryType.Warning, $"{settings.Name} (HTTP): {e.Message}"); }
            catch (AsteriskException e) { Program.LogEvent(EventLogEntryType.Warning, $"{settings.Name} (AMI): {e.Message}"); }
            await Task.Delay(settings.RetryInterval, cancellationToken);
            return false;
        }
    }
}
