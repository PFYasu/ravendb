﻿using System;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow.Json;
using Sparrow.Logging;

namespace Raven.Server.NotificationCenter.Handlers
{
    public class ProxyWebSocketConnection : IDisposable
    {
        private static readonly Logger Logger = LoggingSource.Instance.GetLogger<ProxyWebSocketConnection>(nameof(ProxyWebSocketConnection));

        private readonly CancellationTokenSource _cts;
        private readonly Uri _remoteWebSocketUri;
        private readonly ClientWebSocket _remoteWebSocket;
        private readonly WebSocket _localWebSocket;
        private readonly string _nodeUrl;
        private readonly IMemoryContextPool _contextPool;
        private Task _localToRemote;
        private Task _remoteToLocal;

        public ProxyWebSocketConnection(WebSocket localWebSocket, string nodeUrl, string websocketEndpoint, IMemoryContextPool contextPool, CancellationToken token)
        {
            if (string.IsNullOrEmpty(nodeUrl))
                throw new ArgumentException("Node url cannot be null or empty", nameof(nodeUrl));

            if (string.IsNullOrEmpty(websocketEndpoint))
                throw new ArgumentException("Endpoint cannot be null or empty", nameof(websocketEndpoint));

            if (websocketEndpoint.StartsWith("/") == false)
                throw new ArgumentException("Endpoint must starts with '/' character", nameof(websocketEndpoint));

            _localWebSocket = localWebSocket;
            _nodeUrl = nodeUrl;
            _contextPool = contextPool;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _remoteWebSocketUri = new Uri($"{nodeUrl.Replace("http", "ws", StringComparison.OrdinalIgnoreCase)}{websocketEndpoint}");
            _remoteWebSocket = new ClientWebSocket();
        }

        public Task Establish(X509Certificate2 certificate)
        {
            if (certificate != null)
            {
                var tcpConnection = ReplicationUtils.GetTcpInfo(_nodeUrl, null, $"{nameof(ProxyWebSocketConnection)} to {_nodeUrl}", certificate, _cts.Token);

                var expectedCert = new X509Certificate2(Convert.FromBase64String(tcpConnection.Certificate), (string)null, X509KeyStorageFlags.MachineKeySet);

                _remoteWebSocket.Options.ClientCertificates.Add(certificate);

                _remoteWebSocket.Options.RemoteCertificateValidationCallback += (sender, actualCert, chain, errors) => expectedCert.Equals(actualCert);
            }

            return _remoteWebSocket.ConnectAsync(_remoteWebSocketUri, _cts.Token);
        }

        public async Task RelayData()
        {
            _localToRemote = ForwardLocalToRemote();
            _remoteToLocal = ForwardRemoteToLocal();

            await Task.WhenAny(_localToRemote, _remoteToLocal);
        }

        private async Task ForwardLocalToRemote()
        {
            using (_contextPool.AllocateOperationContext(out JsonOperationContext context))
            using (context.GetMemoryBuffer(out JsonOperationContext.MemoryBuffer segment))
            {
                try
                {
                    while (_localWebSocket.State == WebSocketState.Open || _localWebSocket.State == WebSocketState.CloseSent)
                    {
                        if (_remoteToLocal?.IsCompleted == true)
                            break;

                        var buffer = segment.Memory.Memory;

                        var receiveResult = await _localWebSocket.ReceiveAsync(buffer, _cts.Token);

                        if (receiveResult.MessageType == WebSocketMessageType.Close)
                        {
                            await _localWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "NORMAL_CLOSE", _cts.Token);
                            break;
                        }

                        await _remoteWebSocket.SendAsync(buffer.Slice(0, receiveResult.Count), receiveResult.MessageType, receiveResult.EndOfMessage, _cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // ignored
                }
                catch (IOException ex)
                {
                    if (Logger.IsInfoEnabled)
                        Logger.Info($"Websocket proxy got disconnected (local WS proxy to {_remoteWebSocketUri})", ex);
                }
                catch (Exception ex)
                {
                    // if we received close from the client, we want to ignore it and close the websocket (dispose does it)
                    if (ex is WebSocketException webSocketException
                        && (webSocketException.WebSocketErrorCode == WebSocketError.InvalidState)
                        && (_localWebSocket.State == WebSocketState.Closed || _remoteWebSocket.State == WebSocketState.Closed ||
                            _localWebSocket.State == WebSocketState.CloseReceived || _remoteWebSocket.State == WebSocketState.CloseReceived))
                    {
                        // ignore
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }
        
        private async Task ForwardRemoteToLocal()
        {
            using (_contextPool.AllocateOperationContext(out JsonOperationContext context))
            using (context.GetMemoryBuffer(out JsonOperationContext.MemoryBuffer segment))
            {
                try
                {
                    while (_remoteWebSocket.State == WebSocketState.Open || _remoteWebSocket.State == WebSocketState.CloseSent)
                    {
                        if (_localToRemote?.IsCompleted == true)
                            break;

                        var buffer = segment.Memory.Memory;

                        var receiveResult = await _remoteWebSocket.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);

                        if (receiveResult.MessageType == WebSocketMessageType.Close)
                        {
                            await _remoteWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "NORMAL_CLOSE", _cts.Token);
                            break;
                        }

                        await _localWebSocket.SendAsync(buffer.Slice(0, receiveResult.Count), receiveResult.MessageType, receiveResult.EndOfMessage, _cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // ignored
                }
                catch (IOException ex)
                {
                    if (Logger.IsInfoEnabled)
                        Logger.Info($"Websocket proxy got disconnected ({_remoteWebSocketUri} to local)", ex);
                }
                catch (Exception ex)
                {
                    // if we received close from the client, we want to ignore it and close the websocket (dispose does it)
                    if (ex is WebSocketException webSocketException
                        && (webSocketException.WebSocketErrorCode == WebSocketError.InvalidState)
                        && (_localWebSocket.State == WebSocketState.Closed || _remoteWebSocket.State == WebSocketState.Closed ||
                            _localWebSocket.State == WebSocketState.CloseReceived || _remoteWebSocket.State == WebSocketState.CloseReceived))
                    {
                        // ignore
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }

        public void Dispose()
        {
            _cts.Dispose();
            _remoteWebSocket.Dispose();
        }
    }
}
