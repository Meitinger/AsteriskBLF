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
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Aufbauwerk.Asterisk.BLF
{
    internal enum ExtensionState
    {
        Removed = -2,
        Deactivated = -1,
        Idle = 0x0,
        InUse = 0x1,
        Busy = 0x2,
        Unavailable = 0x4,
        Ringing = 0x8,
        InUse_Ringing = 0x9,
        Hold = 0x10,
        InUse_Hold = 0x11,
    }

    internal enum DeviceState
    {
        UNKNOWN,
        NOT_INUSE,
        INUSE,
        BUSY,
        INVALID,
        UNAVAILABLE,
        RINGING,
        RINGINUSE,
        ONHOLD,
    }

    internal class ExtendedAstersikClient : AsteriskClient
    {
        private readonly Settings.Server _settings;
        private readonly CancellationToken _cancellationToken;

        internal ExtendedAstersikClient(Settings.Server settings, CancellationToken cancellationToken) : base(settings.Host, settings.Port, settings.Prefix)
        {
            _settings = settings;
            _cancellationToken = cancellationToken;
        }

        private CancellationToken GetTimeoutToken()
        {
            var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
            cancellationSource.CancelAfter(_settings.Timeout);
            return cancellationSource.Token;
        }

        public Task LoginAsync() =>
            ExecuteNonQueryAsync(
                new AsteriskAction("Login") {
                    { "Username", _settings.Username },
                    { "Secret", _settings.Secret } },
                GetTimeoutToken());

        public async Task<Dictionary<string, DeviceState>> GetDeviceStatesAsync() =>
            (await ExecuteEnumerationAsync(new AsteriskAction("DeviceStateChange") { }, GetTimeoutToken()))
                .Where(r => string.Equals(r["Event"], "DeviceStateChange", StringComparison.OrdinalIgnoreCase))
                .ToLookup(r => r["Device"], r => Helpers.ParseDeviceState(r["State"]))
                .ToDictionary(l => l.Key, l => l.Last());

        public Task<Dictionary<string, DeviceState>> GetExtensionStatesAsync() => ExtensionEnumerationAsync("ExtensionStateList");

        public Task<Dictionary<string, DeviceState>> GetChangedExtensionStatesAsync() => ExtensionEnumerationAsync("WaitEvent");

        public Task SetDeviceStateAsync(string device, DeviceState state) =>
            ExecuteNonQueryAsync(
                new AsteriskAction("SetVar") {
                    { "Variable", string.Format("DEVICE_STATE({0})", device ?? throw new ArgumentNullException(nameof(device))) },
                    { "Value", Enum.GetName(typeof(DeviceState), state) ?? throw new InvalidEnumArgumentException(nameof(state), (int)state, typeof(DeviceState)) } },
                GetTimeoutToken());

        private async Task<Dictionary<string, DeviceState>> ExtensionEnumerationAsync(string name) =>
            (await ExecuteEnumerationAsync(new AsteriskAction(name) { }, GetTimeoutToken()))
                .Where(r =>
                    string.Equals(r["Event"], "ExtensionStatus", StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(r["Exten"], _settings.ExtensionPattern))
                .ToLookup(
                    r => Regex.Replace(r["Exten"], _settings.ExtensionPattern, _settings.DeviceFormat),
                    r => Helpers.DeviceStateFromExtensionStatus(Helpers.ParseExtensionState(r["Status"])))
                .ToDictionary(l => l.Key, l => l.Last());
    }

    internal static class Helpers
    {
        public static DeviceState ParseDeviceState(string value) =>
            Enum.TryParse<DeviceState>(value ?? throw new ArgumentNullException(nameof(value)), true, out var state)
                ? state
                : throw new AsteriskException($"Device state '{value}' is unrecognized.");

        public static ExtensionState ParseExtensionState(string value) =>
            Enum.TryParse<ExtensionState>(value?.Replace('&', '_') ?? throw new ArgumentNullException(nameof(value)), true, out var state)
                ? state
                : throw new AsteriskException($"Extension state '{value}' is unrecognized.");

        public static DeviceState DeviceStateFromExtensionStatus(ExtensionState value)
        {
            switch (value)
            {
                case ExtensionState.Removed: return DeviceState.INVALID;
                case ExtensionState.Deactivated: return DeviceState.UNKNOWN;
                case ExtensionState.Idle: return DeviceState.NOT_INUSE;
                case ExtensionState.InUse: return DeviceState.INUSE;
                case ExtensionState.Busy: return DeviceState.BUSY;
                case ExtensionState.Unavailable: return DeviceState.UNAVAILABLE;
                case ExtensionState.Ringing: return DeviceState.RINGING;
                case ExtensionState.InUse_Ringing: return DeviceState.RINGINUSE;
                case ExtensionState.Hold: return DeviceState.ONHOLD;
                case ExtensionState.InUse_Hold: return DeviceState.ONHOLD; // HACK: better than UNKNOWN
                default: return DeviceState.UNKNOWN;
            }
        }
    }
}
