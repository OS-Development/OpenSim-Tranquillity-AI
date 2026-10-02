using System.Text.Json;
using System.Text.Json.Serialization;
using OpenMetaverse;

namespace OpenSim.Services.Marketplace;

/// <summary>
/// Firestorm/SL Marketplace-compatible route and payload definitions.
/// This layer contains protocol contracts only; CAPS registration is performed by the
/// simulator integration once an agent's Caps instance is available.
/// </summary>
public static class FirestormMarketplaceRoutes
{
    public const string Merchant = "/merchant";
    public const string Listings = "/listings";
    public const string Listing = "/listing";
    public const string AssociateInventory = "/associate_inventory";
    public const string InventoryImport = "/api/1/{agent_id}/inventory/import/";
}

public sealed class FirestormMarketplaceListing
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("is_listed")]
    public bool IsListed { get; init; }

    [JsonPropertyName("edit_url")]
    public string EditUrl { get; init; } = string.Empty;

    [JsonPropertyName("inventory_info")]
    public FirestormInventoryInfo InventoryInfo { get; init; } = new();
}

public sealed class FirestormInventoryInfo
{
    [JsonPropertyName("listing_folder_id")]
    public UUID ListingFolderId { get; init; }

    [JsonPropertyName("version_folder_id")]
    public UUID VersionFolderId { get; init; }

    [JsonPropertyName("count_on_hand")]
    public int CountOnHand { get; init; }
}

public sealed class FirestormListingRequest
{
    [JsonPropertyName("listing")]
    public FirestormListingData Listing { get; init; } = new();
}

public sealed class FirestormListingData
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("is_listed")]
    public bool IsListed { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("inventory_info")]
    public FirestormInventoryInfo InventoryInfo { get; init; } = new();
}

public sealed class FirestormListingUpdateRequest
{
    [JsonPropertyName("listing")]
    public FirestormListingData Listing { get; init; } = new();
}

public static class FirestormMarketplaceJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options);
}
