#if !(NETSTANDARD2_0 || NETSTANDARD2_1 || NETCOREAPP2_1 || NETCOREAPP3_1)
#define TCP_CLIENT_CANCELLATIONTOKEN_SUPPORT
#endif

#if !(NETSTANDARD2_0 || NETSTANDARD2_1 || NETCOREAPP2_1)
#define SSL_STREAM_CIPHERSUITESPOLICY_SUPPORT
#endif

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.ServerWide.Commands;
using Raven.Client.ServerWide.Tcp;
using Sparrow.Json;
using Sparrow.Logging;

namespace Raven.Client.Util
{
    internal static class TcpUtils
    {
        internal const SslProtocols SupportedSslProtocols =
#if NETSTANDARD2_0 || NETSTANDARD2_1 || NETCOREAPP2_1
            SslProtocols.Tls12;
#else
            SslProtocols.Tls13 | SslProtocols.Tls12;

#endif

        private static void SetTimeouts(TcpClient client, TimeSpan timeout)
        {
            client.SendTimeout = (int)timeout.TotalMilliseconds;
            client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
        }

        internal static async Task<ConnectSecuredTcpSocketResult> ConnectSecuredTcpSocketAsReplication(
            TcpConnectionInfo connection, 
            X509Certificate2 certificate,
#if !(NETSTANDARD2_0 || NETSTANDARD2_1 || NETCOREAPP2_1)
            CipherSuitesPolicy cipherSuitesPolicy,
#endif
            NegotiationCallback negotiationCallback,
            TimeSpan timeout, 
            Logger log
#if TCP_CLIENT_CANCELLATIONTOKEN_SUPPORT
            ,
            CancellationToken token = default
#endif
            )
        {
            try
            {
                return await ConnectSecuredTcpSocket(
                    connection,
                    certificate,
#if SSL_STREAM_CIPHERSUITESPOLICY_SUPPORT
                    cipherSuitesPolicy,
#endif
                    TcpConnectionHeaderMessage.OperationTypes.Replication,
                    negotiationCallback,
                    null,
                    timeout,
                    null
#if TCP_CLIENT_CANCELLATIONTOKEN_SUPPORT
                    ,
                    token
#endif
                ).ConfigureAwait(false);
            }
            catch (AggregateException ae) when (ae.InnerException is SocketException)
            {
                if (log.IsInfoEnabled)
                    log.Info(
                        $"Failed to connect to remote replication destination {connection.Url}. Socket Error Code = {((SocketException)ae.InnerException).SocketErrorCode}",
                        ae.InnerException);
                throw;
            }
            catch (AggregateException ae) when (ae.InnerException is OperationCanceledException)
            {
                if (log.IsInfoEnabled)
                    log.Info(
                        $@"Tried to connect to remote replication destination {connection.Url}, but the operation was aborted.
                            This is not necessarily an issue, it might be that replication destination document has changed at
                            the same time we tried to connect. We will try to reconnect later.",
                        ae.InnerException);
                throw;
            }
            catch (OperationCanceledException e)
            {
                if (log.IsInfoEnabled)
                    log.Info(
                        $@"Tried to connect to remote replication destination {connection.Url}, but the operation was aborted.
                            This is not necessarily an issue, it might be that replication destination document has changed at
                            the same time we tried to connect. We will try to reconnect later.",
                        e);
                throw;
            }
            catch (Exception e)
            {
                if (log.IsInfoEnabled)
                    log.Info($"Failed to connect to remote replication destination {connection.Url}", e);
                throw;
            }
        }

