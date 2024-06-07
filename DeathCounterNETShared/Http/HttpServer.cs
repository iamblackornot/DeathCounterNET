using Nito.AsyncEx;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Net.Sockets;

namespace DeathCounterNETShared
{
    using EndpointCallback = Action<Request, Response>;
    using EndpointAsyncCallback = Func<Request, Response, Task>;
    internal class HttpServer : Notifiable
    {
        private EndpointManager _endpointManager;
        private HttpListener _listener;
        private volatile bool _toStop = false;

        private Response? _not_processed_response;

        private Executor _executor;
        private Task? _loopTask;
        private CancellationTokenSource? _cts;

        static readonly int SERVER_STOP_WAIT_TIMEOUT = 10 * 1000;

        static readonly int ERROR_SHARING_VIOLATION = 32;
        static readonly int WRONG_NETWORK_ADDRESS = 1214;

        public EndpointIPAddress Endpoint { get; private set; }

        public HttpServer(EndpointIPAddress endpoint)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://{endpoint.IP}:{endpoint.Port}/");

            string hostName = Dns.GetHostName();
            IPHostEntry ipHostEntry = Dns.GetHostEntry(hostName, AddressFamily.InterNetwork);

            foreach (IPAddress ip in ipHostEntry.AddressList)
            {
                if (endpoint.IP == ip.ToString()) continue;

                _listener.Prefixes.Add($"http://{ip}:{endpoint.Port}/");
            }


            Endpoint = endpoint;

            _endpointManager = new();
            _endpointManager.NotifyError += _endpointManager_NotifyError;

            _executor = Executor
                .GetBuilder()
                .SetDefaultExceptionHandler(DefaultExceptionHandler)
                .SetCustomExceptionHandler<HttpListenerException>(HttpListenerExceptionHandler)
                .SetCustomExceptionHandler<TaskCanceledException>(TaskCanceledExceptionHandler)
                .Build();
        }

        public void AddEndpoint(HttpMethod method, string endpoint, EndpointCallback callback)
        {
            _endpointManager.Add(method, endpoint, callback);
        }
        public void AddEndpoint(HttpMethod method, string endpoint, EndpointAsyncCallback callback)
        {
            _endpointManager.Add(method, endpoint, callback);
        }

