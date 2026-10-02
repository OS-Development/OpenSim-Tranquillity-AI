using Nini.Config;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Services.Marketplace;

namespace OpenSim.Server.Handlers.Marketplace;

public sealed class MarketplaceServiceConnector : ServiceConnector
{
    private readonly IInventoryService _inventory;
    private readonly IMarketplaceDataPlugin? _data;

    public MarketplaceServiceConnector(IConfigSource config, IHttpServer server, string configName)
        : base(config, server, configName)
    {
        string sectionName = string.IsNullOrEmpty(configName) ? "MarketplaceService" : configName;
        ConfigName = sectionName;

        IConfig serviceConfig = config.Configs[sectionName];
        if (serviceConfig == null)
            throw new Exception($"No section '{sectionName}' in config file");

        string inventoryService = serviceConfig.GetString(
            "InventoryService",
            "OpenSim.Services.InventoryService.dll:XInventoryService");

        _inventory = ServerUtils.LoadPlugin<IInventoryService>(
            inventoryService,
            new object[] { config, "InventoryService" })
            ?? throw new Exception($"Failed to load inventory service from {inventoryService}");

        _data = LoadMarketplaceData(config, serviceConfig);

        server.AddSimpleStreamHandler(
            new MarketplaceInventoryImportHandler(_inventory),
            varPath: true);
    }

    private static IMarketplaceDataPlugin? LoadMarketplaceData(
        IConfigSource config,
        IConfig serviceConfig)
    {
        string storageProvider = serviceConfig.GetString("StorageProvider", string.Empty);
        string connectionString = serviceConfig.GetString("ConnectionString", string.Empty);

        // If Marketplace storage is not explicitly configured, reuse the grid's
        // primary DatabaseService provider and connection string. This makes the
        // Marketplace tables appear automatically in the existing OpenSim DB.
        if (string.IsNullOrWhiteSpace(storageProvider) ||
            string.IsNullOrWhiteSpace(connectionString))
        {
            IConfig databaseConfig = config.Configs["DatabaseService"];
            if (databaseConfig != null)
            {
                if (string.IsNullOrWhiteSpace(storageProvider))
                    storageProvider = databaseConfig.GetString("StorageProvider", string.Empty);

                if (string.IsNullOrWhiteSpace(connectionString))
                    connectionString = databaseConfig.GetString("ConnectionString", string.Empty);
            }
        }

        if (string.IsNullOrWhiteSpace(storageProvider))
            return null;

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new Exception(
                "MarketplaceService has StorageProvider configured but no ConnectionString was supplied.");

        var data = ServerUtils.LoadPlugin<IMarketplaceDataPlugin>(
            storageProvider,
            Array.Empty<object>())
            ?? throw new Exception(
                $"Failed to load Marketplace data provider from {storageProvider}");

        data.Initialise(connectionString);
        return data;
    }
}
