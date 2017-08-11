// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Tests.Common;
using Microsoft.AspNetCore.Sockets;
using Microsoft.AspNetCore.Sockets.Features;
using Microsoft.AspNetCore.Sockets.Client;
using Microsoft.AspNetCore.Testing.xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.AspNetCore.SignalR.Tests
{
    [CollectionDefinition(Name)]
    public class EndToEndTestsCollection : ICollectionFixture<ServerFixture<Startup>>
    {
        public const string Name = "EndToEndTests";
    }

    [Collection(EndToEndTestsCollection.Name)]
    public class EndToEndTests : LoggedTest
    {
        private readonly ServerFixture<Startup> _serverFixture;

        public EndToEndTests(ServerFixture<Startup> serverFixture, ITestOutputHelper output) : base(output)
        {
            if (serverFixture == null)
            {
                throw new ArgumentNullException(nameof(serverFixture));
            }

            _serverFixture = serverFixture;
        }

        [Fact]
        public async Task CanStartAndStopConnectionUsingDefaultTransport()
        {
            var url = _serverFixture.BaseUrl + "/echo";
            // The test should connect to the server using WebSockets transport on Windows 8 and newer.
            // On Windows 7/2008R2 it should use ServerSentEvents transport to connect to the server.
            var connection = new HttpConnection(new Uri(url));
            await connection.StartAsync().OrTimeout();
            await connection.DisposeAsync().OrTimeout();
        }

        [Theory]
        [MemberData(nameof(TransportTypes))]
        public async Task CanStartAndStopConnectionUsingGivenTransport(TransportType transportType)
        {
            var url = _serverFixture.BaseUrl + "/echo";
            // When WebSockets is attempted to be used on Windows 7/2008R2 it will instead use ServerSentEvents
            var connection = new HttpConnection(new Uri(url), transportType);
            await connection.StartAsync().OrTimeout();
            await connection.DisposeAsync().OrTimeout();
        }

        [ConditionalFact]
        [OSSkipCondition(OperatingSystems.Windows, WindowsVersions.Win7, WindowsVersions.Win2008R2, SkipReason = "No WebSockets Client for this platform")]
        public async Task WebSocketsTest()
        {
            using (StartLog(out var loggerFactory))
            {
                var logger = loggerFactory.CreateLogger<EndToEndTests>();

                const string message = "Hello, World!";
                using (var ws = new ClientWebSocket())
                {
                    var socketUrl = _serverFixture.WebSocketsUrl + "/echo";

                    logger.LogInformation("Connecting WebSocket to {socketUrl}", socketUrl);
                    await ws.ConnectAsync(new Uri(socketUrl), CancellationToken.None).OrTimeout();

                    var bytes = Encoding.UTF8.GetBytes(message);
                    logger.LogInformation("Sending {length} byte frame", bytes.Length);
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, true, CancellationToken.None).OrTimeout();

                    logger.LogInformation("Receiving frame");
                    var buffer = new ArraySegment<byte>(new byte[1024]);
                    var result = await ws.ReceiveAsync(buffer, CancellationToken.None).OrTimeout();
                    logger.LogInformation("Received {length} byte frame", result.Count);

                    Assert.Equal(bytes, buffer.Array.AsSpan().Slice(0, result.Count).ToArray());

                    logger.LogInformation("Closing socket");
                    await ws.CloseAsync(WebSocketCloseStatus.Empty, "", CancellationToken.None).OrTimeout();
                    logger.LogInformation("Closed socket");
                }
            }
        }

        [ConditionalTheory]
        [OSSkipCondition(OperatingSystems.Windows, WindowsVersions.Win7, WindowsVersions.Win2008R2, SkipReason = "No WebSockets Client for this platform")]
        [MemberData(nameof(TransportTypesXTransferModes))]
        public async Task ConnectionCanSendAndReceiveMessages(TransportType transportType, TransferMode requestedTransferMode)
        {
            using (StartLog(out var loggerFactory, testName: $"ConnectionCanSendAndReceiveMessages_{transportType.ToString()}"))
            {
                var logger = loggerFactory.CreateLogger<EndToEndTests>();

                const string message = "Major Key";

                var url = _serverFixture.BaseUrl + "/echo";
                var connection = new HttpConnection(new Uri(url), transportType, loggerFactory);

                connection.Features.Set<ITransferModeFeature>(
                    new TransferModeFeature { TransferMode = requestedTransferMode });
                try
                {
                    var receiveTcs = new TaskCompletionSource<string>();
                    var closeTcs = new TaskCompletionSource<object>();
                    connection.Received += data =>
                    {
                        logger.LogInformation("Received {length} byte message", data.Length);

                        if (Base64Encoded(requestedTransferMode, connection))
                        {
                            data = Convert.FromBase64String(Encoding.UTF8.GetString(data));
                        }

                        receiveTcs.TrySetResult(Encoding.UTF8.GetString(data));
                        return Task.CompletedTask;
                    };
                    connection.Closed += e =>
                    {
                        logger.LogInformation("Connection closed");
                        if (e != null)
                        {
                            receiveTcs.TrySetException(e);
                            closeTcs.TrySetException(e);
                        }
                        else
                        {
                            receiveTcs.TrySetResult(null);
                            closeTcs.TrySetResult(null);
                        }
                        return Task.CompletedTask;
                    };

                    logger.LogInformation("Starting connection to {url}", url);
                    await connection.StartAsync().OrTimeout();
                    logger.LogInformation("Started connection to {url}", url);

                    var bytes = Encoding.UTF8.GetBytes(message);

                    // Need to encode binary payloads sent over text transports
                    if (Base64Encoded(requestedTransferMode, connection))
                    {
                        bytes = Encoding.UTF8.GetBytes(Convert.ToBase64String(bytes));
                    }

                    logger.LogInformation("Sending {length} byte message", bytes.Length);
                    await connection.SendAsync(bytes).OrTimeout();
                    logger.LogInformation("Sent message", bytes.Length);

                    logger.LogInformation("Receiving message");
                    Assert.Equal(message, await receiveTcs.Task.OrTimeout());
                    logger.LogInformation("Completed receive");

                    await closeTcs.Task.OrTimeout();
                }
                catch (Exception ex)
                {
                    logger.LogInformation(ex, "Test threw exception");
                    throw;
                }
                finally
                {
                    logger.LogInformation("Disposing Connection");
                    await connection.DisposeAsync().OrTimeout();
                    logger.LogInformation("Disposed Connection");
                }
            }

            bool Base64Encoded(TransferMode transferMode, IConnection connection)
            {
                return requestedTransferMode == TransferMode.Binary &&
                    connection.Features.Get<ITransferModeFeature>().TransferMode == TransferMode.Text;
            }
        }

        public static IEnumerable<object[]> MessageSizesData
        {
            get
            {
                yield return new object[] { new string('A', 5 * 4096) };
                yield return new object[] { new string('A', 1000 * 4096 + 32) };
            }
        }

        [ConditionalTheory]
        [OSSkipCondition(OperatingSystems.Windows, WindowsVersions.Win7, WindowsVersions.Win2008R2, SkipReason = "No WebSockets Client for this platform")]
        [MemberData(nameof(MessageSizesData))]
        public async Task ConnectionCanSendAndReceiveDifferentMessageSizesWebSocketsTransport(string message)
        {
            using (StartLog(out var loggerFactory, testName: $"ConnectionCanSendAndReceiveDifferentMessageSizesWebSocketsTransport_{message.Length}"))
            {
                var logger = loggerFactory.CreateLogger<EndToEndTests>();

                var url = _serverFixture.BaseUrl + "/echo";
                var connection = new HttpConnection(new Uri(url), loggerFactory);
                connection.Features.Set<ITransferModeFeature>(
                    new TransferModeFeature { TransferMode = TransferMode.Binary });

                try
                {
                    var receiveTcs = new TaskCompletionSource<byte[]>();
                    connection.Received += data =>
                    {
                        logger.LogInformation("Received {length} byte message", data.Length);
                        receiveTcs.TrySetResult(data);
                        return Task.CompletedTask;
                    };

                    logger.LogInformation("Starting connection to {url}", url);
                    await connection.StartAsync().OrTimeout();
                    logger.LogInformation("Started connection to {url}", url);

                    var bytes = Encoding.UTF8.GetBytes(message);
                    logger.LogInformation("Sending {length} byte message", bytes.Length);
                    await connection.SendAsync(bytes).OrTimeout();
                    logger.LogInformation("Sent message", bytes.Length);

                    logger.LogInformation("Receiving message");
                    var receivedData = await receiveTcs.Task.OrTimeout();
                    Assert.Equal(message, Encoding.UTF8.GetString(receivedData));
                    logger.LogInformation("Completed receive");
                }
                catch (Exception ex)
                {
                    logger.LogInformation(ex, "Test threw exception");
                    throw;
                }
                finally
                {
                    logger.LogInformation("Disposing Connection");
                    await connection.DisposeAsync().OrTimeout();
                    logger.LogInformation("Disposed Connection");
                }
            }
        }

        [ConditionalFact]
        [OSSkipCondition(OperatingSystems.Windows, WindowsVersions.Win7, WindowsVersions.Win2008R2, SkipReason = "No WebSockets Client for this platform")]
        public async Task ServerClosesConnectionWithErrorIfHubCannotBeCreated_WebSocket()
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await ServerClosesConnectionWithErrorIfHubCannotBeCreated(TransportType.WebSockets));
            Assert.Equal("Websocket closed with error: InternalServerError.", exception.Message);
        }

        [Fact]
        public async Task ServerClosesConnectionWithErrorIfHubCannotBeCreated_LongPolling()
        {
            var exception = await Assert.ThrowsAsync<HttpRequestException>(
                async () => await ServerClosesConnectionWithErrorIfHubCannotBeCreated(TransportType.LongPolling));
            Assert.Equal("Response status code does not indicate success: 500 (Internal Server Error).", exception.Message);
        }

        private async Task ServerClosesConnectionWithErrorIfHubCannotBeCreated(TransportType transportType)
        {
            using (StartLog(out var loggerFactory, testName: $"ConnectionCanSendAndReceiveMessages_{transportType.ToString()}"))
            {
                var logger = loggerFactory.CreateLogger<EndToEndTests>();

                var url = _serverFixture.BaseUrl + "/uncreatable";

                var connection = new HubConnection(new HttpConnection(new Uri(url), transportType, loggerFactory), loggerFactory);
                try
                {
                    var closeTcs = new TaskCompletionSource<object>();

                    connection.Closed += e =>
                    {
                        logger.LogInformation("Connection closed");
                        if (e != null)
                        {
                            closeTcs.TrySetException(e);
                        }
                        else
                        {
                            closeTcs.TrySetResult(null);
                        }
                        return Task.CompletedTask;
                    };

                    logger.LogInformation("Starting connection to {url}", url);
                    await connection.StartAsync().OrTimeout();

                    await closeTcs.Task.OrTimeout();
                }
                catch (Exception ex)
                {
                    logger.LogInformation(ex, "Test threw exception");
                    throw;
                }
                finally
                {
                    logger.LogInformation("Disposing Connection");
                    await connection.DisposeAsync().OrTimeout();
                    logger.LogInformation("Disposed Connection");
                }
            }
        }

        public static IEnumerable<object[]> TransportTypes =>
            new[]
            {
                new object[] { TransportType.WebSockets },
                new object[] { TransportType.ServerSentEvents },
                new object[] { TransportType.LongPolling }
            };

        public static IEnumerable<object[]> TransportTypesXTransferModes()
        {
            foreach (var transport in TransportTypes)
            {
                yield return new object[] { transport[0], TransferMode.Text };
                yield return new object[] { transport[0], TransferMode.Binary };
            }
        }
    }
}
