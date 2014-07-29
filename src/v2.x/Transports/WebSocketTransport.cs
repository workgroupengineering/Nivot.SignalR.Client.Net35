﻿using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR.Client.Http;
using Microsoft.AspNet.SignalR.Client.Infrastructure;
using Microsoft.AspNet.SignalR.Client.Transports.WebSockets;
using Microsoft.AspNet.SignalR.Client.WebSockets;
using WebSocketSharp;
using WebSocketHandler = Microsoft.AspNet.SignalR.Client.WebSockets.WebSocketHandler;

namespace Microsoft.AspNet.SignalR.Client.Transports
{
    public class WebSocketTransport : WebSocketHandler, IClientTransport
    {
        private readonly IHttpClient _client;
        private readonly TransportAbortHandler _abortHandler;
        private CancellationToken _disconnectToken;
        private TransportInitializationHandler _initializeHandler;
        private WebSocketConnectionInfo _connectionInfo;
        private CancellationTokenSource _webSocketTokenSource;
        private ClientWebSocket _webSocket;
        private int _disposed;

        public WebSocketTransport()
            : this(new DefaultHttpClient()) {
        }

        public WebSocketTransport(IHttpClient client)
            : base(maxIncomingMessageSize: null) // Disable max incoming message size on the client
        {
            _client = client;
            _disconnectToken = CancellationToken.None;
            _abortHandler = new TransportAbortHandler(client, Name);
            ReconnectDelay = TimeSpan.FromSeconds(2);
        }

        /// <summary>
        /// The time to wait after a connection drops to try reconnecting.
        /// </summary>
        public TimeSpan ReconnectDelay { get; set; }

        /// <summary>
        /// Indicates whether or not the transport supports keep alive
        /// </summary>
        public bool SupportsKeepAlive {
            get {
                return true;
            }
        }

        /// <summary>
        /// The name of the transport.
        /// </summary>
        public string Name {
            get {
                return "webSockets";
            }
        }

        public Task<NegotiationResponse> Negotiate(IConnection connection, string connectionData) {
            return _client.GetNegotiationResponse(connection, connectionData);
        }

        public virtual Task Start(IConnection connection, string connectionData, CancellationToken disconnectToken) {
            if (connection == null) {
                throw new ArgumentNullException("connection");
            }

            _initializeHandler = new TransportInitializationHandler(connection.TransportConnectTimeout, disconnectToken);

            // Tie into the OnFailure event so that we can stop the transport silently.
            _initializeHandler.OnFailure += () => {
                Dispose();
            };

            _disconnectToken = disconnectToken;
            _connectionInfo = new WebSocketConnectionInfo(connection, connectionData);

            // We don't need to await this task
            PerformConnect().ContinueWith(task => {
                if (task.IsFaulted) {
                    _initializeHandler.Fail(task.Exception);
                } else if (task.IsCanceled) {
                    _initializeHandler.Fail();
                }
            },
            TaskContinuationOptions.NotOnRanToCompletion);

            return _initializeHandler.Task;
        }

        // For testing
        public virtual Task PerformConnect() {
            return PerformConnect(reconnecting: false);
        }

        private async Task PerformConnect(bool reconnecting) {
            var url = _connectionInfo.Connection.Url + (reconnecting ? "reconnect" : "connect");
            url += TransportHelper.GetReceiveQueryString(_connectionInfo.Connection, _connectionInfo.Data, "webSockets");
            var builder = new UriBuilder(url);
            builder.Scheme = builder.Scheme == "https" ? "wss" : "ws";

            _connectionInfo.Connection.Trace(TraceLevels.Events, "WS Connecting to: {0}", builder.Uri);

            // TODO: Revisit thread safety of this assignment
            _webSocketTokenSource = new CancellationTokenSource();

            CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_webSocketTokenSource.Token, _disconnectToken);
            CancellationToken token = linkedCts.Token;

            _webSocket = new ClientWebSocket(builder.Uri.ToString(), token, null);