        public static async Task<TcpClient> ConnectAsync(
            string url,
            TimeSpan? timeout = null,
            bool useIPv6 = false
#if TCP_CLIENT_CANCELLATIONTOKEN_SUPPORT
            ,
            CancellationToken token = default
#endif
            )
        {
            var uri = new Uri(url);

            var isIPv6 = uri.HostNameType == UriHostNameType.IPv6;
            var tcpClient = NewTcpClient(timeout, isIPv6);

            try
            {
                if (isIPv6)
                {
                    var ipAddress = IPAddress.Parse(uri.Host);
                    await tcpClient.ConnectAsync(
                        ipAddress,
                        uri.Port
#if TCP_CLIENT_CANCELLATIONTOKEN_SUPPORT
                        ,
                        token
#endif
                        )
                        .ConfigureAwait(false);
                }
                else
                {
                    await tcpClient.ConnectAsync(
                        uri.Host,
                        uri.Port
#if TCP_CLIENT_CANCELLATIONTOKEN_SUPPORT
                        ,
                        token
#endif
                        )
                        .ConfigureAwait(false);
                }
            }
            catch (NotSupportedException)
            {
                tcpClient.Dispose();

                if (useIPv6)
                    throw;

                return await ConnectAsync(
                    url,
                    timeout,
                    useIPv6: true
#if TCP_CLIENT_CANCELLATIONTOKEN_SUPPORT
                    ,
                    token
#endif
                    ).ConfigureAwait(false);
            }
            catch
            {
                tcpClient.Dispose();
                throw;
            }

            return tcpClient;
        }

        internal static async Task<Stream> WrapStreamWithSslAsync(
            TcpClient tcpClient,
            TcpConnectionInfo info,
            X509Certificate2 storeCertificate,
#if SSL_STREAM_CIPHERSUITESPOLICY_SUPPORT
            CipherSuitesPolicy cipherSuitesPolicy,
#endif
            TimeSpan? timeout
#if TCP_CLIENT_CANCELLATIONTOKEN_SUPPORT
            ,
            CancellationToken token = default
#endif
            )
        {
            var networkStream = tcpClient.GetStream();
            if (timeout != null)
            {
                networkStream.ReadTimeout =
                    networkStream.WriteTimeout = (int)timeout.Value.TotalMilliseconds;
            }

            if (info.Certificate == null)
                return networkStream;

            var expectedCert = new X509Certificate2(Convert.FromBase64String(info.Certificate), (string)null, X509KeyStorageFlags.MachineKeySet);
            var sslStream = new SslStream(networkStream, false, (sender, actualCert, chain, errors) => expectedCert.Equals(actualCert));

            var targetHost = new Uri(info.Url).Host;
            var clientCertificates = new X509CertificateCollection(new X509Certificate[] { storeCertificate });

#if SSL_STREAM_CIPHERSUITESPOLICY_SUPPORT
            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                ClientCertificates = clientCertificates,
                EnabledSslProtocols = SupportedSslProtocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                CipherSuitesPolicy = cipherSuitesPolicy
            }
#if TCP_CLIENT_CANCELLATIONTOKEN_SUPPORT
                ,
                token
#endif
            ).ConfigureAwait(false);
#else
            await sslStream.AuthenticateAsClientAsync(targetHost, clientCertificates, SupportedSslProtocols, checkCertificateRevocation: false).ConfigureAwait(false);
#endif

