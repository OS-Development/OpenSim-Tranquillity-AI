using System.Data.Common;
using System.Text.Json;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Services.Marketplace;

/// <summary>
/// Provider-neutral ADO.NET Marketplace store. Concrete database plugins only supply
/// a DbConnection; SQL uses common ADO.NET parameter syntax.
/// </summary>
public abstract class MarketplaceDbData : IMarketplaceDataPlugin
{
    private readonly object _sync = new();
    protected abstract DbConnection CreateConnection(string connectionString);
    protected string ConnectionString { get; private set; } = string.Empty;

    public abstract string Version { get; }
    public abstract string Name { get; }

    public void Initialise() => throw new PluginNotInitialisedException(Name);

    public void Initialise(string connect)
    {
        ConnectionString = connect ?? throw new ArgumentNullException(nameof(connect));
        using var db = CreateConnection(ConnectionString);
        db.Open();
        EnsureSchema(db);
    }

    public void Dispose() { }

    protected virtual void EnsureSchema(DbConnection db)
    {
        Execute(db, @"CREATE TABLE IF NOT EXISTS marketplace_merchants (
merchant_id VARCHAR(36) PRIMARY KEY, store_name VARCHAR(255) NOT NULL, store_slug VARCHAR(255) NOT NULL,
description TEXT NOT NULL, is_active INTEGER NOT NULL)");
        Execute(db, @"CREATE TABLE IF NOT EXISTS marketplace_stores (
store_id VARCHAR(36) PRIMARY KEY, merchant_id VARCHAR(36) NOT NULL, name VARCHAR(255) NOT NULL,
slug VARCHAR(255) NOT NULL, description TEXT NOT NULL, is_active INTEGER NOT NULL)");
        Execute(db, @"CREATE INDEX IF NOT EXISTS marketplace_stores_merchant ON marketplace_stores(merchant_id)");
        Execute(db, @"CREATE TABLE IF NOT EXISTS marketplace_listings (
listing_id VARCHAR(36) PRIMARY KEY, marketplace_id INTEGER NOT NULL UNIQUE, merchant_id VARCHAR(36) NOT NULL,
store_id VARCHAR(36) NOT NULL, listing_folder_id VARCHAR(36) NOT NULL, version_folder_id VARCHAR(36) NOT NULL,
inventory_item_id VARCHAR(36) NOT NULL, asset_id VARCHAR(36) NOT NULL, name VARCHAR(255) NOT NULL,
description TEXT NOT NULL, category VARCHAR(255) NOT NULL, tags TEXT NOT NULL, price INTEGER NOT NULL,
quantity INTEGER NOT NULL, is_unlimited INTEGER NOT NULL, is_demo INTEGER NOT NULL, status INTEGER NOT NULL)");
        Execute(db, @"CREATE INDEX IF NOT EXISTS marketplace_listings_merchant ON marketplace_listings(merchant_id)");
        Execute(db, @"CREATE TABLE IF NOT EXISTS marketplace_orders (
order_id VARCHAR(36) PRIMARY KEY, buyer_id VARCHAR(36) NOT NULL, merchant_id VARCHAR(36) NOT NULL,
listing_id VARCHAR(36) NOT NULL, quantity INTEGER NOT NULL, unit_price INTEGER NOT NULL, total_price INTEGER NOT NULL,
status INTEGER NOT NULL, payment_transaction_id VARCHAR(255) NOT NULL, delivery_folder_id VARCHAR(36),
delivery_item_id VARCHAR(36), idempotency_key VARCHAR(255) NOT NULL, created_at BIGINT NOT NULL, updated_at BIGINT NOT NULL)");
        Execute(db, @"CREATE UNIQUE INDEX IF NOT EXISTS marketplace_orders_idempotency
ON marketplace_orders(buyer_id, idempotency_key)");
    }

    private static void Execute(DbConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    protected static void Add(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    protected static string S(UUID id) => id.ToString();
    protected static UUID U(object value) => UUID.TryParse(Convert.ToString(value), out var id) ? id : UUID.Zero;
    protected static bool B(object value) => Convert.ToInt32(value) != 0;
    protected static DateTimeOffset D(object value) => DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(value));

    public MarketplaceMerchant? GetMerchant(UUID merchantId)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT * FROM marketplace_merchants WHERE merchant_id=@id");
        Add(cmd, "@id", S(merchantId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new MarketplaceMerchant(merchantId, Convert.ToString(r["store_name"])!, Convert.ToString(r["store_slug"])!, Convert.ToString(r["description"])!, B(r["is_active"])) : null;
    }

    public void StoreMerchant(MarketplaceMerchant m)
    {
        using var db = Open();
        using var cmd = Command(db, @"UPDATE marketplace_merchants SET store_name=@n,store_slug=@s,description=@d,is_active=@a WHERE merchant_id=@id");
        Add(cmd,"@id",S(m.MerchantId)); Add(cmd,"@n",m.StoreName); Add(cmd,"@s",m.StoreSlug); Add(cmd,"@d",m.Description); Add(cmd,"@a",m.IsActive?1:0);
        if (cmd.ExecuteNonQuery()==0) {
            cmd.CommandText=@"INSERT INTO marketplace_merchants(merchant_id,store_name,store_slug,description,is_active) VALUES(@id,@n,@s,@d,@a)";
            cmd.ExecuteNonQuery();
        }
    }

    public MarketplaceStore? GetStore(UUID storeId)
    {
        using var db=Open(); using var cmd=Command(db,"SELECT * FROM marketplace_stores WHERE store_id=@id"); Add(cmd,"@id",S(storeId));
        using var r=cmd.ExecuteReader(); return r.Read()?ReadStore(r):null;
    }

    public MarketplaceStore? GetStoreByMerchant(UUID merchantId)
    {
        using var db=Open(); using var cmd=Command(db,"SELECT * FROM marketplace_stores WHERE merchant_id=@id"); Add(cmd,"@id",S(merchantId));
        using var r=cmd.ExecuteReader(); return r.Read()?ReadStore(r):null;
    }

    public void StoreStore(MarketplaceStore s)
    {
        using var db=Open(); using var cmd=Command(db,@"UPDATE marketplace_stores SET merchant_id=@m,name=@n,slug=@s,description=@d,is_active=@a WHERE store_id=@id");
        Add(cmd,"@id",S(s.StoreId));Add(cmd,"@m",S(s.MerchantId));Add(cmd,"@n",s.Name);Add(cmd,"@s",s.Slug);Add(cmd,"@d",s.Description);Add(cmd,"@a",s.IsActive?1:0);
        if(cmd.ExecuteNonQuery()==0){cmd.CommandText=@"INSERT INTO marketplace_stores(store_id,merchant_id,name,slug,description,is_active) VALUES(@id,@m,@n,@s,@d,@a)";cmd.ExecuteNonQuery();}
    }

    public MarketplaceListing? GetListing(UUID id) { using var db=Open(); using var c=Command(db,"SELECT * FROM marketplace_listings WHERE listing_id=@id");Add(c,"@id",S(id));using var r=c.ExecuteReader();return r.Read()?ReadListing(r):null; }
    public MarketplaceListing? GetListingByMarketplaceId(int id) { using var db=Open(); using var c=Command(db,"SELECT * FROM marketplace_listings WHERE marketplace_id=@id");Add(c,"@id",id);using var r=c.ExecuteReader();return r.Read()?ReadListing(r):null; }
    public IReadOnlyCollection<MarketplaceListing> GetListings(UUID merchantId) { using var db=Open();using var c=Command(db,"SELECT * FROM marketplace_listings WHERE merchant_id=@m");Add(c,"@m",S(merchantId));using var r=c.ExecuteReader();var a=new List<MarketplaceListing>();while(r.Read())a.Add(ReadListing(r));return a; }

    public MarketplaceListing StoreListing(MarketplaceListing l)
    {
        lock(_sync) {
            using var db=Open();
            int id;
            using(var c=Command(db,"SELECT COALESCE(MAX(marketplace_id),0)+1 FROM marketplace_listings")) id=Convert.ToInt32(c.ExecuteScalar());
            l=l with { MarketplaceId=id };
            InsertListing(db,l); return l;
        }
    }

    private static void InsertListing(DbConnection db, MarketplaceListing l)
    {
        using var c=Command(db,@"INSERT INTO marketplace_listings
(listing_id,marketplace_id,merchant_id,store_id,listing_folder_id,version_folder_id,inventory_item_id,asset_id,name,description,category,tags,price,quantity,is_unlimited,is_demo,status)
VALUES(@id,@mid,@m,@s,@lf,@vf,@ii,@a,@n,@d,@c,@t,@p,@q,@u,@demo,@st)");
        Add(c,"@id",S(l.ListingId));Add(c,"@mid",l.MarketplaceId);Add(c,"@m",S(l.MerchantId));Add(c,"@s",S(l.StoreId));Add(c,"@lf",S(l.ListingFolderId));Add(c,"@vf",S(l.VersionFolderId));Add(c,"@ii",S(l.InventoryItemId));Add(c,"@a",S(l.AssetId));Add(c,"@n",l.Name);Add(c,"@d",l.Description);Add(c,"@c",l.Category);Add(c,"@t",JsonSerializer.Serialize(l.Tags));Add(c,"@p",l.Price);Add(c,"@q",l.Quantity);Add(c,"@u",l.IsUnlimited?1:0);Add(c,"@demo",l.IsDemo?1:0);Add(c,"@st",(int)l.Status);c.ExecuteNonQuery();
    }

    public MarketplaceListing UpdateListing(MarketplaceListing l)
    {
        using var db=Open();using var c=Command(db,@"UPDATE marketplace_listings SET merchant_id=@m,store_id=@s,listing_folder_id=@lf,version_folder_id=@vf,inventory_item_id=@ii,asset_id=@a,name=@n,description=@d,category=@c,tags=@t,price=@p,quantity=@q,is_unlimited=@u,is_demo=@demo,status=@st WHERE marketplace_id=@mid");
        Add(c,"@mid",l.MarketplaceId);Add(c,"@m",S(l.MerchantId));Add(c,"@s",S(l.StoreId));Add(c,"@lf",S(l.ListingFolderId));Add(c,"@vf",S(l.VersionFolderId));Add(c,"@ii",S(l.InventoryItemId));Add(c,"@a",S(l.AssetId));Add(c,"@n",l.Name);Add(c,"@d",l.Description);Add(c,"@c",l.Category);Add(c,"@t",JsonSerializer.Serialize(l.Tags));Add(c,"@p",l.Price);Add(c,"@q",l.Quantity);Add(c,"@u",l.IsUnlimited?1:0);Add(c,"@demo",l.IsDemo?1:0);Add(c,"@st",(int)l.Status);c.ExecuteNonQuery();return l;
    }

    public bool DeleteListing(int marketplaceId, UUID merchantId){using var db=Open();using var c=Command(db,"DELETE FROM marketplace_listings WHERE marketplace_id=@id AND merchant_id=@m");Add(c,"@id",marketplaceId);Add(c,"@m",S(merchantId));return c.ExecuteNonQuery()>0;}

    public MarketplaceOrder? GetOrder(UUID orderId){using var db=Open();using var c=Command(db,"SELECT * FROM marketplace_orders WHERE order_id=@id");Add(c,"@id",S(orderId));using var r=c.ExecuteReader();return r.Read()?ReadOrder(r):null;}
    public MarketplaceOrder? GetOrderByIdempotency(UUID buyerId,string key){using var db=Open();using var c=Command(db,"SELECT * FROM marketplace_orders WHERE buyer_id=@b AND idempotency_key=@k");Add(c,"@b",S(buyerId));Add(c,"@k",key);using var r=c.ExecuteReader();return r.Read()?ReadOrder(r):null;}
    public void StoreOrder(MarketplaceOrder o,string key){using var db=Open();using var c=Command(db,@"INSERT INTO marketplace_orders(order_id,buyer_id,merchant_id,listing_id,quantity,unit_price,total_price,status,payment_transaction_id,delivery_folder_id,delivery_item_id,idempotency_key,created_at,updated_at) VALUES(@id,@b,@m,@l,@q,@u,@t,@s,@p,@df,@di,@k,@ca,@ua)");Add(c,"@id",S(o.OrderId));Add(c,"@b",S(o.BuyerId));Add(c,"@m",S(o.MerchantId));Add(c,"@l",S(o.ListingId));Add(c,"@q",o.Quantity);Add(c,"@u",o.UnitPrice);Add(c,"@t",o.TotalPrice);Add(c,"@s",(int)o.Status);Add(c,"@p",o.PaymentTransactionId);Add(c,"@df",o.DeliveryFolderId);Add(c,"@di",o.DeliveryItemId);Add(c,"@k",key);Add(c,"@ca",o.CreatedAt.ToUnixTimeMilliseconds());Add(c,"@ua",o.UpdatedAt.ToUnixTimeMilliseconds());c.ExecuteNonQuery();}

    private DbConnection Open(){var db=CreateConnection(ConnectionString);db.Open();return db;}
    private static DbCommand Command(DbConnection db,string sql){var c=db.CreateCommand();c.CommandText=sql;return c;}
    private static MarketplaceStore ReadStore(DbDataReader r)=>new(U(r["store_id"]),U(r["merchant_id"]),Convert.ToString(r["name"])!,Convert.ToString(r["slug"])!,Convert.ToString(r["description"])!,B(r["is_active"]));
    private static MarketplaceListing ReadListing(DbDataReader r)=>new(U(r["listing_id"]),Convert.ToInt32(r["marketplace_id"]),U(r["merchant_id"]),U(r["store_id"]),U(r["listing_folder_id"]),U(r["version_folder_id"]),U(r["inventory_item_id"]),U(r["asset_id"]),Convert.ToString(r["name"])!,Convert.ToString(r["description"])!,Convert.ToString(r["category"])!,JsonSerializer.Deserialize<string[]>(Convert.ToString(r["tags"])!)??Array.Empty<string>(),Convert.ToInt32(r["price"]),Convert.ToInt32(r["quantity"]),B(r["is_unlimited"]),B(r["is_demo"]),(MarketplaceListingStatus)Convert.ToInt32(r["status"]));
    private static MarketplaceOrder ReadOrder(DbDataReader r)=>new(U(r["order_id"]),U(r["buyer_id"]),U(r["merchant_id"]),U(r["listing_id"]),Convert.ToInt32(r["quantity"]),Convert.ToInt32(r["unit_price"]),Convert.ToInt32(r["total_price"]),(MarketplaceOrderStatus)Convert.ToInt32(r["status"]),Convert.ToString(r["payment_transaction_id"])!,r["delivery_folder_id"]==DBNull.Value?null:Convert.ToString(r["delivery_folder_id"]),r["delivery_item_id"]==DBNull.Value?null:Convert.ToString(r["delivery_item_id"]),D(r["created_at"]),D(r["updated_at"]));
}
