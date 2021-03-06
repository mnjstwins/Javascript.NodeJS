﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jering.Javascript.NodeJS.Tests
{
    public class OutOfProcessNodeJSServiceIntegrationTests : IDisposable
    {
        private IServiceProvider _serviceProvider;
        private const int _numThreads = 5; // Arbitrary
        private static readonly CountdownEvent _countdownEvent = new CountdownEvent(_numThreads); // Only used by 1 test

        // TODO are there better techniques for testing multi-threaded code? 
        [Fact]
        public void TryInvokeCoreAsync_HandlesSimultaneousRequestsFromDifferentThreads()
        {
            // Arrange
            DummyNodeJSService testSubject = CreateDummyNodeService();
            var threads = new List<Thread>();

            // Act
            for (int i = 0; i < _numThreads; i++)
            {
                var thread = new Thread(async () => await testSubject.TryInvokeCoreAsync<string>(null, CancellationToken.None).ConfigureAwait(false));

                thread.Start();
                threads.Add(thread);
            }

            _countdownEvent.Wait();

            // Assert
            // If DummyNodeJSService.TryInvokeAsync gets called by each thread, it means the NodeJS process was successfully started by a thread and that the other 
            // threads were successfully resumed once the process was live.
            Assert.Equal(0, _countdownEvent.CurrentCount);
        }

        private class DummyNodeJSService : OutOfProcessNodeJSService
        {
            public DummyNodeJSService(INodeJSProcessFactory nodeProcessFactory,
                ILogger<DummyNodeJSService> nodeServiceLogger,
                IOptions<OutOfProcessNodeJSServiceOptions> optionsAccessor,
                IEmbeddedResourcesService embeddedResourcesService) :
                base(nodeProcessFactory, nodeServiceLogger, optionsAccessor, embeddedResourcesService, typeof(HttpNodeJSService).GetTypeInfo().Assembly, "HttpServer.js") // TODO somehow get HttpServer into test assembly?
            {
            }

            protected override void OnConnectionEstablishedMessageReceived(string connectionEstablishedMessage)
            {
                // Do nothing
            }

            protected override Task<(bool, T)> TryInvokeAsync<T>(InvocationRequest invocationRequest, CancellationToken cancellationToken)
            {
                _countdownEvent.Signal();
                return Task.FromResult((true, default(T)));
            }
        }

        private DummyNodeJSService CreateDummyNodeService()
        {
            IServiceCollection services = new ServiceCollection();
            services.AddNodeJS();
#if NETCOREAPP2_1
            services.AddLogging(lb =>
            {
                lb.AddDebug().SetMinimumLevel(LogLevel.Debug);
            });
#endif

            services.AddSingleton<INodeJSService, DummyNodeJSService>(); // Override default service
            _serviceProvider = services.BuildServiceProvider();

#if NETCOREAPP1_0
            ILoggerFactory loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
            // Setting min level like this doesn't work for netcoreapp2.1
            loggerFactory.AddDebug(LogLevel.Debug);
#endif
            return _serviceProvider.GetRequiredService<INodeJSService>() as DummyNodeJSService;
        }

        public void Dispose()
        {
            ((IDisposable)_serviceProvider).Dispose();
        }
    }
}
