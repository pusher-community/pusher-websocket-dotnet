﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json;

namespace PusherClient
{
    /* TODO: Write tests
     * - Websocket disconnect
        - Connection lost, not cleanly closed
        - MustConnectOverSSL = 4000,
        - App does not exist
        - App disabled
        - Over connection limit
        - Path not found
        - Client over rate limie
        - Conditions for client event triggering
     */
    // TODO: NUGET Package
    // TODO: Ping & pong, are these handled by the Webscoket library out of the box?
    // TODO: Add assembly info file?
    // TODO: Implement connection fallback strategy

    // A delegate type for hooking up change notifications.
    public delegate void ErrorEventHandler(object sender, PusherException error);
    public delegate void ConnectedEventHandler(object sender);
    public delegate void ConnectionStateChangedEventHandler(object sender, ConnectionState state);

    public class Pusher : EventEmitter, IPusher
    {
        public event ConnectedEventHandler Connected;
        public event ConnectedEventHandler Disconnected;
        public event ConnectionStateChangedEventHandler ConnectionStateChanged;

        // create single TraceSource instance to be used for logging
        public static TraceSource Trace = new TraceSource(nameof(Pusher));

        private readonly string _applicationKey;
        private readonly PusherOptions _options;

        private Connection _connection;

        private readonly object _lockingObject = new object();

        private List<string> _pendingChannelSubscriptions = new List<string>();

        public event ErrorEventHandler Error;

        public string SocketID => _connection?.SocketId;

        public ConnectionState State => _connection?.State ?? ConnectionState.Disconnected;

        public ConcurrentDictionary<string, Channel> Channels { get; set; } = new ConcurrentDictionary<string, Channel>();

        internal PusherOptions Options => _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="Pusher" /> class.
        /// </summary>
        /// <param name="applicationKey">The application key.</param>
        /// <param name="options">The options.</param>
        public Pusher(string applicationKey, PusherOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(applicationKey))
                throw new ArgumentException(ErrorConstants.ApplicationKeyNotSet, nameof(applicationKey));

            _applicationKey = applicationKey;
            _options = null;

            _options = options ?? new PusherOptions { Encrypted = false };
        }



        void IPusher.ConnectionStateChanged(ConnectionState state)
        {
            if (state == ConnectionState.Connected)
            {
                SubscribeExistingChannels();

                if (Connected != null)
                    Connected(this);
            }
            else if (state == ConnectionState.Disconnected)
            {
                MarkChannelsAsUnsubscribed();

                if (Disconnected != null)
                    Disconnected(this);

                if (ConnectionStateChanged != null)
                    ConnectionStateChanged(this, state);
            }
            else
            {
                if (ConnectionStateChanged != null)
                    ConnectionStateChanged(this, state);
            }
        }

        void IPusher.ErrorOccured(PusherException pusherException)
        {
            RaiseError(pusherException);
        }

        void IPusher.EmitPusherEvent(string eventName, string data)
        {
            EmitEvent(eventName, data);
        }

        void IPusher.EmitChannelEvent(string channelName, string eventName, string data)
        {
            if (Channels.ContainsKey(channelName))
            {
                Channels[channelName].EmitEvent(eventName, data);
            }
        }

        void IPusher.AddMember(string channelName, string member)
        {
            if (Channels.Keys.Contains(channelName) && Channels[channelName] is PresenceChannel)
            {
                ((PresenceChannel)Channels[channelName]).AddMember(member);
            }
        }

        void IPusher.RemoveMember(string channelName, string member)
        {
            if (Channels.Keys.Contains(channelName) && Channels[channelName] is PresenceChannel)
            {
                ((PresenceChannel)Channels[channelName]).RemoveMember(member);
            }
        }

        void IPusher.SubscriptionSuceeded(string channelName, string data)
        {
            if (_pendingChannelSubscriptions.Contains(channelName))
                _pendingChannelSubscriptions.Remove(channelName);

            if (Channels.Keys.Contains(channelName))
            {
                Channels[channelName].SubscriptionSucceeded(data);
            }
        }



        public void Connect()
        {
            // Prevent multiple concurrent connections
            lock(_lockingObject)
            {
                // Ensure we only ever attempt to connect once
                if (_connection != null)
                {
                    Trace.TraceEvent(TraceEventType.Warning, 0, ErrorConstants.ConnectionAlreadyConnected);
                    return;
                }

                var scheme = _options.Encrypted ? Constants.SECURE_SCHEMA : Constants.INSECURE_SCHEMA;

                // TODO: Fallback to secure?

                var url = $"{scheme}{_options.Host}/app/{_applicationKey}?protocol={Settings.Default.ProtocolVersion}&client={Settings.Default.ClientName}&version={Settings.Default.VersionNumber}";

                _connection = new Connection(this, url);
                _connection.Connect();
            }
        }

