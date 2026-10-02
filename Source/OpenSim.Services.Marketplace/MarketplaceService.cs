using OpenMetaverse;

namespace OpenSim.Services.Marketplace;

/// <summary>
/// Initial Marketplace service boundary. Persistence, money and inventory adapters are
/// deliberately supplied behind interfaces in later implementation phases.
/// </summary>
public sealed class MarketplaceService : IMarketplaceService
{
    private readonly Dictionary<UUID, MarketplaceMerchant> _merchants = new();
    private readonly Dictionary<UUID, MarketplaceStore> _stores = new();
    private readonly Dictionary<UUID, MarketplaceListing> _listings = new();

    public MarketplaceMerchant? GetMerchant(UUID merchantId) =>
        _merchants.TryGetValue(merchantId, out var merchant) ? merchant : null;

    public MarketplaceStore? GetStore(UUID storeId) =>
        _stores.TryGetValue(storeId, out var store) ? store : null;

    public MarketplaceListing? GetListing(UUID listingId) =>
        _listings.TryGetValue(listingId, out var listing) ? listing : null;

    public MarketplaceListing CreateListing(
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
        bool isDemo)
    {
        if (price < 0)
            throw new ArgumentOutOfRangeException(nameof(price));

        if (!isUnlimited && quantity < 1)
            throw new ArgumentOutOfRangeException(nameof(quantity));

        var listing = new MarketplaceListing(
            UUID.Random(),
            merchantId,
            storeId,
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
        return listing;
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
        var orderId = UUID.Random();

        // Payment, inventory delivery and durable order persistence are intentionally
        // not performed in this in-memory foundation. The production implementation
        // will execute these through explicit adapters and an idempotent order workflow.
        return new MarketplacePurchaseResult(
            orderId,
            MarketplaceOrderStatus.PendingPayment,
            total,
            string.Empty,
            null);
    }
}
