using System.Data.Common;
using Npgsql;
using OpenSim.Services.Marketplace;

namespace OpenSim.Data.PGSQL;

public sealed class PGSQLMarketplaceData : MarketplaceDbData
{
    public override string Version => "1.0.0.0";
    public override string Name => "PostgreSQL Marketplace Data Interface";
    protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);
}