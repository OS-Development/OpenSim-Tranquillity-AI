using System.Data.Common;
using System.Data.SQLite;
using OpenSim.Services.Marketplace;

namespace OpenSim.Data.SQLite;

public sealed class SQLiteMarketplaceData : MarketplaceDbData
{
    public override string Version => "1.0.0.0";
    public override string Name => "SQLite Marketplace Data Interface";
    protected override DbConnection CreateConnection(string connectionString) => new SQLiteConnection(connectionString);
}