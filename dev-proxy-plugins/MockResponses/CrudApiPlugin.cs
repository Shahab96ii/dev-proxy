// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.DevProxy.Abstractions;
using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.DevProxy.Plugins.MockResponses;

public enum CrudApiActionType
{
    Create,
    GetAll,
    GetOne,
    GetMany,
    Merge,
    Update,
    Delete
}

public class CrudApiAction
{
    [JsonPropertyName("action")]
    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
    public CrudApiActionType Action { get; set; } = CrudApiActionType.GetAll;
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
    [JsonPropertyName("method")]
    public string? Method { get; set; }
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;
}

public class CrudApiConfiguration
{
    [JsonPropertyName("apiFile")]
    public string ApiFile { get; set; } = "api.json";
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;
    [JsonPropertyName("dataFile")]
    public string DataFile { get; set; } = string.Empty;
    [JsonPropertyName("actions")]
    public IEnumerable<CrudApiAction> Actions { get; set; } = Array.Empty<CrudApiAction>();
}

public class CrudApiPlugin : BaseProxyPlugin
{
    protected CrudApiConfiguration _configuration = new();
    private CrudApiDefinitionLoader? _loader = null;
    public override string Name => nameof(CrudApiPlugin);
    private IProxyConfiguration? _proxyConfiguration;
    private JArray? _data;

    public override void Register(IPluginEvents pluginEvents,
                            IProxyContext context,
                            ISet<UrlToWatch> urlsToWatch,
                            IConfigurationSection? configSection = null)
    {
        base.Register(pluginEvents, context, urlsToWatch, configSection);

        configSection?.Bind(_configuration);

        pluginEvents.BeforeRequest += OnRequest;

        _proxyConfiguration = context.Configuration;

        _configuration.ApiFile = Path.GetFullPath(ProxyUtils.ReplacePathTokens(_configuration.ApiFile), Path.GetDirectoryName(_proxyConfiguration?.ConfigFile ?? string.Empty) ?? string.Empty);

        _loader = new CrudApiDefinitionLoader(_logger!, _configuration);
        _loader?.InitApiDefinitionWatcher();

        LoadData();
    }

    private void LoadData()
    {
        try
        {
            var dataFilePath = Path.GetFullPath(ProxyUtils.ReplacePathTokens(_configuration.DataFile), Path.GetDirectoryName(_proxyConfiguration?.ConfigFile ?? string.Empty) ?? string.Empty);
            if (!File.Exists(dataFilePath))
            {
                _configuration.Actions = Array.Empty<CrudApiAction>();
                return;
            }

            var dataString = File.ReadAllText(dataFilePath);
            _data = JArray.Parse(dataString);
        }
        catch (Exception ex)
        {
            _logger?.LogError($"An error has occurred while reading {_configuration.DataFile}:");
            _logger?.LogError(ex.Message);
        }
    }

    protected virtual Task OnRequest(object? sender, ProxyRequestArgs e)
    {
        Request request = e.Session.HttpClient.Request;
        ResponseState state = e.ResponseState;

        if (_urlsToWatch is not null && e.ShouldExecute(_urlsToWatch))
        {
            var actionAndParams = GetMatchingActionHandler(request);
            if (actionAndParams is not null)
            {
                actionAndParams.Item1(e.Session, actionAndParams.Item2, actionAndParams.Item3);
                state.HasBeenSet = true;
            }
        }

        return Task.CompletedTask;
    }

    private void SendNotFoundResponse(SessionEventArgs e)
    {
        SendJsonResponse("{\"error\":{\"message\":\"Not found\"}}", HttpStatusCode.NotFound, e);
    }

    private string ReplaceParams(string query, IDictionary<string, string> parameters)
    {
        var result = Regex.Replace(query, "{([^}]+)}", new MatchEvaluator(m =>
        {
            return $"{{{m.Groups[1].Value.Replace('-', '_')}}}";
        }));
        foreach (var param in parameters)
        {
            result = result.Replace($"{{{param.Key}}}", param.Value);
        }
        return result;
    }

    private void SendEmptyResponse(HttpStatusCode statusCode, SessionEventArgs e)
    {
        var headers = new List<HttpHeader>();
        if (e.HttpClient.Request.Headers.Any(h => h.Name == "Origin"))
        {
            headers.Add(new HttpHeader("access-control-allow-origin", "*"));
        }
        e.GenericResponse("", statusCode, headers);
    }

    private void SendJsonResponse(string body, HttpStatusCode statusCode, SessionEventArgs e)
    {
        var headers = new List<HttpHeader> {
            new HttpHeader("content-type", "application/json; charset=utf-8")
        };
        if (e.HttpClient.Request.Headers.Any(h => h.Name == "Origin"))
        {
            headers.Add(new HttpHeader("access-control-allow-origin", "*"));
        }
        e.GenericResponse(body, statusCode, headers);
    }

    private void GetAll(SessionEventArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        SendJsonResponse(JsonConvert.SerializeObject(_data, Formatting.Indented), HttpStatusCode.OK, e);
        _logger?.LogRequest([$"200 {action.Url}"], MessageType.Mocked, new LoggingContext(e));
    }

