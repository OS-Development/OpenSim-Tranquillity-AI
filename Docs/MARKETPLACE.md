# Tranquillity Marketplace

This document defines the initial architecture and implementation scope for the Tranquillity Marketplace.

## Scope

The Marketplace will provide a Second Life Marketplace-style experience:

- Web storefront and merchant management.
- In-world access for residents and merchants.
- Merchant stores.
- Listings backed by OpenSim inventory and assets.
- Purchases using the existing Tranquillity/OpenSim money implementation.
- Delivery directly into the buyer's inventory.
- Merchant settlement through the existing money service.
- Search, categories, maturity, permissions, pricing, stock/limited-quantity listings, demos, and merchant/store pages.
- Marketplace orders and transaction history.
- Support for all current Second Life Marketplace top-level product categories, while keeping the catalog taxonomy configurable rather than hard-coded to one snapshot.

The current Second Life Marketplace exposes categories including Animals, Animated Objects, Animations, Apparel, Art, Audio and Video, Avatar Accessories, Avatar Appearance, Avatar Components, Breedables, Building and Object Components, Buildings and Other Structures, Business, Celebrations, Complete Avatars, Furry, Gachas, Gadgets, Home and Garden, Miscellaneous, Real Estate, Recreation and Entertainment, Scripts, Services, Used Items, Vehicles, Weapons, and Everything Else.

## Architecture

The Marketplace is implemented as a separate service boundary:

```
Web UI / Viewer
      |
      v
Marketplace HTTP API
      |
      +--> Marketplace repository/database
      |
      +--> Inventory service
      |
      +--> Asset service
      |
      +--> User/account services
      |
      +--> Existing money service
      |
      v
Buyer inventory / merchant settlement
```

Marketplace-specific state must not be stored by modifying inventory item semantics. A listing references an inventory item and its delivery metadata; the normal inventory and asset services remain authoritative for inventory/assets.

## Core entities

### Merchant

- Merchant/avatar UUID.
- Store name and description.
- Store slug.
- Store visibility/status.
- Store image/logo references.
- Marketplace settings.

### Listing

- Listing UUID.
- Merchant UUID.
- Store UUID.
- Source inventory item UUID.
- Source asset UUID when applicable.
- Product name and description.
- Category and subcategory.
- Tags/search metadata.
- Price in the existing world currency.
- Quantity/limited-stock state.
- Copy/modify/transfer permission snapshot.
- Maturity rating.
- Demo flag.
- Listing status.
- Marketplace thumbnail/image references.
- Creation/update timestamps.

### Order

- Order UUID.
- Buyer UUID.
- Merchant UUID.
- Listing UUID.
- Quantity.
- Unit price.
- Total price.
- Currency.
- Payment transaction reference.
- Delivery status.
- Delivery inventory folder/item references.
- Timestamps and failure information.

### Store

A merchant can expose one or more storefronts. Store pages contain listings but do not own inventory assets.

## Purchase transaction

Purchasing must be treated as an atomic workflow:

1. Validate listing is active and purchasable.
2. Validate quantity and buyer eligibility.
3. Validate the merchant still owns/controls the source inventory item.
4. Validate delivery permissions.
5. Charge the buyer through the existing money implementation.
6. Create/copy the purchased inventory content through the inventory service.
7. Record the order and delivery result.
8. Settle the merchant according to Marketplace configuration.
9. If delivery fails after payment, persist a recoverable failed-delivery state; never silently discard the payment.

The implementation should use an idempotency key/order token so retries cannot double-charge the buyer or deliver duplicate purchases.

## Web and in-world API

The same Marketplace service API will back both interfaces.

Initial API areas:

- merchant registration/status
- store management
- listing CRUD
- inventory-to-listing preparation
- catalog search
- listing detail
- cart/order creation
- checkout
- order history
- delivery status
- merchant sales
- merchant balance/settlement history
- store pages

Viewer-facing integration can expose capability endpoints and/or Marketplace URLs without coupling viewer UI concerns into the core service.

## Database strategy

Marketplace data is independent from inventory persistence and should have a dedicated schema/repository abstraction.

The first implementation should support the repository's existing relational database providers rather than introducing a second database technology solely for Marketplace state.

Transactions involving money, orders, and delivery need durable transaction identifiers and explicit state transitions.

## Security

- A seller may only list inventory they own/control.
- Listing creation must validate inventory permissions.
- Buyers must never be able to select another resident's inventory item as their delivery source.
- Administrative operations require explicit authorization.
- HTTP endpoints must validate authenticated avatar identity.
- Marketplace HTML/API output must encode user-controlled text.
- Purchase operations must be idempotent.
- Payment references must be persisted for reconciliation.
- Delivery retries must be safe.
- Merchant ownership changes must invalidate or suspend affected listings as appropriate.

## Catalog

The catalog should be data-driven. We will seed it with the current Second Life Marketplace top-level categories and allow administrators to add, rename, disable, or extend categories without recompiling the server.

The implementation must not assume that an inventory asset type maps one-to-one to a Marketplace category. For example, an object can represent a vehicle, building, furniture item, vendor, scripted system, or another product.

## Delivery model

Marketplace delivery should preserve the source item's OpenSim permissions and asset references according to the permissions allowed by the seller.

The first delivery implementation will target normal inventory items and object/folder deliveries. Additional delivery adapters can cover special cases such as services or real-estate listings where no ordinary inventory copy is appropriate.

## Implementation phases

### Phase 1 - Service foundation

- Marketplace service project.
- Configuration section.
- Repository/data contracts.
- Merchant/store/listing/order domain models.
- HTTP API contracts.
- Authentication/authorization boundary.
- Catalog seed data.
- Automated tests for state transitions.

### Phase 2 - Inventory listing

- Merchant inventory browsing.
- Listing creation from inventory.
- Permission validation.
- Listing editing/deactivation.
- Merchant store pages.

### Phase 3 - Checkout and delivery

- Existing money-service integration.
- Idempotent checkout.
- Inventory delivery.
- Order history.
- Failed-delivery recovery.
- Merchant settlement.

### Phase 4 - Web Marketplace

- Search.
- Category navigation.
- Listing pages.
- Store pages.
- Cart.
- Checkout.
- Merchant dashboard.

### Phase 5 - In-world Marketplace

- Viewer/capability integration.
- In-world listing management.
- In-world search/browse.
- Purchase and delivery flows.
- In-world merchant tools.

### Phase 6 - Production hardening

- Database migrations.
- Concurrency tests.
- Money/order reconciliation.
- Audit logging.
- Rate limiting.
- Search indexing.
- Caching.
- Backup/recovery tests.

## Current branch

All Marketplace work is isolated on:

`feature/marketplace`

The `develop` branch is not used for Marketplace implementation commits.