            return sslStream;
        }

        private static TcpClient NewTcpClient(TimeSpan? timeout, bool useIPv6)
        {
            // We start with a IPv4 TcpClient and we fallback to use IPv6 TcpClient only if we fail.
            // This is because that dual mode of IPv6 has a timeout of 1 second
            // which is bigger than the election time in the cluster which is 300ms.
            TcpClient tcpClient;
            if (useIPv6)
            {
                tcpClient = new TcpClient(AddressFamily.InterNetworkV6);
                tcpClient.Client.DualMode = true;
            }
            else
            {
                tcpClient = new TcpClient(AddressFamily.InterNetwork);
            }

            tcpClient.NoDelay = true;
            tcpClient.LingerState = new LingerOption(true, 5);

            if (timeout.HasValue)
                SetTimeouts(tcpClient, timeout.Value);

            Debug.Assert(tcpClient.Client != null);
            return tcpClient;
        }

        internal struct ConnectSecuredTcpSocketResult
        {
            public string Url;
            public TcpClient TcpClient;
            public Stream Stream;
            public TcpConnectionHeaderMessage.SupportedFeatures SupportedFeatures;

            public ConnectSecuredTcpSocketResult(string url, TcpClient tcpClient, Stream stream, TcpConnectionHeaderMessage.SupportedFeatures supportedFeatures)
            {
                Url = url;
                TcpClient = tcpClient;
                Stream = stream;
                SupportedFeatures = supportedFeatures;
            }
        }

        public delegate Task<TcpConnectionHeaderMessage.SupportedFeatures> NegotiationCallback(string url, TcpConnectionInfo tcpInfo, Stream stream, JsonOperationContext context, List<string> logs = null);

        internal static async Task<ConnectSecuredTcpSocketResult> ConnectSecuredTcpSocket(
            TcpConnectionInfo info, 
            X509Certificate2 cert,
#if !(NETSTANDARD2_0 || NETSTANDARD2_1 || NETCOREAPP2_1)
            CipherSuitesPolicy cipherSuitesPolicy,
#endif  
            TcpConnectionHeaderMessage.OperationTypes operationType,
            NegotiationCallback negotiationCallback,
            JsonOperationContext negContext,
            TimeSpan? timeout,
            List<string> logs = null
#if TCP_CLIENT_CANCELLATIONTOKEN_SUPPORT
            ,
            CancellationToken token = default
#endif
            )
        {
            TcpClient tcpClient = null;
            Stream stream = null;
            TcpConnectionHeaderMessage.SupportedFeatures supportedFeatures = null;

            string[] infoUrls = info.Urls == null ? new string[] {info.Url} : info.Urls.Append(info.Url).ToArray();
            
            logs?.Add($"Received tcpInfo: {Environment.NewLine}urls: { string.Join(",", infoUrls)} {Environment.NewLine}guid:{ info.ServerId}");

            var exceptions = new List<Exception>();
            for (int i=0; i < infoUrls.Length; i++)
            {
                string url = infoUrls[i];
                try
                {
                    tcpClient?.Dispose();
                    stream?.Dispose();
                    
                    logs?.Add($"Trying to connect to :{url}");

                    tcpClient = await ConnectAsync(
                        url, 
                        timeout
#if TCP_CLIENT_CANCELLATIONTOKEN_SUPPORT
                        ,
                        token: token
#endif
                        ).ConfigureAwait(false);
                    
                    stream = await TcpUtils.WrapStreamWithSslAsync(
                        tcpClient,
                        info,
                        cert,
#if SSL_STREAM_CIPHERSUITESPOLICY_SUPPORT
                        cipherSuitesPolicy,
#endif
                        timeout
#if TCP_CLIENT_CANCELLATIONTOKEN_SUPPORT
                        ,
                        token: token
#endif
                    ).ConfigureAwait(false);

                    switch (operationType)
                    {
                        case TcpConnectionHeaderMessage.OperationTypes.Subscription:
                        case TcpConnectionHeaderMessage.OperationTypes.Replication:
                        case TcpConnectionHeaderMessage.OperationTypes.Heartbeats:
                        case TcpConnectionHeaderMessage.OperationTypes.Cluster:
                            supportedFeatures = await negotiationCallback(url, info, stream, negContext).ConfigureAwait(false);
                            break;
                        case TcpConnectionHeaderMessage.OperationTypes.TestConnection:
                        case TcpConnectionHeaderMessage.OperationTypes.Ping:
                            supportedFeatures = await negotiationCallback(url, info, stream, negContext, logs).ConfigureAwait(false);
                            break;
                        default:
                            throw new NotSupportedException($"Operation type '{operationType}' not supported.");
                    }

                    logs?.Add($"{Environment.NewLine}Negotiation successful for operation {operationType}.{Environment.NewLine} {tcpClient.Client.LocalEndPoint} "+
                              $"is connected to {tcpClient.Client.RemoteEndPoint}{Environment.NewLine}");

                    return new ConnectSecuredTcpSocketResult(url, tcpClient, stream, supportedFeatures);
                }
                catch (Exception e)
                {
                    logs?.Add($"Failed to connect to url {url}: {e.Message}");
                    exceptions.Add(e);
                    if (i == infoUrls.Length - 1)
                    {
                        throw new AggregateException($"Failed to connect to url {url}", exceptions);
                    }
                }
            }
            //Should not reach here
            Debug.Assert(false, "Shouldn't have reached here. This is likely a bug.");
            throw new InvalidOperationException();
        }
    }
}
