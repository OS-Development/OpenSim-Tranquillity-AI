using System.Collections.Concurrent;
using System.Net;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace OpenSim.Server.Handlers.Marketplace;

/// <summary>
/// Firestorm Marketplace inventory-import compatibility endpoint.
///
/// Firestorm uses:
/// GET  /api/1/{agent_id}/inventory/import/        -> establish session cookie
/// POST /api/1/{agent_id}/inventory/import/       -> trigger import
/// GET  /api/1/{agent_id}/inventory/import/{job}  -> poll a job
///
/// The HTTP contract is implemented independently of the eventual inventory
/// scanning worker so the viewer-facing endpoint remains stable.
/// </summary>
public sealed class MarketplaceInventoryImportHandler : SimpleStreamHandler
{
    private readonly IInventoryService _inventory;

    private static readonly ConcurrentDictionary<string, UUID> Sessions = new();
    private static readonly ConcurrentDictionary<string, UUID> Jobs = new();

    public MarketplaceInventoryImportHandler(IInventoryService inventory)
        : base("/api/1")
    {
        _inventory = inventory;
    }

    protected override void ProcessRequest(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        httpResponse.ContentType = "application/llsd+xml";

        string path = httpRequest.UriPath.TrimEnd('/');
        string[] parameters = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parameters.Length < 5 ||
            !string.Equals(parameters[0], "api", StringComparison.OrdinalIgnoreCase) ||
            parameters[1] != "1" ||
            !UUID.TryParse(parameters[2], out UUID agentId) ||
            !string.Equals(parameters[3], "inventory", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(parameters[4], "import", StringComparison.OrdinalIgnoreCase))
        {
            SetResponse(httpResponse, HttpStatusCode.NotFound, Array.Empty<byte>());
            return;
        }

        string? session = GetSession(httpRequest);
        bool hasSession = !string.IsNullOrEmpty(session) &&
                          Sessions.TryGetValue(session!, out UUID sessionAgent) &&
                          sessionAgent == agentId;

        bool isJobRequest = parameters.Length > 5;

        if (isJobRequest)
        {
            if (!hasSession)
            {
                SetResponse(httpResponse, HttpStatusCode.Unauthorized, Array.Empty<byte>());
                return;
            }

            string jobId = parameters[5];
            if (!Jobs.TryGetValue(jobId, out UUID jobAgent) || jobAgent != agentId)
            {
                SetResponse(httpResponse, HttpStatusCode.NotFound, Array.Empty<byte>());
                return;
            }

            SetResponse(httpResponse, HttpStatusCode.OK, SerializeEmptyMap());
            return;
        }

        switch (httpRequest.HttpMethod.ToUpperInvariant())
        {
            case "GET":
                EstablishSession(httpResponse, agentId);
                return;

            case "POST":
                if (!hasSession)
                {
                    SetResponse(httpResponse, HttpStatusCode.Unauthorized, Array.Empty<byte>());
                    return;
                }

                if (!_inventory.HasInventoryForUser(agentId))
                {
                    SetResponse(httpResponse, HttpStatusCode.Forbidden, Array.Empty<byte>());
                    return;
                }

                TriggerImport(agentId, httpResponse);
                return;

            default:
                SetResponse(httpResponse, HttpStatusCode.MethodNotAllowed, Array.Empty<byte>());
                return;
        }
    }

    private static void EstablishSession(IOSHttpResponse response, UUID agentId)
    {
        string token = Guid.NewGuid().ToString("N");
        Sessions[token] = agentId;

        // Firestorm stores this Set-Cookie value verbatim and sends it back
        // as its Cookie header. Do not append Path/Expires attributes.
        response.AddHeader("Set-Cookie", "marketplace_session=" + token);
        SetResponse(response, HttpStatusCode.OK, SerializeEmptyMap());
    }

    private static string? GetSession(IOSHttpRequest request)
    {
        string? cookie = request.Headers["Cookie"];
        if (string.IsNullOrEmpty(cookie))
            return null;

        foreach (string part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string value = part.Trim();
            const string prefix = "marketplace_session=";

            if (value.StartsWith(prefix, StringComparison.Ordinal))
                return value[prefix.Length..];
        }

        return null;
    }

    private static void TriggerImport(UUID agentId, IOSHttpResponse response)
    {
        string jobId = Guid.NewGuid().ToString("N");
        Jobs[jobId] = agentId;

        // The current boundary is synchronous. The job endpoint is still exposed
        // so a later asynchronous importer can preserve the same URL contract.
        SetResponse(response, HttpStatusCode.OK, SerializeString(jobId));
    }

    private static void SetResponse(IOSHttpResponse response, HttpStatusCode status, byte[] body)
    {
        response.StatusCode = (int)status;
        response.RawBuffer = body;
        response.ContentLength64 = body.Length;
    }

    private static byte[] SerializeEmptyMap()
    {
        return Encoding.UTF8.GetBytes(
            "<?xml version="1.0" encoding="UTF-8"?><llsd><map /></llsd>");
    }

    private static byte[] SerializeString(string value)
    {
        string escaped = System.Security.SecurityElement.Escape(value) ?? string.Empty;
        return Encoding.UTF8.GetBytes(
            "<?xml version="1.0" encoding="UTF-8"?><llsd><string>" +
            escaped +
            "</string></llsd>");
    }
}
