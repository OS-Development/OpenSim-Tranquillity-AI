using OpenMetaverse;

namespace OpenSim.Services.Marketplace;

/// <summary>
/// Marketplace domain service. When an IMarketplaceDataPlugin is supplied, all
/// merchant/store/listing/order state is durable; otherwise the service retains
/// the in-memory fallback used by lightweight standalone deployments/tests.
/// </summary>
public sealed class MarketplaceService : IMarketplaceService
{
    private readonly object _sync = new();
    private readonly IMarketplaceDataPlugin? _data;
    private readonly Dictionary<UUID, MarketplaceMerchant> _merchants = new();
    private readonly Dictionary<UUID, MarketplaceStore> _stores = new();
    private readonly Dictionary<UUID, MarketplaceListing> _listings = new();
    private readonly Dictionary<int, UUID> _marketplaceIds = new();
    private int _nextMarketplaceId = 1;

    public MarketplaceService(IMarketplaceDataPlugin? data = null) => _data = data;

    public MarketplaceMerchant? GetMerchant(UUID merchantId)
        => _data?.GetMerchant(merchantId) ?? (_merchants.TryGetValue(merchantId, out var m) ? m : null);

    public MarketplaceMerchant GetOrCreateMerchant(UUID merchantId)
    {
        lock (_sync)
        {
            var existing = GetMerchant(merchantId);
            if (existing != null)
                return existing;

            var merchant = new MarketplaceMerchant(
                merchantId, "Marketplace Store", merchantId.ToString(), string.Empty, true);

            if (_data != null)
                _data.StoreMerchant(merchant);
            else
                _merchants[merchantId] = merchant;

            return merchant;
        }
    }

    public MarketplaceStore? GetStore(UUID storeId)
        => _data?.GetStore(storeId) ?? (_stores.TryGetValue(storeId, out var s) ? s : null);

    public MarketplaceStore GetOrCreateStore(UUID merchantId)
    {
        lock (_sync)
        {
            var existing = _data?.GetStoreByMerchant(merchantId)
                ?? _stores.Values.FirstOrDefault(x => x.MerchantId == merchantId);
            if (existing != null)
                return existing;

            var store = new MarketplaceStore(
                UUID.Random(), merchantId, "Marketplace Store",
                merchantId.ToString(), string.Empty, true);

            if (_data != null)
                _data.StoreStore(store);
            else
                _stores[store.StoreId] = store;

            return store;
        }
    }

    public MarketplaceListing? GetListing(UUID listingId)
        => _data?.GetListing(listingId) ?? (_listings.TryGetValue(listingId, out var l) ? l : null);

    public MarketplaceListing? GetListingByMarketplaceId(int marketplaceId)
        => _data?.GetListingByMarketplaceId(marketplaceId)
            ?? (_marketplaceIds.TryGetValue(marketplaceId, out var id) ? GetListing(id) : null);

    public IReadOnlyCollection<MarketplaceListing> GetListings(UUID merchantId)
        => _data?.GetListings(merchantId)
            ?? _listings.Values.Where(x => x.MerchantId == merchantId).ToArray();

    public MarketplaceListing CreateListing(
        UUID merchantId, UUID storeId, UUID listingFolderId, UUID versionFolderId,
        UUID inventoryItemId, UUID assetId, string name, string description,
        string category, IEnumerable<string> tags, int price, int quantity,
        bool isUnlimited, bool isDemo)
    {
        if (price < 0)
            throw new ArgumentOutOfRangeException(nameof(price));
        if (!isUnlimited && quantity < 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));

        lock (_sync)
        {
            var listing = new MarketplaceListing(
                UUID.Random(), _nextMarketplaceId++, merchantId, storeId,
                listingFolderId, versionFolderId, inventoryItemId, assetId,
                name, description, category,
                tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                price, quantity, isUnlimited, isDemo,
                MarketplaceListingStatus.Draft);

            if (_data != null)
                return _data.StoreListing(listing);

            _listings[listing.ListingId] = listing;
            _marketplaceIds[listing.MarketplaceId] = listing.ListingId;
            return listing;
        }
    }

    public MarketplaceListing UpdateListing(
        int marketplaceId, UUID merchantId, UUID listingFolderId,
        UUID versionFolderId, bool isListed, int countOnHand)
    {
        lock (_sync)
        {
            var listing = GetListingByMarketplaceId(marketplaceId)
                ?? throw new InvalidOperationException("Marketplace listing was not found.");

            if (listing.MerchantId != merchantId)
                throw new UnauthorizedAccessException(
                    "Marketplace listing belongs to another merchant.");

            var updated = listing with
            {
                ListingFolderId = listingFolderId,
                VersionFolderId = versionFolderId,
                Quantity = countOnHand,
                Status = isListed
                    ? MarketplaceListingStatus.Active
                    : MarketplaceListingStatus.Draft
            };

            if (_data != null)
                return _data.UpdateListing(updated);

            _listings[listing.ListingId] = updated;
            return updated;
        }
    }

    public bool DeleteListing(int marketplaceId, UUID merchantId)
    {
        lock (_sync)
        {
            if (_data != null)
                return _data.DeleteListing(marketplaceId, merchantId);

            if (!_marketplaceIds.TryGetValue(marketplaceId, out var listingId) ||
                !_listings.TryGetValue(listingId, out var listing))
                return false;

            if (listing.MerchantId != merchantId)
                throw new UnauthorizedAccessException(
                    "Marketplace listing belongs to another merchant.");

            _listings.Remove(listingId);
            _marketplaceIds.Remove(marketplaceId);
            return true;
        }
    }

    public MarketplacePurchaseResult Purchase(MarketplacePurchaseRequest request)
    {
        if (request.Quantity < 1)
            throw new ArgumentOutOfRangeException(nameof(request.Quantity));

        lock (_sync)
        {
            if (_data != null)
            {
                var prior = _data.GetOrderByIdempotency(request.BuyerId, request.IdempotencyKey);
                if (prior != null)
                    return new MarketplacePurchaseResult(
                        prior.OrderId, prior.Status, prior.TotalPrice,
                        prior.PaymentTransactionId, null);
            }

            var listing = GetListing(request.ListingId)
                ?? throw new InvalidOperationException("Marketplace listing was not found.");

            if (listing.Status != MarketplaceListingStatus.Active)
                throw new InvalidOperationException("Marketplace listing is not available.");

            if (!listing.IsUnlimited && request.Quantity > listing.Quantity)
                throw new InvalidOperationException("Requested quantity is not available.");

            var total = checked(listing.Price * request.Quantity);
            var order = new MarketplaceOrder(
                UUID.Random(), request.BuyerId, listing.MerchantId, listing.ListingId,
                request.Quantity, listing.Price, total,
                MarketplaceOrderStatus.PendingPayment, string.Empty, null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

            _data?.StoreOrder(order, request.IdempotencyKey);

            return new MarketplacePurchaseResult(
                order.OrderId, order.Status, total, string.Empty, null);
        }
    }
}
