using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Zoro.IO;
using Zoro.IO.Json;
using Zoro.Ledger;
using Zoro.Network.RPC;
using Zoro.Network.P2P;
using Cowboy.Sockets;

namespace Zoro.RpcHost
{
    class RpcHost : IDisposable
    {
        private class RpcTask
        {
            public HttpContext Context;
            public JObject Response;
            public AutoResetEvent ResetEvent;
        }

        private IWebHost host;
        private TcpSocketClient client;
        private readonly ConcurrentDictionary<Guid, RpcTask> RpcTasks = new ConcurrentDictionary<Guid, RpcTask>();
        private readonly ConcurrentQueue<RpcRequestPayload> request_queue = new ConcurrentQueue<RpcRequestPayload>();

        public RpcHost()
        {
        }

        private static JObject CreateErrorResponse(JObject id, int code, string message, JObject data = null)
        {
            JObject response = CreateResponse(id);
            response["error"] = new JObject();
            response["error"]["code"] = code;
            response["error"]["message"] = message;
            if (data != null)
                response["error"]["data"] = data;
            return response;
        }

        private static JObject CreateResponse(JObject id)
        {
            JObject response = new JObject();
            response["jsonrpc"] = "2.0";
            response["id"] = id;
            return response;
        }

        public void Dispose()
        {
            if (host != null)
            {
                host.Dispose();
                host = null;
            }
        }

        private static JObject GetRelayResult(RelayResultReason reason)
        {
            switch (reason)
            {
                case RelayResultReason.Succeed:
                    return true;
                case RelayResultReason.AlreadyExists:
                    throw new RpcException(-501, "Block or transaction already exists and cannot be sent repeatedly.");
                case RelayResultReason.OutOfMemory:
                    throw new RpcException(-502, "The memory pool is full and no more transactions can be sent.");
                case RelayResultReason.UnableToVerify:
                    throw new RpcException(-503, "The block cannot be validated.");
                case RelayResultReason.Invalid:
                    throw new RpcException(-504, "Block or transaction validation failed.");
                default:
                    throw new RpcException(-500, "Unkown error.");
            }
        }

        private UInt160 GetChainHash(JObject param)
        {
            string hashString = param.AsString();
            if (hashString.Length == 40 || (hashString.StartsWith("0x") && hashString.Length == 42))
            {
                return UInt160.Parse(param.AsString());
            }

            return UInt160.Zero;
        }

