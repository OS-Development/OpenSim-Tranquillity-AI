using OpenMetaverse;

namespace OpenSim.Services.Marketplace;

/// <summary>
/// Initial Marketplace service boundary. Persistence, money and inventory adapters are
/// deliberately supplied behind interfaces in later implementation phases.
/// </summary>
public sealed class MarketplaceService : IMarketplaceService
{
    private readonly object _sync = new();
    private readonly Dictionary<UUID, MarketplaceMerchant> _merchants = new();
    private readonly Dictionary<UUID, MarketplaceStore> _stores = new();
    private readonly Dictionary<UUID, MarketplaceListing> _listings = new();
    private readonly Dictionary<int, UUID> _marketplaceIds = new();
    private int _nextMarketplaceId = 1;

    public MarketplaceMerchant? GetMerchant(UUID merchantId) =>
        _merchants.TryGetValue(merchantId, out var merchant) ? merchant : null;

    public MarketplaceMerchant GetOrCreateMerchant(UUID merchantId)
    {
        lock (_sync)
        {
            if (_merchants.TryGetValue(merchantId, out var merchant))
                return merchant;

            merchant = new MarketplaceMerchant(
                merchantId,
                "Marketplace Store",
                merchantId.ToString(),
                string.Empty,
                true);

            _merchants.Add(merchantId, merchant);
            return merchant;
        }
    }

    public MarketplaceStore? GetStore(UUID storeId) =>
        _stores.TryGetValue(storeId, out var store) ? store : null;

    public MarketplaceStore GetOrCreateStore(UUID merchantId)
    {
        lock (_sync)
        {
            var existing = _stores.Values.FirstOrDefault(x => x.MerchantId == merchantId);
            if (existing != null)
                return existing;

            var store = new MarketplaceStore(
                UUID.Random(),
                merchantId,
                "Marketplace Store",
                merchantId.ToString(),
                string.Empty,
                true);

            _stores.Add(store.StoreId, store);
            return store;
        }
    }

    public MarketplaceListing? GetListing(UUID listingId) =>
        _listings.TryGetValue(listingId, out var listing) ? listing : null;

    public MarketplaceListing? GetListingByMarketplaceId(int marketplaceId)
    {
        lock (_sync)
        {
            return _marketplaceIds.TryGetValue(marketplaceId, out var listingId)
                ? GetListing(listingId)
                : null;
        }
    }

    public IReadOnlyCollection<MarketplaceListing> GetListings(UUID merchantId)
    {
        lock (_sync)
            return _listings.Values.Where(x => x.MerchantId == merchantId).ToArray();
    }

    public MarketplaceListing CreateListing(
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
        bool isDemo)
    {
        if (price < 0)
            throw new ArgumentOutOfRangeException(nameof(price));

        if (!isUnlimited && quantity < 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));

        lock (_sync)
        {
            var marketplaceId = _nextMarketplaceId++;
            var listing = new MarketplaceListing(
                UUID.Random(),
                marketplaceId,
                merchantId,
                storeId,
                listingFolderId,
                versionFolderId,
                inventoryItemId,
                assetId,
                name,
                description,
                category,
                tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                price,
                quantity,
                isUnlimited,
                isDemo,
                MarketplaceListingStatus.Draft);

            _listings.Add(listing.ListingId, listing);
            _marketplaceIds.Add(marketplaceId, listing.ListingId);
            return listing;
        }
    }

    public MarketplaceListing UpdateListing(
        int marketplaceId,
        UUID merchantId,
        UUID listingFolderId,
        UUID versionFolderId,
        bool isListed,
        int countOnHand)
    {
        lock (_sync)
        {
            var listing = GetListingByMarketplaceId(marketplaceId)
                ?? throw new InvalidOperationException("Marketplace listing was not found.");

            if (listing.MerchantId != merchantId)
                throw new UnauthorizedAccessException("Marketplace listing belongs to another merchant.");

            var status = isListed
                ? MarketplaceListingStatus.Active
                : MarketplaceListingStatus.Draft;

            var updated = listing with
            {
                ListingFolderId = listingFolderId,
                VersionFolderId = versionFolderId,
                Quantity = countOnHand,
                Status = status
            };

            _listings[listing.ListingId] = updated;
            return updated;
        }
    }

    public bool DeleteListing(int marketplaceId, UUID merchantId)
    {
        lock (_sync)
        {
            if (!_marketplaceIds.TryGetValue(marketplaceId, out var listingId) ||
                !_listings.TryGetValue(listingId, out var listing))
                return false;

            if (listing.MerchantId != merchantId)
                throw new UnauthorizedAccessException("Marketplace listing belongs to another merchant.");

            _listings.Remove(listingId);
            _marketplaceIds.Remove(marketplaceId);
            return true;
        }
    }

    public MarketplacePurchaseResult Purchase(MarketplacePurchaseRequest request)
    {
        if (request.Quantity < 1)
            throw new ArgumentOutOfRangeException(nameof(request.Quantity));

        if (!_listings.TryGetValue(request.ListingId, out var listing))
            throw new InvalidOperationException("Marketplace listing was not found.");

        if (listing.Status != MarketplaceListingStatus.Active)
            throw new InvalidOperationException("Marketplace listing is not available.");

        if (!listing.IsUnlimited && request.Quantity > listing.Quantity)
            throw new InvalidOperationException("Requested quantity is not available.");

        var total = checked(listing.Price * request.Quantity);

        return new MarketplacePurchaseResult(
            UUID.Random(),
            MarketplaceOrderStatus.PendingPayment,
            total,
            string.Empty,
            null);
    }
}
