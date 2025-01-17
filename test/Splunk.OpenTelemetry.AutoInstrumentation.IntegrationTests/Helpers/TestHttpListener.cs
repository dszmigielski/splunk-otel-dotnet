// <copyright file="TestHttpListener.cs" company="Splunk Inc.">
// Copyright Splunk Inc.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Abstractions;

namespace Splunk.OpenTelemetry.AutoInstrumentation.IntegrationTests.Helpers;

public class TestHttpListener : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly Action<HttpListenerContext> _requestHandler;
    private readonly HttpListener _listener;
    private readonly string _prefix;

    public TestHttpListener(ITestOutputHelper output, Action<HttpListenerContext> requestHandler, string host, string sufix = "/")
    {
        _output = output;
        _requestHandler = requestHandler;

        // try up to 5 consecutive ports before giving up
        int retries = 4;
        while (true)
        {
            // seems like we can't reuse a listener if it fails to start,
            // so create a new listener each time we retry
            _listener = new HttpListener();

            try
            {
                _listener.Start();

                // See https://docs.microsoft.com/en-us/dotnet/api/system.net.httplistenerprefixcollection.add?redirectedfrom=MSDN&view=net-6.0#remarks
                // for info about the host value.
                Port = TcpPortProvider.GetOpenPort();
                _prefix = new UriBuilder("http", host, Port, sufix).ToString();
                _listener.Prefixes.Add(_prefix);

                // successfully listening
                var listenerThread = new Thread(HandleHttpRequests);
                listenerThread.Start();
                WriteOutput($"Listening on '{_prefix}'");

                return;
            }
            catch (HttpListenerException) when (retries > 0)
            {
                retries--;
            }

            // always close listener if exception is thrown,
            // whether it was caught or not
            _listener.Close();

            WriteOutput("Listener shut down. Could not find available port.");
        }
    }

    /// <summary>
    /// Gets the TCP port that this listener is listening on.
    /// </summary>
    public int Port { get; }

    public Task<bool> VerifyHealthzAsync()
    {
        var healhtzEndpoint = $"{_prefix.Replace("*", "localhost")}/healthz";

        return HealthzHelper.TestHealtzAsync(healhtzEndpoint, nameof(MockTracesCollector), _output);
    }

    public void Dispose()
    {
        WriteOutput("Listener is shutting down.");
        _listener.Stop();
    }

    private void HandleHttpRequests()
    {
        while (_listener.IsListening)
        {
            try
            {
                var ctx = _listener.GetContext();
                var rawUrl = ctx.Request.RawUrl;
                if (rawUrl != null)
                {
                    if (rawUrl.EndsWith("/healthz", StringComparison.OrdinalIgnoreCase))
                    {
                        CreateHealthResponse(ctx);
                        continue;
                    }
                }

                _requestHandler(ctx);
            }
            catch (HttpListenerException)
            {
                // listener was stopped,
                // ignore to let the loop end and the method return
            }
            catch (ObjectDisposedException)
            {
                // the response has been already disposed.
            }
            catch (InvalidOperationException)
            {
                // this can occur when setting Response.ContentLength64, with the framework claiming that the response has already been submitted
                // for now ignore, and we'll see if this introduces downstream issues
            }
            catch (Exception) when (!_listener.IsListening)
            {
                // we don't care about any exception when listener is stopped
            }
            catch (Exception ex)
            {
                // somethig unexpected happened
                // log instead of crashing the thread
                WriteOutput(ex.ToString());
            }
        }
    }

    private void CreateHealthResponse(HttpListenerContext ctx)
    {
        ctx.Response.ContentType = "text/plain";
        var buffer = Encoding.UTF8.GetBytes("OK");
        ctx.Response.ContentLength64 = buffer.LongLength;
        ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
        ctx.Response.StatusCode = (int)HttpStatusCode.OK;
        ctx.Response.Close();
    }

    private void WriteOutput(string msg)
    {
        const string name = nameof(TestHttpListener);
        _output.WriteLine($"[{name}]: {msg}");
    }
}
