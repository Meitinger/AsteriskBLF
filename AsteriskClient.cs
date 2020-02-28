/* Copyright (C) 2013-2020, Manuel Meitinger
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
using System.Collections.Specialized;
using System.Net;
using System.Net.Http;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Aufbauwerk.Net.Asterisk
{
    /// <summary>
    /// Represents a client for an Asterisk Manager Interface via HTTP.
    /// </summary>
    public class AsteriskClient : HttpMessageInvoker
    {
        /// <summary>
        /// Represents a parser for the raw AMI result.
        /// </summary>
        /// <typeparam name="T">The result type.</typeparam>
        protected abstract class Parser<T>
        {
            /// <summary>
            /// Creates a new parser instance.
            /// </summary>
            /// <param name="action">The action whose results should be parsed.</param>
            /// <exception cref="System.ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
            public Parser(AsteriskAction action)
            {
                Action = action ?? throw ExceptionBuilder.NullArgument(nameof(action));
            }

            internal AsteriskAction Action { get; }

            /// <summary>
            /// Indicates whether the parser should be run synchronously. Default is <c>true</c>.
            /// </summary>
            public virtual bool ExecuteSynchronously => true;

            /// <summary>
            /// Parses the result of an AMI action.
            /// </summary>
            /// <param name="s">The raw AMI result as <see cref="string"/>.</param>
            /// <returns>The properly typed result.</returns>
            /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
            public abstract T Parse(string s);

            internal string ExpectedResponse
            {
                get
                {
                    return
                        string.Equals(Action.Name, "Ping", StringComparison.OrdinalIgnoreCase) ? "Pong" :
                        string.Equals(Action.Name, "Logoff", StringComparison.OrdinalIgnoreCase) ? "Goodbye" :
                        "Success";
                }
            }
        }

        private class QueryParser : Parser<AsteriskResponse>
        {
            public QueryParser(AsteriskAction action) : base(action) { }

            public override AsteriskResponse Parse(string s) => new AsteriskResponse(s, ExpectedResponse);
        }

        private class NonQueryParser : Parser<bool>
        {
            public NonQueryParser(AsteriskAction action) : base(action) { }

            public override bool Parse(string s) => new AsteriskResponse(s, ExpectedResponse) != null;
        }

        private class ScalarParser : Parser<string>
        {
            public ScalarParser(AsteriskAction action, string valueName) : base(action)
            {
                ValueName = valueName ?? throw ExceptionBuilder.NullArgument(nameof(valueName));
                if (valueName.Length == 0) throw ExceptionBuilder.EmptyArgument(nameof(valueName));
            }

            public string ValueName { get; }

            public override string Parse(string s) => new AsteriskResponse(s, ExpectedResponse).Get(ValueName);
        }

        private class EnumerationParser : Parser<AsteriskEnumeration>
        {
            public EnumerationParser(AsteriskAction action) :
                this(action ?? throw ExceptionBuilder.NullArgument(nameof(action)), action.Name + "Complete")
            { }

            public EnumerationParser(AsteriskAction action, string completeEventName) : base(action)
            {
                CompleteEventName = completeEventName ?? throw ExceptionBuilder.NullArgument(nameof(completeEventName));
                if (completeEventName.Length == 0) throw ExceptionBuilder.EmptyArgument(nameof(completeEventName));
            }

            public string CompleteEventName { get; }

            public override bool ExecuteSynchronously => false;

            public override AsteriskEnumeration Parse(string s) => new AsteriskEnumeration(s, CompleteEventName);
        }

        private readonly Uri _baseAddress;

        /// <summary>
        /// Creates a new instance by building the base uri.
        /// </summary>
        /// <param name="host">The server name or IP.</param>
        /// <param name="port">The port on which the Asterisk micro webserver listens.</param>
        /// <param name="prefix">The prefix to the manager endpoints.</param>
        /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="port"/> is less than -1 or greater than 65,535.</exception>
        /// <exception cref="System.UriFormatException">The URI constructed by the parameters is invalid.</exception>
        public AsteriskClient(string host, int port = 8088, string prefix = "asterisk") :
            this(new UriBuilder("http", host, port, prefix).Uri)
        { }

        /// <summary>
        /// Creates a new client instance.
        /// </summary>
        /// <param name="baseAddress">The manager base address.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="baseAddress"/> is <c>null</c>.</exception>
        public AsteriskClient(Uri baseAddress) :
            this(baseAddress, new HttpClientHandler(), true)
        { }

        /// <summary>
        /// Creates a new client instance without a base address.
        /// </summary>
        /// <param name="baseAddress">The manager base address.</param>
        /// <param name="handler">The <see cref="System.Net.Http.HttpMessageHandler"/> responsible for processing the HTTP response messages.</param>
        /// <param name="disposeHandler"><c>true</c> if the inner handler should be disposed of by <see cref="System.Net.Http.HttpClient.Dispose(bool)"/>; <c>false</c> if you intend to reuse the inner handler.</param>
        /// <exception cref="System.ArgumentNullException">The <paramref name="handler"/> is <c>null</c>.</exception>
        protected AsteriskClient(Uri baseAddress, HttpMessageHandler handler, bool disposeHandler = true) : base(handler, disposeHandler)
        {
            _baseAddress = baseAddress ?? throw ExceptionBuilder.NullArgument(nameof(baseAddress));
        }

        /// <summary>
        /// Executes an asynchronous AMI action.
        /// </summary>
        /// <typeparam name="T">The result type.</typeparam>
        /// <param name="parser">A <see cref="Parser{T}"/> that converts the raw AMI result into type <typeparamref name="T"/>.</param>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        /// <returns>The task object representing the asynchronous operation.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="parser"/> is <c>null</c>.</exception>
        /// <exception cref="System.Net.Http.HttpRequestException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        protected virtual async Task<T> ExecuteAsync<T>(Parser<T> parser, CancellationToken cancellationToken)
        {
            if (parser == null) throw ExceptionBuilder.NullArgument(nameof(parser));
            return parser.Parse(await
                (await SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(_baseAddress, parser.Action.ToString())), cancellationToken))
                .EnsureSuccessStatusCode().Content.ReadAsStringAsync());
        }

        /// <summary>
        /// Executes an asynchronous non-query operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        /// <returns>The task object representing the asynchronous operation.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
        /// <exception cref="System.Net.Http.HttpRequestException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public Task ExecuteNonQueryAsync(AsteriskAction action, CancellationToken cancellationToken) =>
            ExecuteAsync(new NonQueryParser(action), cancellationToken);

        /// <summary>
        /// Executes an asynchronous scalar operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <param name="valueName">The name of the value to return.</param>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        /// <returns>The task object representing the asynchronous operation.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> or <paramref name="valueName"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="valueName"/> is empty.</exception>
        /// <exception cref="System.Net.Http.HttpRequestException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public Task<string> ExecuteScalarAsync(AsteriskAction action, string valueName, CancellationToken cancellationToken) =>
            ExecuteAsync(new ScalarParser(action, valueName), cancellationToken);

        /// <summary>
        /// Executes an asynchronous query operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        /// <returns>The task object representing the asynchronous operation.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
        /// <exception cref="System.Net.Http.HttpRequestException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public Task<AsteriskResponse> ExecuteQueryAsync(AsteriskAction action, CancellationToken cancellationToken) =>
            ExecuteAsync(new QueryParser(action), cancellationToken);

        /// <summary>
        /// Executes an asynchronous enumeration operation.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        /// <returns>An <see cref="Aufbauwerk.Net.Asterisk.AsteriskEnumeration"/> instance.</returns>
        /// <returns>The task object representing the asynchronous operation.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
        /// <exception cref="System.Net.Http.HttpRequestException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public Task<AsteriskEnumeration> ExecuteEnumerationAsync(AsteriskAction action, CancellationToken cancellationToken) =>
            ExecuteAsync(new EnumerationParser(action), cancellationToken);

        /// <summary>
        /// Executes an asynchronous enumeration operation with a given completion event name.
        /// </summary>
        /// <param name="action">The Asterisk Manager action definition.</param>
        /// <param name="completeEventName">The name of the complete name.</param>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        /// <returns>The task object representing the asynchronous operation.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="action"/> or <paramref name="completeEventName"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="completeEventName"/> is empty.</exception>
        /// <exception cref="System.Net.Http.HttpRequestException">An error occurred while querying the server.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response contains an error.</exception>
        public Task<AsteriskEnumeration> ExecuteEnumerationAsync(AsteriskAction action, string completeEventName, CancellationToken cancellationToken) =>
            ExecuteAsync(new EnumerationParser(action, completeEventName), cancellationToken);
    }

    internal static class ExceptionBuilder
    {
        private static readonly ResourceManager Res = new ResourceManager(typeof(AsteriskClient));

        private static String GetString([CallerMemberName] string functionName = null) => Res.GetString(functionName);

        internal static ArgumentNullException NullArgument(string paramName) => new ArgumentNullException(paramName, string.Format(GetString(), paramName));
        internal static ArgumentException EmptyArgument(string paramName) => new ArgumentException(paramName, string.Format(GetString(), paramName));
        internal static AsteriskException ResultSetMultipleEncountered() => new AsteriskException(GetString());
        internal static AsteriskException ResultSetKeyNotFound(string name) => new AsteriskException(string.Format(GetString(), name));
        internal static AsteriskException ResultSetKeyNotUnique(string name) => new AsteriskException(string.Format(GetString(), name));
        internal static AsteriskException ResponseUnexpected(string response, string expectedResponse, string message) => new AsteriskException(string.Format(GetString(), response, expectedResponse, message));
        internal static AsteriskException EnumerationResponseMissing() => new AsteriskException(GetString());
        internal static AsteriskException EnumerationCompleteEventMissing() => new AsteriskException(GetString());
    }

    /// <summary>
    /// Represents an Asterisk Manager action request.
    /// </summary>
    public sealed class AsteriskAction : System.Collections.IEnumerable
    {
        private readonly StringBuilder _queryBuilder = new StringBuilder("rawman?action=");
        private readonly NameValueCollection _parameters = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
        private string _cachedQuery;

        private class ReadOnlyNameValueCollection : NameValueCollection
        {
            internal ReadOnlyNameValueCollection(NameValueCollection col) : base(col)
            {
                IsReadOnly = true;
            }
        }

        /// <summary>
        /// Creates a new action query definition.
        /// </summary>
        /// <param name="name">The name of the action.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="name"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="name"/> is empty.</exception>
        public AsteriskAction(string name)
        {
            // add the action param 
            Name = name ?? throw ExceptionBuilder.NullArgument(nameof(name));
            if (name.Length == 0) throw ExceptionBuilder.EmptyArgument(nameof(name));
            _queryBuilder.Append(Uri.EscapeUriString(name));
            _cachedQuery = null;
        }

        /// <summary>
        /// Adds another parameter to the action.
        /// </summary>
        /// <param name="paramName">The parameter name.</param>
        /// <param name="paramValue">The value of the parameter.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="paramName"/> or <paramref name="paramValue"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="paramName"/> is empty.</exception>
        public void Add(string paramName, string paramValue)
        {
            // check, escape and add the param
            if (paramName == null) throw ExceptionBuilder.NullArgument(nameof(paramName));
            if (paramName.Length == 0) throw ExceptionBuilder.EmptyArgument(nameof(paramName));
            if (paramValue == null) throw ExceptionBuilder.NullArgument(nameof(paramValue));
            _parameters.Add(paramName, paramValue);
            _queryBuilder.Append('&');
            _queryBuilder.Append(Uri.EscapeUriString(paramName));
            _queryBuilder.Append('=');
            _queryBuilder.Append(Uri.EscapeUriString(paramValue));
            _cachedQuery = null;
        }

        /// <summary>
        /// Gets the name of this action.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets a read-only copy of the parameters.
        /// </summary>
        public NameValueCollection Parameters => new ReadOnlyNameValueCollection(_parameters);

        /// <summary>
        /// Returns the entire <c>rawman</c> action URL.
        /// </summary>
        /// <returns>A relative URL.</returns>
        public override string ToString()
        {
            if (_cachedQuery == null) _cachedQuery = _queryBuilder.ToString();
            return _cachedQuery;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _parameters.GetEnumerator();
    }

    /// <summary>
    /// A <see cref="System.Collections.Specialized.NameValueCollection"/> that behaves more like <see cref="System.Collections.IDictionary"/> and is read-only.
    /// </summary>
    public class AsteriskResultSet : NameValueCollection
    {
        private static readonly string[] LineSeparator = new string[] { "\r\n" };
        private static readonly char[] PartSeparator = new char[] { ':' };

        /// <summary>
        /// Creates a result set.
        /// </summary>
        /// <param name="input">The server response.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="input"/> is <c>null</c>.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response is invalid.</exception>
        public AsteriskResultSet(string input) : base(StringComparer.OrdinalIgnoreCase)
        {
            // check the input
            if (input == null) throw ExceptionBuilder.NullArgument(nameof(input));
            if (input.Contains("\n\r\n\r")) throw ExceptionBuilder.ResultSetMultipleEncountered();

            // split the lines
            var lines = input.Split(LineSeparator, StringSplitOptions.RemoveEmptyEntries);

            // add each name-value pair
            for (int i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split(PartSeparator, 2);
                if (parts.Length == 2) Add(parts[0].Trim(), parts[1].Trim());
                else Add(null, parts[0].Trim());
            }

            // don't allow further modifications
            IsReadOnly = true;
        }

        /// <summary>
        /// Gets the value associated with the given key.
        /// </summary>
        /// <param name="name">The key of the entry that contains the value.</param>
        /// <returns>A <see cref="string"/> that contains the value.</returns>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">There are either no or multiple associated values.</exception>
        public override string Get(string name)
        {
            // ensure that there is one and only one value
            var values = base.GetValues(name);
            if (values == null || values.Length == 0) throw ExceptionBuilder.ResultSetKeyNotFound(name);
            if (values.Length > 1) throw ExceptionBuilder.ResultSetKeyNotUnique(name);
            return values[0];
        }
    }

    /// <summary>
    /// A result set with additional response metadata.
    /// </summary>
    public class AsteriskResponse : AsteriskResultSet
    {
        /// <summary>
        /// Creates a response result set.
        /// </summary>
        /// <param name="input">The server response.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="input"/> is <c>null</c>.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response is invalid.</exception>
        public AsteriskResponse(string input) : base(input)
        {
            // set the status and message
            Status = Get("Response");
            var messages = GetValues("Message");
            Message = messages == null || messages.Length == 0 ? null : string.Join(Environment.NewLine, messages);
        }

        /// <summary>
        /// Creates a new response result set and ensures that the status is as expected.
        /// </summary>
        /// <param name="input">The server response.</param>
        /// <param name="expectedResponseStatus">The expected status code.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="input"/> or <paramref name="expectedResponseStatus"/> is <c>null</c>.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response is invalid.</exception>
        public AsteriskResponse(string input, string expectedResponseStatus) : this(input)
        {
            // check the response status
            if (expectedResponseStatus == null) throw ExceptionBuilder.NullArgument(nameof(expectedResponseStatus));
            if (!string.Equals(Status, expectedResponseStatus, StringComparison.OrdinalIgnoreCase)) throw ExceptionBuilder.ResponseUnexpected(Status, expectedResponseStatus, Message);
        }

        /// <summary>
        /// Gets the value of the response field, usually <c>Success</c> or <c>Error</c>.
        /// </summary>
        public string Status { get; }

        /// <summary>
        /// Gets the optional status message.
        /// </summary>
        public string Message { get; }
    }

    /// <summary>
    /// Represents an Asterisk Manager event.
    /// </summary>
    public class AsteriskEvent : AsteriskResultSet
    {
        /// <summary>
        /// Creates a new event description.
        /// </summary>
        /// <param name="input">The server response.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="input"/> is <c>null</c>.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response is invalid.</exception>
        public AsteriskEvent(string input) : base(input)
        {
            // get the event name
            EventName = Get("Event");
        }

        /// <summary>
        /// Gets the name of the current event.
        /// </summary>
        public string EventName { get; }
    }

    /// <summary>
    /// A collection of events and metadata.
    /// </summary>
    public class AsteriskEnumeration : IEnumerable<AsteriskEvent>
    {
        private static readonly string[] ResultSetSeparator = new string[] { "\r\n\r\n" };

        private readonly AsteriskEvent[] _events;

        /// <summary>
        /// Creates a new enumeration from a manager response.
        /// </summary>
        /// <param name="input">The server response.</param>
        /// <param name="expectedCompleteEventName">The name of the event that ends the enumeration.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="input"/> or <paramref name="expectedCompleteEventName"/> is <c>null</c>.</exception>
        /// <exception cref="Aufbauwerk.Net.Asterisk.AsteriskException">The server response is invalid.</exception>
        public AsteriskEnumeration(string input, string expectedCompleteEventName)
        {
            // check the input
            if (input == null) throw ExceptionBuilder.NullArgument(nameof(input));
            if (expectedCompleteEventName == null) throw ExceptionBuilder.NullArgument(nameof(expectedCompleteEventName));

            // split the events
            var items = input.Split(ResultSetSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (items.Length == 0) throw ExceptionBuilder.EnumerationResponseMissing();

            // get (and check) the response result set
            Response = new AsteriskResponse(items[0], "Success");

            // get the complete event
            if (items.Length == 1) throw ExceptionBuilder.EnumerationCompleteEventMissing();
            CompleteEvent = new AsteriskEvent(items[items.Length - 1]);
            if (!string.Equals(CompleteEvent.EventName, expectedCompleteEventName, StringComparison.OrdinalIgnoreCase)) throw ExceptionBuilder.EnumerationCompleteEventMissing();

            // get the rest
            _events = new AsteriskEvent[items.Length - 2];
            for (int i = 0; i < _events.Length; i++) _events[i] = new AsteriskEvent(items[i + 1]);
        }

        /// <summary>
        /// Gets the response that was sent before any event.
        /// </summary>
        public AsteriskResponse Response { get; }

        /// <summary>
        /// Gets the event that was sent after the enumeration was complete.
        /// </summary>
        public AsteriskEvent CompleteEvent { get; }

        /// <summary>
        /// Gets the event at a certain position within the enumeration.
        /// </summary>
        /// <param name="index">The offset within the enumeration.</param>
        /// <returns>The event description.</returns>
        /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="index"/> is less than zero or equal to or greater than <see cref="Count"/>.</exception>
        public AsteriskEvent this[int index] => _events[index];

        /// <summary>
        /// Gets the number of events that were returned by the Asterisk Manager, excluding <see cref="CompleteEvent"/>.
        /// </summary>
        public int Count => _events.Length;

        /// <summary>
        /// Returns an enumerator that iterates through the retrieved events.
        /// </summary>
        /// <returns>An enumerator that can be used to iterate through the returned events.</returns>
        public IEnumerator<AsteriskEvent> GetEnumerator() => ((IEnumerable<AsteriskEvent>)_events).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Represents an Asterisk Manager error.
    /// </summary>
    [Serializable]
    public class AsteriskException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Aufbauwerk.Net.Asterisk.AsteriskException"/> class with a specified error message.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public AsteriskException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="Aufbauwerk.Net.Asterisk.AsteriskException"/> class with a specified error message and a reference to the inner exception that is the cause of this exception.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        /// <param name="inner">The exception that is the cause of the current exception, or a <c>null</c> reference if no inner exception is specified.</param>
        public AsteriskException(string message, Exception inner) : base(message, inner) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="Aufbauwerk.Net.Asterisk.AsteriskException"/> class with serialized data.
        /// </summary>
        /// <param name="info">The <see cref="System.Runtime.Serialization.SerializationInfo"/> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="System.Runtime.Serialization.StreamingContext"/> that contains contextual information about the source or destination.</param>
        protected AsteriskException(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
}
