using OpenMetaverse;

namespace OpenSim.Services.Marketplace;

/// <summary>
/// Persistent Marketplace representation of the inventory-folder model used by
/// Second Life-compatible viewers such as Firestorm.
/// </summary>
public sealed record MarketplaceListingRecord(
    int MarketplaceId,
    UUID MerchantId,
    UUID ListingFolderId,
    UUID VersionFolderId,
    string Name,
    bool IsListed,
    int CountOnHand,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
