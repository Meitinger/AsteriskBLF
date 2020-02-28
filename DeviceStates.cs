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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Aufbauwerk.Asterisk.BLF
{
    internal static class DeviceStates
    {
        private static readonly Dictionary<string, DeviceState> _global = new Dictionary<string, DeviceState>();

        private static event Action<IEnumerable<KeyValuePair<string, DeviceState>>> UpdateCallbacks;

        public static void Update(IEnumerable<KeyValuePair<string, DeviceState>> updates)
        {
            var callbacks = UpdateCallbacks;
            lock (_global)
            {
                foreach (var update in updates) _global[update.Key] = update.Value;
                callbacks?.Invoke(updates);
            }
        }

        public class Forwarder : IDisposable
        {
            private readonly object _mutex = new object();
            private readonly Settings.Server _settings;
            private readonly Dictionary<string, DeviceState> _currentDeviceStates;
            private readonly Dictionary<string, DeviceState> _deviceStateUpdates;
            private readonly Func<string, DeviceState, Task> _updateFunction;
            private readonly CancellationTokenSource _cancellationSource;
            private readonly CancellationToken _cancellationToken;
            private bool _disposed = false;
            private Task _currentUpdate = null;

            public Forwarder(Settings.Server settings, Dictionary<string, DeviceState> initialDeviceStates, Func<string, DeviceState, Task> updateFunction, CancellationToken token)
            {
                _settings = settings ?? throw new ArgumentNullException(nameof(settings));
                _currentDeviceStates = initialDeviceStates ?? throw new ArgumentNullException(nameof(initialDeviceStates));
                _deviceStateUpdates = new Dictionary<string, DeviceState>();
                _updateFunction = updateFunction ?? throw new ArgumentNullException(nameof(updateFunction));
                _cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(token);
                _cancellationToken = _cancellationSource.Token;
                UpdateCallbacks += UpdateDeviceStates;
                lock (_global) UpdateDeviceStates(_global);
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    UpdateCallbacks -= UpdateDeviceStates;
                    _cancellationSource.Cancel();
                    _cancellationSource.Dispose();
                }
            }

            private void UpdateDeviceStates(IEnumerable<KeyValuePair<string, DeviceState>> updates)
            {
                if (_disposed) return;
                lock (_mutex)
                {
                    foreach (var update in updates)
                    {
                        var device = update.Key;
                        var newState = update.Value;
                        if (_currentDeviceStates.TryGetValue(device, out var oldState) && oldState == newState) _deviceStateUpdates.Remove(device); // the current state was set again
                        else _deviceStateUpdates[device] = newState; // add or update the target state
                    }
                    if (_deviceStateUpdates.Any() &&
                        (_currentUpdate == null || _currentUpdate.IsCompleted) &&
                        !_disposed) _currentUpdate = UpdateDeviceStateAsync(_deviceStateUpdates.First()); // recheck disposed first
                }
            }

            private async Task UpdateDeviceStateAsync(KeyValuePair<string, DeviceState> update)
            {
                while (!_disposed)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var device = update.Key;
                    var state = update.Value;
                    var succeeded = await Server.TryOrWaitAsync(_settings, _cancellationToken, () => _updateFunction(device, state));
                    lock (_mutex)
                    {
                        if (succeeded)
                        {
                            if (_deviceStateUpdates.TryGetValue(device, out var targetState))
                            {
                                if (state == targetState) _deviceStateUpdates.Remove(device); // remove the update if we reached the target state
                            }
                            else _deviceStateUpdates.Add(device, _currentDeviceStates[device]); // update got removed, so restore the old state
                            _currentDeviceStates[device] = state; // always update the current state
                        }
                        if (!_deviceStateUpdates.Any())
                        {
                            _currentUpdate = null;
                            break; // task will be started again once there are new updates
                        }
                        update = _deviceStateUpdates.First(); // perform next update
                    }
                }
            }
        }
    }
}
