using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jering.Javascript.NodeJS
{
    /// <summary>
    /// <para>An abstract <see cref="INodeJSService"/> implementation that facilitates working with an out of process NodeJS instance.</para>
    /// <para>The primary responsibilities of this class are launching and maintaining a NodeJS process.
    /// This class uses the stdout stream of the child process to perform a simple handshake with the NodeJS process. This is agnostic to the mechanism that
    /// derived classes use to actually perform the invocations (e.g., they could use HTTP-RPC, or a binary TCP
    /// protocol, or any other RPC-type mechanism).</para>
    /// </summary>
    /// <seealso cref="INodeJSService" />
    public abstract class OutOfProcessNodeJSService : INodeJSService
    {
        /// <summary>
        /// Start of the message used to perform a handshake with the NodeJS process.
        /// </summary>
        protected const string CONNECTION_ESTABLISHED_MESSAGE_START = "[Jering.Javascript.NodeJS: Listening on ";

        /// <summary>
        /// The logger for the NodeJS process's stdout and stderr streams as well as messages from <see cref="OutOfProcessNodeJSService"/> and its implementations.
        /// Can be null, in which case, no logging will occur.
        /// </summary>
        protected readonly ILogger Logger;

        private readonly IEmbeddedResourcesService _embeddedResourcesService;
        private readonly INodeJSProcessFactory _nodeProcessFactory;
        private readonly string _serverScriptName;
        private readonly Assembly _serverScriptAssembly;
        private readonly OutOfProcessNodeJSServiceOptions _options;
        private readonly SemaphoreSlim _processSemaphore = new SemaphoreSlim(1, 1);

        private Process _nodeProcess;
        private bool _connected;
        private bool _disposed;

        /// <summary>
        /// Creates an<see cref="OutOfProcessNodeJSService"/> instance.
        /// </summary>
        /// <param name="nodeProcessFactory"></param>
        /// <param name="logger"></param>
        /// <param name="optionsAccessor"></param>
        /// <param name="embeddedResourcesService"></param>
        /// <param name="serverScriptAssembly"></param>
        /// <param name="serverScriptName"></param>
        protected OutOfProcessNodeJSService(INodeJSProcessFactory nodeProcessFactory,
            ILogger logger,
            IOptions<OutOfProcessNodeJSServiceOptions> optionsAccessor,
            IEmbeddedResourcesService embeddedResourcesService,
            Assembly serverScriptAssembly,
            string serverScriptName)
        {
            _nodeProcessFactory = nodeProcessFactory;
            Logger = logger;
            _options = optionsAccessor?.Value ?? new OutOfProcessNodeJSServiceOptions();
            _embeddedResourcesService = embeddedResourcesService;
            _serverScriptName = serverScriptName;
            _serverScriptAssembly = serverScriptAssembly;
        }

        /// <summary>
        /// Asynchronously invokes code in the NodeJS instance.
        /// </summary>
        /// <typeparam name="T">The type of the object this method will return. It can be a JSON-serializable type, <see cref="string"/>, or <see cref="Stream"/>.</typeparam>
        /// <param name="invocationRequest">The invocation request to send to the NodeJS process.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel the invocation.</param>
        /// <returns>The task object representing the asynchronous operation.</returns>
        protected abstract Task<(bool, T)> TryInvokeAsync<T>(InvocationRequest invocationRequest, CancellationToken cancellationToken);

        /// <summary>
        /// <para>This method is called when the connection established message from the NodeJS process is received.</para>
        /// <para>The message can be used to complete the handshake with the
        /// NodeJS process, for example by delivering a port and an IP address to use in further communications.</para>
        /// </summary>
        /// <param name="connectionEstablishedMessage">The connection established message.</param>
        protected abstract void OnConnectionEstablishedMessageReceived(string connectionEstablishedMessage);

        /// <inheritdoc />
        public async Task<T> InvokeFromFileAsync<T>(string modulePath, string exportName = null, object[] args = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            var invocationRequest = new InvocationRequest(ModuleSourceType.File,
                    modulePath,
                    exportName: exportName,
                    args: args);

            return (await TryInvokeCoreAsync<T>(invocationRequest, cancellationToken).ConfigureAwait(false)).Item2;
        }

        /// <inheritdoc />
        public async Task<T> InvokeFromStringAsync<T>(string moduleString, string newCacheIdentifier = null, string exportName = null, object[] args = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            var invocationRequest = new InvocationRequest(ModuleSourceType.String,
                    moduleString,
                    newCacheIdentifier,
                    exportName,
                    args);

            return (await TryInvokeCoreAsync<T>(invocationRequest, cancellationToken).ConfigureAwait(false)).Item2;
        }

        /// <inheritdoc />
        public async Task<T> InvokeFromStreamAsync<T>(Stream moduleStream, string newCacheIdentifier = null, string exportName = null, object[] args = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            var invocationRequest = new InvocationRequest(ModuleSourceType.Stream,
                    newCacheIdentifier: newCacheIdentifier,
                    exportName: exportName,
                    args: args,
                    moduleStreamSource: moduleStream);

            return (await TryInvokeCoreAsync<T>(invocationRequest, cancellationToken).ConfigureAwait(false)).Item2;
        }

        /// <inheritdoc />
        public Task<(bool, T)> TryInvokeFromCacheAsync<T>(string moduleCacheIdentifier, string exportName = null, object[] args = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            var invocationRequest = new InvocationRequest(ModuleSourceType.Cache,
                    moduleCacheIdentifier,
                    exportName: exportName,
                    args: args);

            return TryInvokeCoreAsync<T>(invocationRequest, cancellationToken);
        }

        internal async Task<(bool, T)> TryInvokeCoreAsync<T>(InvocationRequest invocationRequest, CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(OutOfProcessNodeJSService));
            }

            // Disposables
            CancellationTokenSource timeoutCTS = null;
            CancellationTokenSource combinedCTS = null;

            try
            {
                // Create combined CancellationToken only as required.
                if (_options.TimeoutMS > 0)
                {
                    timeoutCTS = new CancellationTokenSource(_options.TimeoutMS);

                    if (cancellationToken != CancellationToken.None)
                    {
                        combinedCTS = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCTS.Token);
                        cancellationToken = combinedCTS.Token;
                    }
                    else
                    {
                        cancellationToken = timeoutCTS.Token;
                    }
                }

                // Once ConnectToInputOutStreams is called, _nodeProcess will not be null and _nodeProcess.HasExited will be false.
                // Therefore, there is no risk of _connected being set to false while waiting for or after receiving the connection established
                // message.
                if (_nodeProcess?.HasExited != false)
                {
                    _connected = false;
                }

                // If the the NodeJS process has not been started has has been terminated for some reason, attempt to create a 
                // new process. Apart from the thread creating the process, all other threads will be blocked. If the new process 
                // is created successfully, all threads will be released by the OutputDataReceived delegate in 
                // ConnectToInputOutputStreams.
                if (!_connected)
                {
                    if (Logger?.IsEnabled(LogLevel.Debug) == true)
                    {
                        Logger.LogDebug($"Before first semaphore wait, count: {_processSemaphore.CurrentCount}. Thread ID: {Thread.CurrentThread.ManagedThreadId.ToString()}");
                    }

                    await _processSemaphore.WaitAsync().ConfigureAwait(false);

                    // Double checked lock
                    if (!_connected)
                    {
                        // Even though process has exited, dispose instance to release handle - https://docs.microsoft.com/en-sg/dotnet/api/system.diagnostics.process?view=netframework-4.7.1
                        _nodeProcess?.Dispose();
                        string serverScript = _embeddedResourcesService.ReadAsString(_serverScriptAssembly, _serverScriptName);
                        _nodeProcess = _nodeProcessFactory.Create(serverScript);
                        ConnectToInputOutputStreams(_nodeProcess);

                        if (Logger?.IsEnabled(LogLevel.Debug) == true)
                        {
                            Logger.LogDebug($"Before second semaphore wait, count: {_processSemaphore.CurrentCount}. Thread ID: {Thread.CurrentThread.ManagedThreadId.ToString()}");
                        }

                        await _processSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                return await TryInvokeAsync<T>(invocationRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // TODO do CancellationTokenSources use unmanaged resources in this scenario? If not, no need to dispose of them here.
                // At present, if a propogated exception is not caught by an enclosing try-catch block, finally might never be called.
                // - https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/try-finally
                timeoutCTS?.Dispose();
                combinedCTS?.Dispose();

                if (!(exception is OperationCanceledException))
                {
                    throw;
                }

                if (!_connected)
                {
                    // This is very unlikely
                    throw new InvocationException(
                        $"Attempt to connect to Node timed out after {_options.TimeoutMS}ms.",
                        string.Empty);
                }

                // Developers encounter this fairly often (if their Node code fails without invoking the callback,
                // all that the .NET side knows is that the invocation eventually times out). Previously, this surfaced
                // as a TaskCanceledException, but this led to a lot of issue reports. Now we throw the following
                // descriptive error.
                throw new InvocationException(
                    $"The Node invocation timed out after {_options.TimeoutMS}ms.",
                    $"You can change the timeout duration by setting the {nameof(OutOfProcessNodeJSServiceOptions.TimeoutMS)} "
                    + $"property on {nameof(OutOfProcessNodeJSServiceOptions)}.\n\n"
                    + "The first debugging step is to ensure that your NodeJS function always invokes the supplied "
                    + "callback (or throws an exception synchronously), even if it encounters an error. Otherwise, "
                    + "the .NET code has no way to know that it is finished or has failed."
                );
            }
            finally
            {
                timeoutCTS?.Dispose();
                combinedCTS?.Dispose();
            }
        }

        private void ConnectToInputOutputStreams(Process nodeProcess)
        {
            var outputStringBuilder = new StringBuilder();
            var errorStringBuilder = new StringBuilder();

            nodeProcess.OutputDataReceived += (sender, evt) =>
            {
                if (evt.Data == null)
                {
                    return;
                }

                if (!_connected && evt.Data.StartsWith(CONNECTION_ESTABLISHED_MESSAGE_START))
                {
                    OnConnectionEstablishedMessageReceived(evt.Data);
                    _connected = true;

                    // Release all threads by resetting CurrentCount to 1
                    while (_processSemaphore.CurrentCount < 1)
                    {
                        if (Logger?.IsEnabled(LogLevel.Debug) == true)
                        {
                            Logger.LogDebug($"Releasing semaphore, count: {_processSemaphore.CurrentCount}. Thread ID: {Thread.CurrentThread.ManagedThreadId.ToString()}");
                        }

                        _processSemaphore.Release();
                    }
                }
                else if (Logger != null && TryCreateMessage(outputStringBuilder, evt.Data, out string message))
                {
                    // Process output is received line by line. The last line of a message ends with a \0 (null character),
                    // so we accumulate lines in a StringBuilder till the \0, then log the entire message in one go.
                    Logger.LogInformation(message);
                }
            };

            nodeProcess.ErrorDataReceived += (sender, evt) =>
            {
                if (evt.Data == null)
                {
                    return;
                }

                if (Logger != null && TryCreateMessage(errorStringBuilder, evt.Data, out string message))
                {
                    Logger.LogError(message);
                }
            };

            nodeProcess.BeginOutputReadLine();
            nodeProcess.BeginErrorReadLine();
        }

        private bool TryCreateMessage(StringBuilder stringBuilder, string newLine, out string message)
        {
            // OutputDataReceived is called everytime a newline character is read. The event data supplied to the callback
            // is a string containing all the characters between the previous newline character and the most recent one.
            // In other words, the stream is read line by line. The last line in each message ends with a null terminating 
            // character (see HttpServer).
            if (newLine[newLine.Length - 1] != '\0')
            {
                stringBuilder.AppendLine(newLine);
                message = null;
                return false;
            }

            stringBuilder.Append(newLine);
            stringBuilder.Length--; // Remove null terminating character
            message = stringBuilder.ToString();
            stringBuilder.Length = 0;

            return true;
        }

        /// <summary>
        /// Disposes this instance.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes the instance.
        /// </summary>
        /// <param name="disposing">True if the object is disposing or false if it is finalizing.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                _processSemaphore.Dispose();
            }

            // Ensure that node process gets killed
            if (_nodeProcess?.HasExited == false)
            {
                _nodeProcess.Kill();
                // Give async output some time to push its messages
                // TODO this can throw, is it safe to call in the finalizer?
                _nodeProcess.WaitForExit(500);
                _nodeProcess.Dispose();
            }

            _disposed = true;
        }

        /// <summary>
        /// Implements the finalization part of the IDisposable pattern by calling Dispose(false).
        /// </summary>
        ~OutOfProcessNodeJSService()
        {
            Dispose(false);
        }
    }
}