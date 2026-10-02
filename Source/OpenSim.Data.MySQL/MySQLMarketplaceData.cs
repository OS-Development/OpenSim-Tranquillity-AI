using System.Data.Common;
using MySqlConnector;
using OpenSim.Services.Marketplace;

namespace OpenSim.Data.MySQL;

public sealed class MySQLMarketplaceData : MarketplaceDbData
{
    public override string Version => "1.0.0.0";
    public override string Name => "MySQL Marketplace Data Interface";
    protected override DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);
}