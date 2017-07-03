﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Newtonsoft.Json;
using Sharpbrake.Client.Impl;
using Sharpbrake.Client.Model;
#if NET452
using System.Threading.Tasks;
#elif NETSTANDARD1_4
using System.Runtime.InteropServices;
using System.Threading.Tasks;
#endif

namespace Sharpbrake.Client
{
    /// <summary>
    /// Functionality for notifying Airbrake on exception.
    /// </summary>
    public class AirbrakeNotifier
    {
        private readonly AirbrakeConfig config;
        private readonly ILogger logger;
        private readonly IHttpRequestHandler httpRequestHandler;

        /// <summary>
        /// List of filters for applying to the <see cref="Notice"/> object.
        /// </summary>
        private readonly IList<Func<Notice, Notice>> filters = new List<Func<Notice, Notice>>();

        /// <summary>
        /// Initializes a new instance of the <see cref="AirbrakeNotifier"/> class.
        /// </summary>
        /// <param name="config">The <see cref="AirbrakeConfig"/> instance to use.</param>
        /// <param name="logger">The <see cref="ILogger"/> implementation to use.</param>
        /// <param name="httpRequestHandler">The <see cref="IHttpRequestHandler"/> implementation to use.</param>
        public AirbrakeNotifier(AirbrakeConfig config, ILogger logger = null, IHttpRequestHandler httpRequestHandler = null)
        {
            this.config = config ?? throw new ArgumentNullException(nameof(config));

            // use default FileLogger if no custom implementation has been provided
            // but config contains non-empty value for "LogFile" property
            if (logger != null)
                this.logger = logger;
            else if (!string.IsNullOrEmpty(config.LogFile))
                this.logger = new FileLogger(config.LogFile);

            // use default provider that returns HttpWebRequest from standard .NET library
            // if custom implementation has not been provided
            this.httpRequestHandler = httpRequestHandler ?? new HttpRequestHandler(config.ProjectId, config.ProjectKey, config.Host);
        }

        /// <summary>
        /// Adds filter to the list of filters for current notifier.
        /// </summary>
        public void AddFilter(Func<Notice, Notice> filter)
        {
            filters.Add(filter);
        }

        /// <summary>
        /// Notifies Airbrake on error in your app and logs response from Airbrake.
        /// </summary>
        /// <remarks>
        /// Call to Airbrake is made asynchronously. Logging is deferred and occurs only if constructor has been
        /// provided with logger implementation or config contains non-empty value for "LogFile" property.
        /// </remarks>
        public void Notify(Exception exception, IHttpContext context = null, Severity severity = Severity.Error)
        {
            var notifyTask = NotifyAsync(exception, context, severity);
            if (logger != null)
            {
                notifyTask.ContinueWith(response =>
                {
                    if (response.IsFaulted)
                        logger.Log(response.Exception);
                    else
                        logger.Log(response.Result);
                });
            }
        }

        /// <summary>
        /// Notifies Airbrake on error in your app using asynchronous call.
        /// </summary>
        public Task<AirbrakeResponse> NotifyAsync(Exception exception, IHttpContext context = null, Severity severity = Severity.Error)
        {
            if (string.IsNullOrEmpty(config.ProjectId))
                throw new Exception("Project Id is required");

            if (string.IsNullOrEmpty(config.ProjectKey))
                throw new Exception("Project Key is required");

            // Task-based Asynchronous Pattern (https://msdn.microsoft.com/en-us/library/hh873177.aspx)
            var tcs = new TaskCompletionSource<AirbrakeResponse>();
            try
            {
                if (Utils.IsIgnoredEnvironment(config.Environment, config.IgnoreEnvironments))
                {
                    var response = new AirbrakeResponse { Status = RequestStatus.Ignored };
                    tcs.SetResult(response);
                    return tcs.Task;
                }

                var noticeBuilder = new NoticeBuilder();
                noticeBuilder.SetErrorEntries(exception);
                noticeBuilder.SetConfigurationContext(config);
                noticeBuilder.SetSeverity(severity);

                if (context != null)
                    noticeBuilder.SetHttpContext(context, config);

#if NET452
                noticeBuilder.SetEnvironmentContext(Dns.GetHostName(), Environment.OSVersion.VersionString, "C#/NET45");
#elif NETSTANDARD1_4
                // TODO: check https://github.com/dotnet/corefx/issues/4306 for "Environment.MachineName"
                noticeBuilder.SetEnvironmentContext("", RuntimeInformation.OSDescription, "C#/NETCORE");
#endif
                var notice = noticeBuilder.ToNotice();

                if (filters.Count > 0)
                    notice = Utils.ApplyFilters(notice, filters);

                if (notice == null)
                {
                    var response = new AirbrakeResponse { Status = RequestStatus.Ignored };
                    tcs.SetResult(response);
                    return tcs.Task;
                }

                var request = httpRequestHandler.Get();

                request.ContentType = "application/json";
                request.Accept = "application/json";
                request.Method = "POST";

                request.GetRequestStreamAsync().ContinueWith(requestStreamTask =>
                {
                    if (requestStreamTask.IsFaulted)
                    {
                        if (requestStreamTask.Exception != null)
                            tcs.SetException(requestStreamTask.Exception.InnerExceptions);
                    }
                    else if (requestStreamTask.IsCanceled)
                    {
                        tcs.SetCanceled();
                    }
                    else
                    {
                        using (var requestStream = requestStreamTask.Result)
                        using (var requestWriter = new StreamWriter(requestStream))
                            requestWriter.Write(NoticeBuilder.ToJsonString(notice));

                        request.GetResponseAsync().ContinueWith(responseTask =>
                        {
                            if (responseTask.IsFaulted)
                            {
                                if (responseTask.Exception != null)
                                    tcs.SetException(responseTask.Exception.InnerExceptions);
                            }
                            else if (responseTask.IsCanceled)
                            {
                                tcs.SetCanceled();
                            }
                            else
                            {
                                IHttpResponse httpResponse = null;
                                try
                                {
                                    httpResponse = responseTask.Result;

                                    using (var responseStream = httpResponse.GetResponseStream())
                                    using (var responseReader = new StreamReader(responseStream))
                                    {
                                        var airbrakeResponse = JsonConvert.DeserializeObject<AirbrakeResponse>(responseReader.ReadToEnd());
                                        // Note: a success response means that the data has been received and accepted for processing.
                                        // Use the URL or id from the response to query the status of an error. This will tell you if the error has been processed,
                                        // or if it has been rejected for reasons including invalid JSON and rate limiting.
                                        airbrakeResponse.Status = httpResponse.StatusCode == HttpStatusCode.Created
                                            ? RequestStatus.Success
                                            : RequestStatus.RequestError;

                                        tcs.SetResult(airbrakeResponse);
                                    }
                                }
                                finally
                                {
                                    var disposableResponse = httpResponse as IDisposable;
                                    disposableResponse?.Dispose();
                                }
                            }
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
                return tcs.Task;
            }

            return tcs.Task;
        }
    }
}
