using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Serilog.Ui.Core;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Serilog.Ui.Web
{
    public class SerilogUiMiddleware
    {
        private const string EmbeddedFileNamespace = "Serilog.Ui.Web.wwwroot.dist";
        private readonly UiOptions _options;
        private readonly StaticFileMiddleware _staticFileMiddleware;
        private readonly JsonSerializerSettings _jsonSerializerOptions;
        private readonly ILogger<SerilogUiMiddleware> _logger;
        private string[] _providerKeys;

        public SerilogUiMiddleware(
            RequestDelegate next,
            IWebHostEnvironment hostingEnv,
            ILoggerFactory loggerFactory,
            UiOptions options,
            ILogger<SerilogUiMiddleware> logger)
        {
            _options = options;
            _logger = logger;
            _staticFileMiddleware = CreateStaticFileMiddleware(next, hostingEnv, loggerFactory);
            _jsonSerializerOptions = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                Formatting = Formatting.None
            };
        }

        public async Task Invoke(HttpContext httpContext)
        {
            var httpMethod = httpContext.Request.Method;
            var path = httpContext.Request.Path.Value;

            if (httpMethod == "GET" && Regex.IsMatch(path, $"^/{Regex.Escape(_options.RoutePrefix)}/api/keys/?$",
                    RegexOptions.IgnoreCase))
            {
                httpContext.Response.ContentType = "application/json;charset=utf-8";
                if (!CanAccess(httpContext))
                {
                    httpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    return;
                }

                try
                {
                    httpContext.Response.ContentType = "application/json;charset=utf-8";
                    if (!CanAccess(httpContext))
                    {
                        httpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                        return;
                    }

                    if (_providerKeys == null)
                    {
                        var aggregateDataProvider = httpContext.RequestServices.GetRequiredService<AggregateDataProvider>();
                        _providerKeys = aggregateDataProvider.Keys.ToArray();
                    }

                    var result = JsonConvert.SerializeObject(_providerKeys);
                    httpContext.Response.StatusCode = (int)HttpStatusCode.OK;
                    await httpContext.Response.WriteAsync(result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, ex.Message);
                    httpContext.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

                    var errorMessage = httpContext.Request.IsLocal()
                        ? JsonConvert.SerializeObject(new { errorMessage = ex.Message })
                        : JsonConvert.SerializeObject(new { errorMessage = "Internal server error" });

                    await httpContext.Response.WriteAsync(JsonConvert.SerializeObject(new { errorMessage }));
                }

                return;
            }

            // If the RoutePrefix is requested (with or without trailing slash), redirect to index URL
            if (httpMethod == "GET" && Regex.IsMatch(path, $"^/{Regex.Escape(_options.RoutePrefix)}/api/logs/?$", RegexOptions.IgnoreCase))
            {
                try
                {
                    httpContext.Response.ContentType = "application/json;charset=utf-8";
                    if (!CanAccess(httpContext))
                    {
                        httpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                        return;
                    }

                    var result = await FetchLogsAsync(httpContext);
                    httpContext.Response.StatusCode = (int)HttpStatusCode.OK;
                    await httpContext.Response.WriteAsync(result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, ex.Message);
                    httpContext.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

                    var errorMessage = httpContext.Request.IsLocal()
                        ? JsonConvert.SerializeObject(new { errorMessage = ex.Message })
                        : JsonConvert.SerializeObject(new { errorMessage = "Internal server error" });

                    await httpContext.Response.WriteAsync(JsonConvert.SerializeObject(new { errorMessage }));
                }

                return;
            }

            if (httpMethod == "GET" && Regex.IsMatch(path, $"^/?{Regex.Escape(_options.RoutePrefix)}/?$", RegexOptions.IgnoreCase))
            {
                var indexUrl = httpContext.Request.GetEncodedUrl().TrimEnd('/') + "/index.html";
                RespondWithRedirect(httpContext.Response, indexUrl);
                return;
            }

            if (httpMethod == "GET" && Regex.IsMatch(path, $"^/{Regex.Escape(_options.RoutePrefix)}/?index.html$", RegexOptions.IgnoreCase))
            {
                await RespondWithIndexHtml(httpContext.Response);
                return;
            }

            await _staticFileMiddleware.Invoke(httpContext);
        }

        private StaticFileMiddleware CreateStaticFileMiddleware(
            RequestDelegate next,
            IWebHostEnvironment hostingEnv,
            ILoggerFactory loggerFactory)
        {
            var staticFileOptions = new StaticFileOptions
            {
                RequestPath = $"/{_options.RoutePrefix}",
                FileProvider = new EmbeddedFileProvider(typeof(SerilogUiMiddleware).GetTypeInfo().Assembly, EmbeddedFileNamespace),
            };

            return new StaticFileMiddleware(next, hostingEnv, Options.Create(staticFileOptions), loggerFactory);
        }

        private void RespondWithRedirect(HttpResponse response, string location)
        {
            response.StatusCode = 301;
            response.Headers["Location"] = location;
        }

        private async Task RespondWithIndexHtml(HttpResponse response)
        {
            if (!CanAccess(response.HttpContext))
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                response.ContentType = "text/html;charset=utf-8";
                await response.WriteAsync("<p>You don't have enough permission to access this page!</p>", Encoding.UTF8);

                return;
            }

            response.StatusCode = 200;
            response.ContentType = "text/html;charset=utf-8";

            await using var stream = IndexStream();
            var htmlStringBuilder = new StringBuilder(await new StreamReader(stream).ReadToEndAsync());
            var authType = _options.Authorization.AuthenticationType.ToString();
            var encodeAuthOpts = Uri.EscapeDataString(JsonConvert.SerializeObject(new { _options.RoutePrefix, authType, _options.HomeUrl }, _jsonSerializerOptions));

            htmlStringBuilder
                .Replace("%(Configs)", encodeAuthOpts)
                .Replace("<meta name=\"dummy\" content=\"%(HeadContent)\">", _options.HeadContent)
                .Replace("<meta name=\"dummy\" content=\"%(BodyContent)\">", _options.BodyContent);

            var htmlString = htmlStringBuilder.ToString();
            await response.WriteAsync(htmlString, Encoding.UTF8);
        }

        private Func<Stream> IndexStream { get; } = () => typeof(AuthorizationOptions).GetTypeInfo().Assembly
            .GetManifestResourceStream("Serilog.Ui.Web.wwwroot.dist.index.html");

        private async Task<string> FetchLogsAsync(HttpContext httpContext)
        {
            httpContext.Request.Query.TryGetValue("page", out var pageStr);
            httpContext.Request.Query.TryGetValue("count", out var countStr);
            httpContext.Request.Query.TryGetValue("level", out var levelStr);
            httpContext.Request.Query.TryGetValue("search", out var searchStr);
            httpContext.Request.Query.TryGetValue("startDate", out var startDateStar);
            httpContext.Request.Query.TryGetValue("endDate", out var endDateStar);
            httpContext.Request.Query.TryGetValue("key", out var keyStr);

            int.TryParse(pageStr, out var currentPage);
            int.TryParse(countStr, out var count);

            DateTime.TryParse(startDateStar, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var startDate);
            DateTime.TryParse(endDateStar, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var endDate);

            currentPage = currentPage == default ? 1 : currentPage;
            count = count == default ? 10 : count;

            var provider = httpContext.RequestServices.GetService<AggregateDataProvider>();

            string key = keyStr;
            if (!string.IsNullOrWhiteSpace(key))
            {
                provider.SwitchToProvider(key);
            }

            var (logs, total) = await provider.FetchDataAsync(currentPage, count, levelStr, searchStr,
                startDate == default ? (DateTime?)null : startDate, endDate == default ? (DateTime?)null : endDate);
            var result = JsonConvert.SerializeObject(new { logs, total, count, currentPage }, _jsonSerializerOptions);

            return result;
        }

        private bool CanAccess(HttpContext httpContext)
        {
            return _options.Authorization.Filters.All(filter => filter.Authorize(httpContext));
        }
    }
}