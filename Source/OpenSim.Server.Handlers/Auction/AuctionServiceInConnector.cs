/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using System.Reflection;

namespace OpenSim.Server.Handlers.Auction;
public class AuctionServiceInConnector : ServiceConnector
{
    private static readonly ILogger m_log =
        LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod()!.DeclaringType!);

    private IAuctionService m_AuctionService;
    private string m_ConfigName = "AuctionService";

    public AuctionServiceInConnector(IConfigSource config, IHttpServer server, string configName)
        : base(config, server, configName)
    {
        if (configName != String.Empty)
            m_ConfigName = configName;

        IConfig serverConfig = config.Configs[m_ConfigName];
        if (serverConfig == null)
            throw new Exception(String.Format("No section '{0}' in config file", m_ConfigName));

        string auctionService = serverConfig.GetString("LocalServiceModule", string.Empty);
        if (auctionService.Length == 0)
            throw new Exception($"No LocalServiceModule in section '{m_ConfigName}'");

        object[] args = new object[] { config, m_ConfigName };
        m_AuctionService = ServerUtils.LoadPlugin<IAuctionService>(auctionService, args);

        if (m_AuctionService is null)
            throw new Exception($"Failed to load AuctionService from '{auctionService}'");

        // Same as the other Robust connectors: always blocks llHTTPRequest callers,
        // and enforces AuthType (e.g. BasicHttpAuthentication) if configured in
        // [Network] or [AuctionService].
        IServiceAuth auth = ServiceAuth.Create(config, m_ConfigName);

        server.AddStreamHandler(new AuctionServerPostHandler(m_AuctionService, auth));
        m_log.LogInformation("[AUCTION IN CONNECTOR]: Registered POST /auction handler.");
    }
}