        private async Task ProcessAsync(HttpContext context)
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST";
            context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            context.Response.Headers["Access-Control-Max-Age"] = "31536000";
            if (context.Request.Method != "GET" && context.Request.Method != "POST") return;
            JObject request = null;
            if (context.Request.Method == "GET")
            {
                string jsonrpc = context.Request.Query["jsonrpc"];
                string id = context.Request.Query["id"];
                string method = context.Request.Query["method"];
                string _params = context.Request.Query["params"];
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(method) && !string.IsNullOrEmpty(_params))
                {
                    try
                    {
                        _params = Encoding.UTF8.GetString(Convert.FromBase64String(_params));
                    }
                    catch (FormatException) { }
                    request = new JObject();
                    if (!string.IsNullOrEmpty(jsonrpc))
                        request["jsonrpc"] = jsonrpc;
                    request["id"] = id;
                    request["method"] = method;
                    request["params"] = JObject.Parse(_params);
                }
            }
            else if (context.Request.Method == "POST")
            {
                using (StreamReader reader = new StreamReader(context.Request.Body))
                {
                    try
                    {
                        request = JObject.Parse(reader);
                    }
                    catch (FormatException) { }
                }
            }
            JObject response;
            if (request == null)
            {
                response = CreateErrorResponse(null, -32700, "Parse error");
            }
            else if (request is JArray array)
            {
                if (array.Count == 0)
                {
                    response = CreateErrorResponse(request["id"], -32600, "Invalid Request");
                }
                else
                {
                    response = array.Select(p => ProcessRequest(context, p)).Where(p => p != null).ToArray();
                }
            }
            else
            {
                response = ProcessRequest(context, request);
            }
            if (response == null || (response as JArray)?.Count == 0) return;
            context.Response.ContentType = "application/json-rpc";
            await context.Response.WriteAsync(response.ToString(), Encoding.UTF8);
        }

        private JObject ProcessRequest(HttpContext context, JObject request)
        {
            if (!request.ContainsProperty("id")) return null;
            if (!request.ContainsProperty("method") || !request.ContainsProperty("params") || !(request["params"] is JArray))
            {
                return CreateErrorResponse(request["id"], -32600, "Invalid Request");
            }
            try
            {
                string method = request["method"].AsString();
                JArray _params = (JArray)request["params"];
                Guid guid = Process(method, _params);

                JObject response = CreateResponse(request["id"]);
                WaitRpcTask(guid, context, response);

                return response;
            }
            catch (Exception ex)
            {
#if DEBUG
                return CreateErrorResponse(request["id"], ex.HResult, ex.Message, ex.StackTrace);
#else
                return CreateErrorResponse(request["id"], ex.HResult, ex.Message);
#endif
            }
        }

        private Guid Process(string method, JArray _params)
        {
            switch (method)
            {
                case "invokescript":
                case "sendrawtransaction":
                    {
                        UInt160 chainHash = GetChainHash(_params[0]);
                        RpcRequestPayload payload = RpcRequestPayload.Create(method, chainHash, _params[1].AsString().HexToBytes());
                        EnqueueRequest(payload);
                        return payload.Guid;
                    }
                default:
                    throw new RpcException(-32601, "Method not found");
            }
        }

        public void StartWebHost(IPAddress bindAddress, int port, string sslCert = null, string password = null, string[] trustedAuthorities = null)
        {
            host = new WebHostBuilder().UseKestrel(options => options.Listen(bindAddress, port, listenOptions =>
            {
                if (string.IsNullOrEmpty(sslCert)) return;
                listenOptions.UseHttps(sslCert, password, httpsConnectionAdapterOptions =>
                {
                    if (trustedAuthorities is null || trustedAuthorities.Length == 0)
                        return;
                    httpsConnectionAdapterOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    httpsConnectionAdapterOptions.ClientCertificateValidation = (cert, chain, err) =>
                    {
                        if (err != SslPolicyErrors.None)
                            return false;
                        X509Certificate2 authority = chain.ChainElements[chain.ChainElements.Count - 1].Certificate;
                        return trustedAuthorities.Contains(authority.Thumbprint);
                    };
                });
            }))
            .Configure(app =>
            {
                app.UseResponseCompression();
                app.Run(ProcessAsync);
            })
            .ConfigureServices(services =>
            {
                services.AddResponseCompression(options =>
                {
                    // options.EnableForHttps = false;
                    options.Providers.Add<GzipCompressionProvider>();
                    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/json-rpc" });
                });

                services.Configure<GzipCompressionProviderOptions>(options =>
                {
                    options.Level = CompressionLevel.Fastest;
                });
            })
            .Build();

            host.Start();
        }

        public void ConnectToAgent(IPAddress address, int port)
        {
            var config = new TcpSocketClientConfiguration();
            IPEndPoint remoteEP = new IPEndPoint(address, port);

            client = new TcpSocketClient(remoteEP, config);
            client.ServerConnected += client_ServerConnected;
            client.ServerDisconnected += client_ServerDisconnected;
            client.ServerDataReceived += client_ServerDataReceived;
            client.Connect();
        }

        void client_ServerConnected(object sender, TcpServerConnectedEventArgs e)
        {
            Console.WriteLine(string.Format("TCP server {0} has connected.", e.RemoteEndPoint));
        }

        void client_ServerDisconnected(object sender, TcpServerDisconnectedEventArgs e)
        {
            Console.WriteLine(string.Format("TCP server {0} has disconnected.", e.RemoteEndPoint));
        }

        void client_ServerDataReceived(object sender, TcpServerDataReceivedEventArgs e)
        {
            byte[] data = e.Data.Skip(e.DataOffset).Take(e.DataLength).ToArray();
            Message msg = data.AsSerializable<Message>();

            if (msg.Command == "rpc-response")
            {
                RpcResponsePayload payload = msg.Payload.AsSerializable<RpcResponsePayload>();

                OnReceiveRpcResult(payload);
            }
        }

        private void EnqueueRequest(RpcRequestPayload payload)
        {
            request_queue.Enqueue(payload);
        }

        private void SendRpcRequest()
        {
            if (request_queue.TryDequeue(out RpcRequestPayload payload))
            {
                Message msg = Message.Create("rpc-request", payload.ToArray());
                client.Send(msg.ToArray());
            }
        }

        private void WaitRpcTask(Guid guid, HttpContext context, JObject response)
        {
            RpcTask task = new RpcTask
            {
                Context = context,
                Response = response,
                ResetEvent = new AutoResetEvent(false),

            };

            RpcTasks.TryAdd(guid, task);

            SendRpcRequest();

            task.ResetEvent.WaitOne();
        }

        public void OnReceiveRpcResult(RpcResponsePayload payload)
        {
            if (RpcTasks.TryGetValue(payload.Guid, out RpcTask task))
            {
                task.Response["result"] = payload.Result;
                task.ResetEvent.Set();
            }
        }
    }
}
