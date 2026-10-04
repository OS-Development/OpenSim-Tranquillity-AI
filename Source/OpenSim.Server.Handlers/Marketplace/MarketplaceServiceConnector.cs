using System.Reflection;
using Nini.Config;
using Microsoft.Extensions.Logging;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Services.Marketplace;

namespace OpenSim.Server.Handlers.Marketplace;

public sealed class MarketplaceServiceConnector : ServiceConnector
{
    private static readonly ILogger m_log =
        LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

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

        string resolvedStorageProvider = string.Empty;
        bool resolvedConnectionStringConfigured = false;

        try
        {
            _data = LoadMarketplaceData(
                config,
                serviceConfig,
                out resolvedStorageProvider,
                out resolvedConnectionStringConfigured);

            if (_data != null)
            {
                m_log.LogInformation(
                    "[MARKETPLACE]: Database initialization completed successfully using provider {Provider}",
                    resolvedStorageProvider);
            }
            else
            {
                m_log.LogWarning(
                    "[MARKETPLACE]: No Marketplace database provider is configured. " +
                    "Marketplace persistence is disabled and the service will use in-memory state.");
            }
        }
        catch (Exception e)
        {
            m_log.LogCritical(
                e,
                "[MARKETPLACE]: FATAL database initialization failure. " +
                "Marketplace startup cannot continue. StorageProvider={Provider}, " +
                "ConnectionStringConfigured={ConnectionStringConfigured}",
                resolvedStorageProvider,
                resolvedConnectionStringConfigured);

            throw new InvalidOperationException(
                "Marketplace database initialization failed. See the preceding [MARKETPLACE] log entry for the provider and configuration state.",
                e);
        }

        server.AddSimpleStreamHandler(
            new MarketplaceInventoryImportHandler(_inventory),
            varPath: true);
    }

    private static IMarketplaceDataPlugin? LoadMarketplaceData(
        IConfigSource config,
        IConfig serviceConfig,
        out string resolvedStorageProvider,
        out bool resolvedConnectionStringConfigured)
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

        resolvedStorageProvider = storageProvider;
        resolvedConnectionStringConfigured = !string.IsNullOrWhiteSpace(connectionString);

        if (string.IsNullOrWhiteSpace(storageProvider))
            return null;

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new Exception(
                $"MarketplaceService resolved StorageProvider '{storageProvider}' but no ConnectionString was supplied.");

        var data = ServerUtils.LoadPlugin<IMarketplaceDataPlugin>(
            storageProvider,
            Array.Empty<object>())
            ?? throw new Exception(
                $"Failed to load Marketplace data provider from {storageProvider}");

        data.Initialise(connectionString);
        return data;
    }
}
