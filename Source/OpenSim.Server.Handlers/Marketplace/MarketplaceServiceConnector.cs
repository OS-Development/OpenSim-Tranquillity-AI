using Nini.Config;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;

namespace OpenSim.Server.Handlers.Marketplace;

public sealed class MarketplaceServiceConnector : ServiceConnector
{
    private readonly IInventoryService _inventory;

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

        server.AddSimpleStreamHandler(
            new MarketplaceInventoryImportHandler(_inventory),
            varPath: true);
    }
}
