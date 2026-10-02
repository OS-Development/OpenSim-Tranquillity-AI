using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Services.Marketplace;

public interface IMarketplaceDataPlugin : IPlugin
{
    void Initialise(string connect);
    MarketplaceMerchant? GetMerchant(UUID merchantId);
    void StoreMerchant(MarketplaceMerchant merchant);
    MarketplaceStore? GetStore(UUID storeId);
    MarketplaceStore? GetStoreByMerchant(UUID merchantId);
    void StoreStore(MarketplaceStore store);
    MarketplaceListing? GetListing(UUID listingId);
    MarketplaceListing? GetListingByMarketplaceId(int marketplaceId);
    IReadOnlyCollection<MarketplaceListing> GetListings(UUID merchantId);
    MarketplaceListing StoreListing(MarketplaceListing listing);
    MarketplaceListing UpdateListing(MarketplaceListing listing);
    bool DeleteListing(int marketplaceId, UUID merchantId);
    MarketplaceOrder? GetOrder(UUID orderId);
    MarketplaceOrder? GetOrderByIdempotency(UUID buyerId, string idempotencyKey);
    void StoreOrder(MarketplaceOrder order, string idempotencyKey);
}