        public Result Start()
        {
            Func<Result<Nothing>> startAction = () =>
            {
                _listener.Start();
                _toStop = false;
                _cts = new CancellationTokenSource();

                _loopTask = Task.Run(ListenLoop);

                return new GoodResult();
            };

            return _executor.Execute(startAction);
        }
        public void Stop()
        {
            if (_toStop || _loopTask is null || _loopTask.IsCompleted || _cts is null) return;

            _listener.Stop();
            _toStop = true;
            _cts.Cancel();

            _loopTask.Wait(SERVER_STOP_WAIT_TIMEOUT);
            _cts = null;
        }
        public async Task Join()
        {
            if (_loopTask is null) return;

            await _loopTask;
        }
        private async Task ListenLoop()
        {
            while (!_toStop)
            {
                await _executor.ExecuteAsync(Listen);
            }
        }
        private async Task Listen()
        {
            HttpListenerContext ctx = await _listener.GetContextAsync().WaitAsync(_cts!.Token);
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse resp = ctx.Response;

            Response response = new Response(resp);
            _not_processed_response = response;

            Regex regex = new Regex("(\\/[a-z-_]*(\\/[a-z-_]+)*)", RegexOptions.IgnoreCase);
            var matches = regex.Matches(req.RawUrl ?? string.Empty);

            if (matches.Count == 0)
            {
                resp.StatusCode = (int)HttpStatusCode.BadRequest;
                resp.Close();
                return;
            }

            string endpoint = matches[0].Value;

            byte[] buffer = new byte[req.ContentLength64];
            int readCount = req.InputStream.Read(buffer, 0, buffer.Length);
            string body = Encoding.UTF8.GetString(buffer);

            Request request = Request
                .GetBuilder()
                .SetMethod(new HttpMethod(req.HttpMethod))
                .SetEndpoint(endpoint)
                .SetBody(body)
                .SetContentType(req.ContentType)
                .SetQueryString(req.QueryString)
                .Build();

            await _endpointManager.ProcessRequest(request, response);

            _not_processed_response = null;
        }
        private Result HttpListenerExceptionHandler(Exception ex)
        {
            if (ex is HttpListenerException hlex)
            {
                if (hlex.ErrorCode == ERROR_SHARING_VIOLATION)
                {
                    return new BadResult("port is already used");
                }
                if (hlex.ErrorCode == WRONG_NETWORK_ADDRESS)
                {
                    return new BadResult("wrong ip address");
                }
            }

            return DefaultExceptionHandler(ex);
        }
        private void SendInternalServerErrorIfNeeded()
        {
            try
            {
                if (_not_processed_response is not null && !_not_processed_response.WasSent)
                {
                    _not_processed_response.Send(HttpStatusCode.InternalServerError, "server listen loop threw an exception");
                }
            }
            catch (Exception ex)
            {
                Logger.AddToLogs(ex.ToString());
            }
            finally
            {
                _not_processed_response = null;
            }
        }
        private void _endpointManager_NotifyError(object? sender, NotifyArgs e)
        {
            OnNotifyError($"[HttpServer] {e.Message}");
        }
        private Result DefaultExceptionHandler(Exception ex)
        {
            SendInternalServerErrorIfNeeded();
            Logger.AddToLogs(ex.ToString());
            OnNotifyError("[HttpServer] something wrong happened, more info in logs");
            return new Result(false, "http server throwed an exception, more info in logs");
        }
        private Result TaskCanceledExceptionHandler(Exception arg)
        {
            return new GoodResult();
        }
    }

    class EndpointManager : Notifiable
    {
        private Executor _executor;
        private ConcurrentDictionary<HttpMethod, ConcurrentDictionary<string, EndpointCallback>> _syncEndpointsByMethod;
        private ConcurrentDictionary<HttpMethod, ConcurrentDictionary<string, EndpointAsyncCallback>> _asyncEndpointsByMethod;
        //private ConcurrentQueue<Tuple<Request, Response>> _queue;

        public EndpointManager()
        {
            _syncEndpointsByMethod = new();
            _asyncEndpointsByMethod = new();
            //_queue = new();

            _executor = Executor
                .GetBuilder()
                .SetDefaultExceptionHandler(DefaultExceptionHandler)
                .Build();
        }
        public void Add(HttpMethod method, string endpoint, EndpointCallback callback)
        {
            if (!_syncEndpointsByMethod.ContainsKey(method))
            {
                _syncEndpointsByMethod.AddOrUpdate(
                    method,
                    key => new(),
                    (key, currentValue) => currentValue);
            }

            _syncEndpointsByMethod.AddOrUpdate(
                method,
                key => new(),
                (key, currentValue) =>
                {
                    currentValue.AddOrUpdate(
                    endpoint,
                    key => callback,
                    (key, currentValue) => callback);

                    return currentValue;
                });
        }
        public void Add(HttpMethod method, string endpoint, EndpointAsyncCallback callback)
        {
            if (!_asyncEndpointsByMethod.ContainsKey(method))
            {
                _asyncEndpointsByMethod.AddOrUpdate(
                    method,
                    key => new(),
                    (key, currentValue) => currentValue);
            }

            _asyncEndpointsByMethod.AddOrUpdate(
                method,
                key => new(),
                (key, currentValue) =>
                {
                    currentValue.AddOrUpdate(
                    endpoint,
                    key => callback,
                    (key, currentValue) => callback);

                    return currentValue;
                });
        }

        public async Task ProcessRequest(Request req, Response resp)
        { 
            bool syncMatch = _syncEndpointsByMethod.ContainsKey(req.Method) && _syncEndpointsByMethod[req.Method].ContainsKey(req.Endpoint);
            bool asyncMatch = _asyncEndpointsByMethod.ContainsKey(req.Method) && _asyncEndpointsByMethod[req.Method].ContainsKey(req.Endpoint);

            if (!syncMatch && !asyncMatch)
            {
                resp.Send(HttpStatusCode.BadRequest, "no such endpoint");
                return;
            }

            Result res;

            if (syncMatch)
            {
                res = _executor.Execute(() =>
                {
                    _syncEndpointsByMethod[req.Method][req.Endpoint](req, resp);
                    return new GoodResult();
                });
            }
            else
            {
                res = await _executor.ExecuteAsync<Nothing>(async () =>
                {
                    await _asyncEndpointsByMethod[req.Method][req.Endpoint](req, resp);
                    return new GoodResult();
                });
            }

            if (!res.IsSuccessful)
            {
                OnNotifyError($"failed to proccess request [{req.Method}] [{req.Endpoint}] (more info in logs)");
                resp.Send(HttpStatusCode.InternalServerError, "endpoint callback threw an exception");

                Logger.AddToLogs(
                    $"failed to proccess request [{req.Method}] [{req.Endpoint}]" + Environment.NewLine +
                    $"QueryString {req.QueryString}" + Environment.NewLine +
                    $"Body:" + Environment.NewLine + $"{req.Body}");

                return;
            }

            if (!resp.WasSent) resp.Send(HttpStatusCode.NotImplemented);
        }
        private Result DefaultExceptionHandler(Exception ex)
        {
            Logger.AddToLogs(ex.ToString());
            return new Result(false, "endpoint callback throwed an exception, more info in logs");
        }
    }
 
    partial class Request
    {
        public HttpMethod Method { get; private set; } = HttpMethod.Get;
        public string Endpoint { get; private set; } = string.Empty;
        public string? Body { get; private set; }
        public string? ContentType { get; private set; }
        public Dictionary<string, string>? QueryString { get; private set; }
        private Request() { }
    }
    partial class Request
    {
        public static IMethodRequired GetBuilder()
        {
            return new Builder();
        }
        public interface IMethodRequired
        {
            IEndpointRequired SetMethod(HttpMethod method);
        }
        public interface IEndpointRequired
        {
            IBuildable SetEndpoint(string endpoint);
        }
        public interface IBuildable
        {
            Request Build();
            IBuildable SetBody(string body);
            IBuildable SetContentType(string? contentType);
            IBuildable SetQueryString(in NameValueCollection array);
        }
        class Builder : IMethodRequired, IEndpointRequired, IBuildable
        {
            Request _request;
            public Builder()
            {
                _request = new Request();
            }

            public Request Build()
            {
                return _request;
            }

            public IEndpointRequired SetMethod(HttpMethod method)
            {
                _request.Method = method;
                return this;
            }
            public IBuildable SetEndpoint(string endpoint)
            {
                _request.Endpoint = endpoint;
                return this;
            }
            public IBuildable SetBody(string body)
            {
                _request.Body = body;
                return this;
            }
            public IBuildable SetContentType(string? contentType)
            {
                _request.ContentType = contentType;
                return this;
            }
            public IBuildable SetQueryString(in NameValueCollection array)
            {
                if (array is null) return this;
                
                _request.QueryString = new();

                foreach (string? k in array.AllKeys)
                {
                    if (k is string key && array[key] is string value)
                    {
                        _request.QueryString.Add(key, value);
                    }
                }
                
                return this;
            }
        }
    }
    class Response
    {
        private HttpListenerResponse _resp;
        public bool WasSent { get; private set; } = false;
        public Response(HttpListenerResponse resp)
        {
            _resp = resp;
        }
        public void Send(HttpStatusCode statusCode)
        {
            Send(statusCode, string.Empty);
        }
        public void Send(HttpStatusCode statusCode, string body)
        {
            if(!string.IsNullOrWhiteSpace(body)) 
            {
                byte[] data = Encoding.UTF8.GetBytes(body);
                _resp.ContentType = "application/json;charset=UTF-8";
                _resp.ContentEncoding = Encoding.UTF8;
                _resp.ContentLength64 = data.LongLength;
                _resp.OutputStream.Write(data, 0, data.Length);
            }

            _resp.StatusCode = (int)statusCode;
            _resp.Close();
            WasSent = true;
        }
    }
}