    private void GetOne(SessionEventArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var item = _data?.SelectToken(ReplaceParams(action.Query, parameters));
            if (item is null)
            {
                SendNotFoundResponse(e);
                _logger?.LogRequest([$"404 {action.Url}"], MessageType.Mocked, new LoggingContext(e));
                return;
            }

            SendJsonResponse(JsonConvert.SerializeObject(item, Formatting.Indented), HttpStatusCode.OK, e);
            _logger?.LogRequest([$"200 {action.Url}"], MessageType.Mocked, new LoggingContext(e));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e);
            _logger?.LogRequest([$"500 {action.Url}"], MessageType.Failed, new LoggingContext(e));
        }
    }

    private void GetMany(SessionEventArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var items = _data?.SelectTokens(ReplaceParams(action.Query, parameters));
            if (items is null)
            {
                items = Array.Empty<JToken>();
            }

            SendJsonResponse(JsonConvert.SerializeObject(items, Formatting.Indented), HttpStatusCode.OK, e);
            _logger?.LogRequest([$"200 {action.Url}"], MessageType.Mocked, new LoggingContext(e));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e);
            _logger?.LogRequest([$"500 {action.Url}"], MessageType.Failed, new LoggingContext(e));
        }
    }

    private void Create(SessionEventArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            _data?.Add(JObject.Parse(e.HttpClient.Request.BodyString));
            SendJsonResponse(JsonConvert.SerializeObject(e.HttpClient.Request.BodyString, Formatting.Indented), HttpStatusCode.Created, e);
            _logger?.LogRequest([$"201 {action.Url}"], MessageType.Mocked, new LoggingContext(e));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e);
            _logger?.LogRequest([$"500 {action.Url}"], MessageType.Failed, new LoggingContext(e));
        }
    }

    private void Merge(SessionEventArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var item = _data?.SelectToken(ReplaceParams(action.Query, parameters));
            if (item is null)
            {
                SendNotFoundResponse(e);
                _logger?.LogRequest([$"404 {action.Url}"], MessageType.Mocked, new LoggingContext(e));
                return;
            }
            var update = JObject.Parse(e.HttpClient.Request.BodyString);
            ((JContainer)item)?.Merge(update);
            SendEmptyResponse(HttpStatusCode.NoContent, e);
            _logger?.LogRequest([$"204 {action.Url}"], MessageType.Mocked, new LoggingContext(e));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e);
            _logger?.LogRequest([$"500 {action.Url}"], MessageType.Failed, new LoggingContext(e));
        }
    }

    private void Update(SessionEventArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var item = _data?.SelectToken(ReplaceParams(action.Query, parameters));
            if (item is null)
            {
                SendNotFoundResponse(e);
                _logger?.LogRequest([$"404 {action.Url}"], MessageType.Mocked, new LoggingContext(e));
                return;
            }
            var update = JObject.Parse(e.HttpClient.Request.BodyString);
            ((JContainer)item)?.Replace(update);
            SendEmptyResponse(HttpStatusCode.NoContent, e);
            _logger?.LogRequest([$"204 {action.Url}"], MessageType.Mocked, new LoggingContext(e));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e);
            _logger?.LogRequest([$"500 {action.Url}"], MessageType.Failed, new LoggingContext(e));
        }
    }

    private void Delete(SessionEventArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var item = _data?.SelectToken(ReplaceParams(action.Query, parameters));
            if (item is null)
            {
                SendNotFoundResponse(e);
                _logger?.LogRequest([$"404 {action.Url}"], MessageType.Mocked, new LoggingContext(e));
                return;
            }

            item?.Remove();
            SendEmptyResponse(HttpStatusCode.NoContent, e);
            _logger?.LogRequest([$"204 {action.Url}"], MessageType.Mocked, new LoggingContext(e));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e);
            _logger?.LogRequest([$"500 {action.Url}"], MessageType.Failed, new LoggingContext(e));
        }
    }

    private Tuple<Action<SessionEventArgs, CrudApiAction, IDictionary<string, string>>, CrudApiAction, IDictionary<string, string>>? GetMatchingActionHandler(Request request)
    {
        if (_configuration.Actions is null ||
            !_configuration.Actions.Any())
        {
            return null;
        }

        var parameterMatchEvaluator = new MatchEvaluator(m =>
        {
            var paramName = m.Value.Trim('{', '}').Replace('-', '_');
            return $"(?<{paramName}>[^/&]+)";
        });

        var parameters = new Dictionary<string, string>();
        var action = _configuration.Actions.FirstOrDefault(action =>
        {
            if (action.Method != request.Method) return false;
            var absoluteActionUrl = (_configuration.BaseUrl.TrimEnd('/') + "/" + action.Url.TrimStart('/')).TrimEnd('/');

            if (absoluteActionUrl == request.Url)
            {
                return true;
            }

            // check if the action contains parameters
            // if it doesn't, it's not a match for the current request for sure
            if (!absoluteActionUrl.Contains('{'))
            {
                return false;
            }

            // convert parameters into named regex groups
            var urlRegex = Regex.Replace(Regex.Escape(absoluteActionUrl).Replace("\\{", "{"), "({[^}]+})", parameterMatchEvaluator);
            var match = Regex.Match(request.Url, urlRegex);
            if (!match.Success)
            {
                return false;
            }

            foreach (var groupName in match.Groups.Keys)
            {
                if (groupName == "0")
                {
                    continue;
                }
                parameters.Add(groupName, match.Groups[groupName].Value);
            }
            return true;
        });

        if (action is null)
        {
            return null;
        }

        return new Tuple<Action<SessionEventArgs, CrudApiAction, IDictionary<string, string>>, CrudApiAction, IDictionary<string, string>>(action.Action switch
        {
            CrudApiActionType.Create => Create,
            CrudApiActionType.GetAll => GetAll,
            CrudApiActionType.GetOne => GetOne,
            CrudApiActionType.GetMany => GetMany,
            CrudApiActionType.Merge => Merge,
            CrudApiActionType.Update => Update,
            CrudApiActionType.Delete => Delete,
            _ => throw new NotImplementedException()
        }, action, parameters);
    }
}
