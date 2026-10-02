using OpenMetaverse;

namespace OpenSim.Services.Marketplace;

public enum MarketplaceListingStatus
{
    Draft,
    Active,
    Suspended,
    SoldOut,
    Archived
}

public enum MarketplaceOrderStatus
{
    PendingPayment,
    Paid,
    Delivering,
    Delivered,
    DeliveryFailed,
    Refunded,
    Cancelled
}

public sealed record MarketplaceMerchant(
    UUID MerchantId,
    string StoreName,
    string StoreSlug,
    string Description,
    bool IsActive);

public sealed record MarketplaceStore(
    UUID StoreId,
    UUID MerchantId,
    string Name,
    string Slug,
    string Description,
    bool IsActive);

public sealed record MarketplaceListing(
    UUID ListingId,
    UUID MerchantId,
    UUID StoreId,
    UUID InventoryItemId,
    UUID AssetId,
    string Name,
    string Description,
    string Category,
    string[] Tags,
    int Price,
    int Quantity,
    bool IsUnlimited,
    bool IsDemo,
    MarketplaceListingStatus Status);

public sealed record MarketplaceOrder(
    UUID OrderId,
    UUID BuyerId,
    UUID MerchantId,
    UUID ListingId,
    int Quantity,
    int UnitPrice,
    int TotalPrice,
    MarketplaceOrderStatus Status,
    string PaymentTransactionId,
    string? DeliveryFolderId,
    string? DeliveryItemId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MarketplacePurchaseRequest(
    UUID BuyerId,
    UUID ListingId,
    int Quantity,
    string IdempotencyKey);

public sealed record MarketplacePurchaseResult(
    UUID OrderId,
    MarketplaceOrderStatus Status,
    int TotalPrice,
    string PaymentTransactionId,
    string? FailureReason);