            _webSocket.Log = new Logger(LogLevel.Trace, null, ((data, s) => _connectionInfo.Connection.Trace(TraceLevels.All, "WebSocket - {0}", data.Message)));
            _webSocket.OnOpen += (sender, args) => _connectionInfo.Connection.ChangeState(_connectionInfo.Connection.State, ConnectionState.Connected);
            _webSocket.OnClose += (sender, args) => _connectionInfo.Connection.ChangeState(_connectionInfo.Connection.State, ConnectionState.Disconnected);

            _connectionInfo.Connection.PrepareRequest(new WebSocketWrapperRequest(_webSocket, _connectionInfo.Connection));

            _webSocket.ConnectAsync();
            await ProcessWebSocketRequestAsync(_webSocket, token);
        }

        public void Abort(IConnection connection, TimeSpan timeout, string connectionData) {
            _webSocketTokenSource.Cancel();
            _abortHandler.Abort(connection, timeout, connectionData);
        }

        public Task Send(IConnection connection, string data, string connectionData) {
            if (connection == null) {
                throw new ArgumentNullException("connection");
            }

            // If we don't throw here when the WebSocket isn't open, WebSocketHander.SendAsync will noop.
            if (WebSocket.ReadyState != WebSocketState.Open) {
                // Make this a faulted task and trigger the OnError even to maintain consistency with the HttpBasedTransports
                var ex = new InvalidOperationException(Resources.Error_DataCannotBeSentDuringWebSocketReconnect);
                connection.OnError(ex);
                return TaskAsyncHelper.FromError(ex);
            }

            return SendAsync(data);
        }

        public override void OnMessage(string message) {
            _connectionInfo.Connection.Trace(TraceLevels.Messages, "WS: OnMessage({0})", message);

            bool timedOut;
            bool disconnected;
            TransportHelper.ProcessResponse(_connectionInfo.Connection,
                                            message,
                                            out timedOut,
                                            out disconnected,
                                            _initializeHandler.Success);

            if (disconnected && !_disconnectToken.IsCancellationRequested) {
                _connectionInfo.Connection.Trace(TraceLevels.Messages, "Disconnect command received from server.");
                _connectionInfo.Connection.Disconnect();
            }
        }

        public override void OnOpen() {
            // This will noop if we're not in the reconnecting state
            if (_connectionInfo.Connection.ChangeState(ConnectionState.Reconnecting, ConnectionState.Connected)) {
                _connectionInfo.Connection.OnReconnected();
            }
        }

        public override void OnClose() {
            _connectionInfo.Connection.Trace(TraceLevels.Events, "WS: OnClose()");

            if (_disconnectToken.IsCancellationRequested) {
                return;
            }

            if (_abortHandler.TryCompleteAbort()) {
                return;
            }

            DoReconnect();
        }

        private async void DoReconnect() {
            while (TransportHelper.VerifyLastActive(_connectionInfo.Connection) && _connectionInfo.Connection.EnsureReconnecting()) {
                try {
                    await PerformConnect(reconnecting: true);
                    break;
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    if (ExceptionHelper.IsRequestAborted(ex)) {
                        break;
                    }

                    _connectionInfo.Connection.OnError(ex);
                }
                await Task.Factory.StartNew(() => System.Threading.Thread.Sleep(ReconnectDelay));
            }
        }

        public override void OnError() {
            _connectionInfo.Connection.OnError(Error);
        }

        public void LostConnection(IConnection connection) {
            _connectionInfo.Connection.Trace(TraceLevels.Events, "WS: LostConnection");

            if (_webSocketTokenSource != null) {
                _webSocketTokenSource.Cancel();
            }
        }

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            if (disposing) {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) {
                    return;
                }

                if (_webSocketTokenSource != null) {
                    // Gracefully close the websocket message loop
                    _webSocketTokenSource.Cancel();
                }

                _abortHandler.Dispose();

                if (_webSocket != null) {
                    ((IDisposable)_webSocket).Dispose();
                }

                if (_webSocketTokenSource != null) {
                    _webSocketTokenSource.Dispose();
                }
            }
        }

        [SuppressMessage("Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable", Justification = "This class is just a data holder")]
        private class WebSocketConnectionInfo
        {
            public IConnection Connection;
            public string Data;

            public WebSocketConnectionInfo(IConnection connection, string data) {
                Connection = connection;
                Data = data;
            }
        }
    }
}
