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
using System.Configuration;

namespace Aufbauwerk.Asterisk.BLF
{
    public sealed class Settings : ConfigurationSection
    {
        private const string InstanceElement = "blfSettings";
        private const string ServersElement = "servers";

        public static Settings Instance { get; } = (Settings)ConfigurationManager.GetSection(InstanceElement);

        [ConfigurationProperty(ServersElement, IsDefaultCollection = false)]
        [ConfigurationCollection(typeof(ServerCollection))]
        public ServerCollection Servers => (ServerCollection)base[ServersElement];

        public sealed class ServerCollection : ConfigurationElementCollection, IEnumerable<Server>
        {
            protected override ConfigurationElement CreateNewElement() => new Server();

            protected override object GetElementKey(ConfigurationElement element) => ((Server)element).Name;

            public new IEnumerator<Server> GetEnumerator()
            {
                var enumerator = base.GetEnumerator();
                while (enumerator.MoveNext()) yield return (Server)enumerator.Current;
            }
        }

        public sealed class Server : ConfigurationElement
        {
            private const string NameAttribute = "name";
            private const string HostAttribute = "host";
            private const string PortAttribute = "port";
            private const string PrefixAttribute = "prefix";
            private const string TimeoutAttribute = "timeout";
            private const string RetryIntervalAttribute = "retryInterval";
            private const string UsernameAttribute = "username";
            private const string SecretAttribute = "secret";
            private const string ExtensionPatternAttribute = "extensionPattern";
            private const string DeviceFormatAttribute = "deviceFormat";

            [ConfigurationProperty(NameAttribute, IsRequired = true, IsKey = true)]
            public string Name => (string)this[NameAttribute];

            [ConfigurationProperty(HostAttribute, IsRequired = true)]
            public string Host => (string)this[HostAttribute];

            [ConfigurationProperty(PortAttribute, DefaultValue = 8088)]
            [IntegerValidator(MinValue = 0, MaxValue = 65535)]
            public int Port => (int)this[PortAttribute];

            [ConfigurationProperty(PrefixAttribute, DefaultValue = "asterisk")]
            public string Prefix => (string)this[PrefixAttribute];

            [ConfigurationProperty(TimeoutAttribute, DefaultValue = "00:00:45")]
            [PositiveTimeSpanValidator]
            public TimeSpan Timeout => (TimeSpan)this[TimeoutAttribute];

            [ConfigurationProperty(RetryIntervalAttribute, DefaultValue = "00:00:30")]
            [PositiveTimeSpanValidator]
            public TimeSpan RetryInterval => (TimeSpan)this[RetryIntervalAttribute];

            [ConfigurationProperty(UsernameAttribute, IsRequired = true)]
            public string Username => (string)this[UsernameAttribute];

            [ConfigurationProperty(SecretAttribute, IsRequired = true)]
            public string Secret => (string)this[SecretAttribute];

            [ConfigurationProperty(ExtensionPatternAttribute, IsRequired = true)]
            public string ExtensionPattern => (string)this[ExtensionPatternAttribute];

            [ConfigurationProperty(DeviceFormatAttribute, DefaultValue = "Custom:$0")]
            public string DeviceFormat => (string)this[DeviceFormatAttribute];
        }
    }
}
