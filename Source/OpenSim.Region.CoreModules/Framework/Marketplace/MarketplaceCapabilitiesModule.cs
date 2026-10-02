using System.Net;
using System.Text;
using System.Text.Json;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Capabilities;
using OpenSimCaps = OpenSim.Framework.Capabilities.Caps;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Marketplace;
using OpenSim.Services.Interfaces;
using OpenSim.Server.Base;

namespace OpenSim.Region.CoreModules.Framework.Marketplace;

/// <summary>
/// Exposes the Firestorm/Second Life Marketplace DirectDelivery protocol to logged-in agents.
/// Durable marketplace state and checkout are intentionally kept in the Marketplace service.
/// </summary>
public sealed class MarketplaceCapabilitiesModule : INonSharedRegionModule
{
    private IMarketplaceService _marketplace = new MarketplaceService();
    private IMarketplaceDataPlugin? _data;

    private Scene? _scene;
    private IInventoryService? _inventory;

    public string Name => "Marketplace Capabilities Module";
    public Type ReplaceableInterface => null;

    public void Initialise(Nini.Config.IConfigSource source)
    {
        var config = source.Configs["Marketplace"];
        if (config == null)
            return;

        string provider = config.GetString("StorageProvider", string.Empty);
        if (string.IsNullOrWhiteSpace(provider))
            return;

        string connectionString = config.GetString("ConnectionString", string.Empty);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Marketplace StorageProvider is configured but ConnectionString is empty.");

        _data = ServerUtils.LoadPlugin<IMarketplaceDataPlugin>(
            provider,
            Array.Empty<object>());

        if (_data == null)
            throw new InvalidOperationException($"Unable to load Marketplace storage provider '{provider}'.");

        _data.Initialise(connectionString);
        _marketplace = new MarketplaceService(_data);
    }

    public void AddRegion(Scene scene)
    {
        _scene = scene;
        _inventory = scene.RequestModuleInterface<IInventoryService>();
        scene.EventManager.OnRegisterCaps += OnRegisterCaps;
    }

    public void RegionLoaded(Scene scene)
    {
    }

    public void RemoveRegion(Scene scene)
    {
        scene.EventManager.OnRegisterCaps -= OnRegisterCaps;
        _scene = null;
        _inventory = null;
    }

    public void PostInitialise()
    {
    }

    public void Close()
    {
    }

    private void OnRegisterCaps(UUID agentId, OpenSimCaps caps)
    {
        var path = caps.CapsObjectPath + "/DirectDelivery";
        var handler = new SimpleStreamHandler(path, (request, response) =>
            HandleMarketplaceRequest(agentId, request, response));

        caps.RegisterSimpleHandler("DirectDelivery", handler, true, true);
    }

