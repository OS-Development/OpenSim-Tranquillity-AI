using OpenMetaverse;

namespace OpenSim.Services.Marketplace;

public interface IMarketplaceService
{
    MarketplaceMerchant? GetMerchant(UUID merchantId);
    MarketplaceMerchant GetOrCreateMerchant(UUID merchantId);
    MarketplaceStore? GetStore(UUID storeId);
    MarketplaceStore GetOrCreateStore(UUID merchantId);

    MarketplaceListing? GetListing(UUID listingId);
    MarketplaceListing? GetListingByMarketplaceId(int marketplaceId);
    IReadOnlyCollection<MarketplaceListing> GetListings(UUID merchantId);

    MarketplaceListing CreateListing(
        UUID merchantId,
        UUID storeId,
        UUID listingFolderId,
        UUID versionFolderId,
        UUID inventoryItemId,
        UUID assetId,
        string name,
        string description,
        string category,
        IEnumerable<string> tags,
        int price,
        int quantity,
        bool isUnlimited,
        bool isDemo);

    MarketplaceListing UpdateListing(
        int marketplaceId,
        UUID merchantId,
        UUID listingFolderId,
        UUID versionFolderId,
        bool isListed,
        int countOnHand);

    bool DeleteListing(int marketplaceId, UUID merchantId);

    MarketplacePurchaseResult Purchase(MarketplacePurchaseRequest request);
}
