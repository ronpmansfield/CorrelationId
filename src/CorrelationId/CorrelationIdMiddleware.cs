﻿using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace CorrelationId
{
    /// <summary>
    /// Middleware which attempts to reads / creates a Correlation ID that can then be used in logs and 
    /// passed to upstream requests.
    /// </summary>
    public class CorrelationIdMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger _logger;
        private readonly CorrelationIdOptions _options;

        /// <summary>
        /// Creates a new instance of the CorrelationIdMiddleware.
        /// </summary>
        /// <param name="next">The next middleware in the pipeline.</param>
        /// <param name="logger">The <see cref="ILogger"/> instance to log to.</param>
        /// <param name="options">The configuration options.</param>
        public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger, IOptions<CorrelationIdOptions> options)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Processes a request to synchronise TraceIdentifier and Correlation ID headers. Also creates a 
        /// <see cref="CorrelationContext"/> for the current request and disposes of it when the request is completing.
        /// </summary>
        /// <param name="context">The <see cref="HttpContext"/> for the current request.</param>
        /// <param name="correlationContextFactory">The <see cref="ICorrelationContextFactory"/> which can create a <see cref="CorrelationContext"/>.</param>
        public async Task Invoke(HttpContext context, ICorrelationContextFactory correlationContextFactory)
        {
            var hasCorrelationIdHeader = context.Request.Headers.TryGetValue(_options.Header, out var cid) &&
                                           !StringValues.IsNullOrEmpty(cid);

            if (!hasCorrelationIdHeader && _options.EnforceHeader)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync($"The '{_options.Header}' request header is required, but was not found.");
                return;
            }

            var correlationId = hasCorrelationIdHeader ? cid.FirstOrDefault() : "Not set";

            if (_options.IgnoreRequestHeader || RequiresGenerationOfCorrelationId(hasCorrelationIdHeader, cid))
            {
                correlationId = GenerateCorrelationId(context.TraceIdentifier);
            }
            
            if (_options.UpdateTraceIdentifier)
                context.TraceIdentifier = correlationId;

            correlationContextFactory.Create(correlationId, _options.Header);

            if (_options.IncludeInResponse)
            {
                // apply the correlation ID to the response header for client side tracking
                context.Response.OnStarting(() =>
                {
                    if (!context.Response.Headers.ContainsKey(_options.Header))
                    {
                        context.Response.Headers.Add(_options.Header, correlationId);
                    }

                    return Task.CompletedTask;
                });
            }

            using (_logger.BeginScope(new Dictionary<string, object>
            {
                {nameof(CorrelationContext.CorrelationId), correlationId}
            }))
            {
                await _next(context);
            }

            correlationContextFactory.Dispose();
        }
        
        private static bool RequiresGenerationOfCorrelationId(bool idInHeader, StringValues idFromHeader) => 
            !idInHeader || StringValues.IsNullOrEmpty(idFromHeader);

        private StringValues GenerateCorrelationId(string traceIdentifier)
        {
            if (_options.UseGuidForCorrelationId) return Guid.NewGuid().ToString();
            if (_options.CorrelationIdGenerator != null) return _options.CorrelationIdGenerator();
            return string.IsNullOrEmpty(traceIdentifier) ? Guid.NewGuid().ToString() : traceIdentifier;
        }
    }
}