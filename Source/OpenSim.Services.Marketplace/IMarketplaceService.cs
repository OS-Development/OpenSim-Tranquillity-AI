using OpenMetaverse;

namespace OpenSim.Services.Marketplace;

public interface IMarketplaceService
{
    MarketplaceMerchant? GetMerchant(UUID merchantId);
    MarketplaceStore? GetStore(UUID storeId);
    MarketplaceListing? GetListing(UUID listingId);

    MarketplaceListing CreateListing(
        UUID merchantId,
        UUID storeId,
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

    MarketplacePurchaseResult Purchase(MarketplacePurchaseRequest request);
}