        public void Disconnect()
        {
            if (_connection != null)
            {
                MarkChannelsAsUnsubscribed();
                _connection.Disconnect();
                _connection = null;
            }
        }

        public Channel Subscribe(string channelName)
        {
            if (string.IsNullOrWhiteSpace(channelName))
            {
                throw new ArgumentException("The channel name cannot be null or whitespace", nameof(channelName));
            }

            if (AlreadySubscribed(channelName))
            {
                Trace.TraceEvent(TraceEventType.Warning, 0, "Channel '" + channelName + "' is already subscribed to. Subscription event has been ignored.");
                return Channels[channelName];
            }
            
            _pendingChannelSubscriptions.Add(channelName);

            return SubscribeToChannel(channelName);
        }



        private Channel SubscribeToChannel(string channelName)
        {
            var channelType = GetChannelType(channelName);

            if (!Channels.ContainsKey(channelName))
                CreateChannel(channelType, channelName);

            if (State == ConnectionState.Connected)
            {
                if (channelType == ChannelTypes.Presence || channelType == ChannelTypes.Private)
                {
                    var jsonAuth = _options.Authorizer.Authorize(channelName, _connection.SocketId);

                    var template = new { auth = string.Empty, channel_data = string.Empty };
                    var message = JsonConvert.DeserializeAnonymousType(jsonAuth, template);

                    _connection.Send(JsonConvert.SerializeObject(new { @event = Constants.CHANNEL_SUBSCRIBE, data = new { channel = channelName, auth = message.auth, channel_data = message.channel_data } }));
                }
                else
                {
                    // No need for auth details. Just send subscribe event
                    _connection.Send(JsonConvert.SerializeObject(new { @event = Constants.CHANNEL_SUBSCRIBE, data = new { channel = channelName } }));
                }
            }

            return Channels[channelName];
        }

        private static ChannelTypes GetChannelType(string channelName)
        {
            // If private or presence channel, check that auth endpoint has been set
            var channelType = ChannelTypes.Public;

            if (channelName.ToLowerInvariant().StartsWith(Constants.PRIVATE_CHANNEL))
            {
                channelType = ChannelTypes.Private;
            }
            else if (channelName.ToLowerInvariant().StartsWith(Constants.PRESENCE_CHANNEL))
            {
                channelType = ChannelTypes.Presence;
            }
            return channelType;
        }

        private void CreateChannel(ChannelTypes type, string channelName)
        {
            switch (type)
            {
                case ChannelTypes.Public:
                    Channels[channelName] = new Channel(channelName, this);
                    break;
                case ChannelTypes.Private:
                    AuthEndpointCheck();
                    Channels[channelName] = new PrivateChannel(channelName, this);
                    break;
                case ChannelTypes.Presence:
                    AuthEndpointCheck();
                    Channels[channelName] = new PresenceChannel(channelName, this);
                    break;
            }
        }

        private void AuthEndpointCheck()
        {
            if (_options.Authorizer == null)
            {
                var pusherException = new PusherException("You must set a ChannelAuthorizer property to use private or presence channels", ErrorCodes.ChannelAuthorizerNotSet);
                RaiseError(pusherException);
                throw pusherException;
            }
        }

        internal void Trigger(string channelName, string eventName, object obj)
        {
            _connection.Send(JsonConvert.SerializeObject(new { @event = eventName, channel = channelName, data = obj }));
        }

        internal void Unsubscribe(string channelName)
        {
            if (_connection.IsConnected)

              _connection.Send(JsonConvert.SerializeObject(new { @event = Constants.CHANNEL_UNSUBSCRIBE, data = new { channel = channelName } }));
        }

        private void RaiseError(PusherException error)
        {
            var handler = Error;

            if (handler != null)
                handler(this, error);
        }

        private bool AlreadySubscribed(string channelName)
        {
            return _pendingChannelSubscriptions.Contains(channelName) || (Channels.ContainsKey(channelName) && Channels[channelName].IsSubscribed);
        }

        private void MarkChannelsAsUnsubscribed()
        {
            foreach (var channel in Channels)
            {
                channel.Value.Unsubscribe();
            }
        }

        private void SubscribeExistingChannels()
        {
            foreach (var channel in Channels)
            {
                SubscribeToChannel(channel.Key);
            }
        }
    }
}