    private void HandleMarketplaceRequest(
        UUID agentId,
        IOSHttpRequest request,
        IOSHttpResponse response)
    {
        response.ContentType = "application/json";
        response.ContentEncoding = Encoding.UTF8;

        var basePath = request.UriPath;
        var directDeliveryIndex = basePath.IndexOf("/DirectDelivery", StringComparison.Ordinal);
        if (directDeliveryIndex < 0)
        {
            WriteError(response, HttpStatusCode.NotFound, "DirectDelivery capability path not found.");
            return;
        }

        var route = basePath[(directDeliveryIndex + "/DirectDelivery".Length)..];
        if (string.IsNullOrEmpty(route))
            route = "/";

        try
        {
            switch (request.HttpMethod.ToUpperInvariant())
            {
                case "GET":
                    HandleGet(agentId, route, response);
                    break;

                case "POST":
                    HandlePost(agentId, route, request, response);
                    break;

                case "PUT":
                    HandlePut(agentId, route, request, response);
                    break;

                case "DELETE":
                    HandleDelete(agentId, route, response);
                    break;

                default:
                    WriteError(response, HttpStatusCode.MethodNotAllowed, "HTTP method is not supported.");
                    break;
            }
        }
        catch (UnauthorizedAccessException)
        {
            WriteError(response, HttpStatusCode.Forbidden, "The listing does not belong to this merchant.");
        }
        catch (ArgumentException ex)
        {
            WriteError(response, HttpStatusCode.BadRequest, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            WriteError(response, HttpStatusCode.NotFound, ex.Message);
        }
        catch (JsonException ex)
        {
            WriteError(response, HttpStatusCode.BadRequest, ex.Message);
        }
    }

    private void HandleGet(UUID agentId, string route, IOSHttpResponse response)
    {
        if (route.Equals("/merchant", StringComparison.OrdinalIgnoreCase))
        {
            _marketplace.GetOrCreateMerchant(agentId);
            WriteJson(response, new { merchant = true });
            return;
        }

        if (route.Equals("/listings", StringComparison.OrdinalIgnoreCase))
        {
            WriteListings(response, _marketplace.GetListings(agentId));
            return;
        }

        if (TryGetListingId(route, "/listing/", out var listingId))
        {
            var listing = s_marketplace.GetListingByMarketplaceId(listingId);
            if (listing == null || listing.MerchantId != agentId)
            {
                WriteError(response, HttpStatusCode.NotFound, "Marketplace listing was not found.");
                return;
            }

            WriteListings(response, new[] { listing });
            return;
        }

        WriteError(response, HttpStatusCode.NotFound, "Marketplace route was not found.");
    }

    private void HandlePost(
        UUID agentId,
        string route,
        IOSHttpRequest request,
        IOSHttpResponse response)
    {
        if (!route.Equals("/listings", StringComparison.OrdinalIgnoreCase))
        {
            WriteError(response, HttpStatusCode.NotFound, "Marketplace route was not found.");
            return;
        }

        var payload = ReadJson<FirestormListingRequest>(request);
        var listing = payload?.Listing
            ?? throw new ArgumentException("The listing payload is required.");

        var store = s_marketplace.GetOrCreateStore(agentId);
        var created = s_marketplace.CreateListing(
            agentId,
            store.StoreId,
            listing.InventoryInfo.ListingFolderId,
            listing.InventoryInfo.VersionFolderId,
            UUID.Zero,
            UUID.Zero,
            listing.Name,
            string.Empty,
            string.Empty,
            Array.Empty<string>(),
            0,
            listing.InventoryInfo.CountOnHand,
            listing.InventoryInfo.VersionFolderId == UUID.Zero,
            false);

        WriteListings(response, new[] { created });
        response.StatusCode = (int)HttpStatusCode.Created;
    }

    private void HandlePut(
        UUID agentId,
        string route,
        IOSHttpRequest request,
        IOSHttpResponse response)
    {
        if (!TryGetListingId(route, "/listing/", out var listingId) &&
            !TryGetListingId(route, "/associate_inventory/", out listingId))
        {
            WriteError(response, HttpStatusCode.NotFound, "Marketplace route was not found.");
            return;
        }

        var payload = ReadJson<FirestormListingUpdateRequest>(request);
        var listing = payload?.Listing
            ?? throw new ArgumentException("The listing payload is required.");

        var updated = s_marketplace.UpdateListing(
            listing.Id > 0 ? listing.Id : listingId,
            agentId,
            listing.InventoryInfo.ListingFolderId,
            listing.InventoryInfo.VersionFolderId,
            listing.IsListed,
            listing.InventoryInfo.CountOnHand);

        WriteListings(response, new[] { updated });
    }

    private void HandleDelete(UUID agentId, string route, IOSHttpResponse response)
    {
        if (!TryGetListingId(route, "/listing/", out var listingId))
        {
            WriteError(response, HttpStatusCode.NotFound, "Marketplace route was not found.");
            return;
        }

        if (!s_marketplace.DeleteListing(listingId, agentId))
        {
            WriteError(response, HttpStatusCode.NotFound, "Marketplace listing was not found.");
            return;
        }

        WriteJson(response, new { listings = Array.Empty<object>() });
    }

    private void WriteListings(
        IOSHttpResponse response,
        IEnumerable<MarketplaceListing> listings)
    {
        var result = new
        {
            listings = listings.Select(listing => new
            {
                id = listing.MarketplaceId,
                is_listed = listing.Status == MarketplaceListingStatus.Active,
                edit_url = string.Empty,
                inventory_info = new
                {
                    listing_folder_id = listing.ListingFolderId,
                    version_folder_id = listing.VersionFolderId,
                    count_on_hand = listing.Quantity
                }
            }).ToArray()
        };

        WriteJson(response, result);
    }

    private static T? ReadJson<T>(IOSHttpRequest request)
    {
        using var reader = new StreamReader(
            request.InputStream,
            request.ContentEncoding ?? Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);

        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<T>(json, FirestormMarketplaceJson.Options);
    }

    private static bool TryGetListingId(string route, string prefix, out int listingId)
    {
        listingId = 0;
        if (!route.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var value = route[prefix.Length..].Trim('/');
        return int.TryParse(value, out listingId) && listingId > 0;
    }

    private static void WriteJson(IOSHttpResponse response, object value)
    {
        var bytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(value, FirestormMarketplaceJson.Options));

        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentLength = bytes.Length;
        response.ContentLength64 = bytes.Length;
        response.Body.Write(bytes, 0, bytes.Length);
    }

    private static void WriteError(
        IOSHttpResponse response,
        HttpStatusCode status,
        string message)
    {
        var bytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(
                new { error_description = message },
                FirestormMarketplaceJson.Options));

        response.StatusCode = (int)status;
        response.ContentLength = bytes.Length;
        response.ContentLength64 = bytes.Length;
        response.Body.Write(bytes, 0, bytes.Length);
    }
}
