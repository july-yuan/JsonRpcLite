﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using JsonRpcLite.Log;
using JsonRpcLite.Services;
using JsonRpcLite.Utilities;

namespace JsonRpcLite.InProcess
{
    internal class JsonRpcInProcessRouter
    {
        private readonly Dictionary<string, JsonRpcService> _services = new();

        public JsonRpcInProcessRouter()
        {
            RegisterServices();
        }

        /// <summary>
        /// Register all services which implemented by users
        /// </summary>
        private void RegisterServices()
        {
            var serviceTypes = Assembly.GetEntryAssembly()?.GetTypes().Where(x => typeof(JsonRpcService).IsAssignableFrom(x) && x != typeof(JsonRpcService)).ToArray();
            if (serviceTypes != null)
            {
                foreach (var serviceType in serviceTypes)
                {
                    var serviceAttributes = serviceType.GetCustomAttributes(typeof(RpcServiceAttribute), false);
                    if (serviceAttributes.Length > 1)
                    {
                        throw new InvalidOperationException($"Method {serviceType.Name} defined more than one rpc service attributes.");
                    }

                    if (serviceAttributes.Length > 0)
                    {
                        var serviceAttribute = (RpcServiceAttribute)serviceAttributes[0];
                        if (string.IsNullOrEmpty(serviceAttribute.Name)) continue;
                        var key = $"{serviceAttribute.Name.ToLower()}";
                        if (!string.IsNullOrWhiteSpace(serviceAttribute.Version))
                        {
                            key = $"{serviceAttribute.Name.ToLower()}/{serviceAttribute.Version}";
                        }
                        _services.Add(key, (JsonRpcService)serviceType.New());
                        Logger.WriteInfo($"Register service:{key}");
                    }
                }
            }
        }

        /// <summary>
        /// Dispatch request to the service
        /// </summary>
        /// <param name="serviceName">The name of the service.</param>
        /// <param name="serviceVersion">The version of the service.</param>
        /// <param name="request">The request which will pass to the service.</param>
        /// <returns>The response.</returns>
        public async Task<T> DispatchCallAsync<T>(string serviceName, string serviceVersion, T request)
        {
            try
            {
                var key = $"{serviceName}/{serviceVersion}";
                if (!_services.TryGetValue(key, out var service))
                {
                    Logger.WriteWarning($"Service for request: {key} not found.");
                    throw new InvalidOperationException($"Service [{key}] does not exist.");
                }

                byte[] requestData = null;
                int dataLength = 0;
                bool rent = false;
                if (typeof(T) == typeof(string))
                {
                    var requestString = (string)Convert.ChangeType(request, typeof(string));
                    if (!string.IsNullOrEmpty(requestString))
                    {
                        dataLength = Encoding.UTF8.GetMaxByteCount(requestString.Length);
                        requestData = ArrayPool<byte>.Shared.Rent(dataLength);
                        rent = true;
                        dataLength = Encoding.UTF8.GetBytes(requestString, requestData);
                        if (Logger.DebugMode)
                        {
                            Logger.WriteDebug($"Receive request data:{requestString}");
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("request is not a string data");
                    }
                }
                else if (typeof(T) == typeof(byte[]))
                {
                    requestData = (byte[]) Convert.ChangeType(request, typeof(byte[]));
                }

                if (requestData != null)
                {
                    var jsonRpcRequests = await JsonRpcCodec.DecodeRequestsAsync(requestData, dataLength).ConfigureAwait(false);
                    if (rent)
                    {
                        ArrayPool<byte>.Shared.Return(requestData);
                    }
                    if (jsonRpcRequests.Length == 1)
                    {
                        var response = await GetResponseAsync(service, jsonRpcRequests[0]).ConfigureAwait(false);
                        var responseData = await JsonRpcCodec.EncodeResponsesAsync(new[] {response});
                        if (typeof(T) == typeof(string))
                        {
                            var responseString = Encoding.UTF8.GetString(responseData);
                            return (T)Convert.ChangeType(responseString, typeof(T));
                        }

                        if (typeof(T) == typeof(byte[]))
                        {
                            return (T) Convert.ChangeType(responseData, typeof(T));
                        }
                    }
                    else
                    {
                        //batch call.
                        var responseList = new List<JsonRpcResponse>();
                        foreach (var jsonRpcRequest in jsonRpcRequests)
                        {
                            var response = await GetResponseAsync(service, jsonRpcRequest).ConfigureAwait(false);
                            if (response != null)
                            {
                                responseList.Add(response);
                            }
                        }

                        if (responseList.Count > 0)
                        {
                            var responseData = await JsonRpcCodec.EncodeResponsesAsync(responseList.ToArray());
                            if (typeof(T) == typeof(string))
                            {
                                var responseString = Encoding.UTF8.GetString(responseData);
                                return (T)Convert.ChangeType(responseString, typeof(T));
                            }

                            if (typeof(T) == typeof(byte[]))
                            {
                                return (T)Convert.ChangeType(responseData, typeof(T));
                            }
                        }
                    }
                }
                else
                {
                    throw new InvalidOperationException("Invalid request.");
                }
            }
            catch (Exception ex)
            {
                Logger.WriteError($"Handle request error: {ex.Format()}");
            }

            return default;
        }


        protected async Task<JsonRpcResponse> GetResponseAsync(JsonRpcService service, JsonRpcRequest request)
        {
            JsonRpcResponse response = new JsonRpcResponse(request.Id);
            try
            {
                var rpcCall = service.GetRpcCall(request.Method);
                if (rpcCall == null)
                {
                    throw new InvalidOperationException($"Method: {request.Method} not found.");
                }

                var arguments = await JsonRpcCodec.DecodeArgumentsAsync(request.Params, rpcCall.Parameters.ToArray()).ConfigureAwait(false);

                //From here we got the response id.
                //The parser will add context into the args, so the final count is parameter count + 1.
                if (arguments.Length == rpcCall.Parameters.Length)
                {
                    try
                    {
                        var result = await rpcCall.Call(arguments).ConfigureAwait(false);
                        if (request.IsNotification)
                        {
                            return null;
                        }

                        response.WriteResult(result);
                    }
                    catch (Exception ex)
                    {
                        var argumentString = new StringBuilder();
                        argumentString.Append(Environment.NewLine);
                        foreach (var argument in arguments)
                        {
                            argumentString.AppendLine($"{argument.Name} = {argument.Value}");
                        }

                        argumentString.Append(Environment.NewLine);
                        Logger.WriteError( $"Call method {rpcCall.Name} with args:{argumentString} error :{ex.Format()}");
                        response.WriteResult(new InvalidOperationException());
                    }
                }
                else
                {
                    throw new InvalidOperationException("Argument count is not matched");
                }
            }
            catch (Exception ex)
            {
                Logger.WriteError($"Handle request {request} error: {ex.Format()}");
                response.WriteResult(ex);
            }
            return response;
        }


    }
}