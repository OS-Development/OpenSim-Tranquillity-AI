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

using System.Reflection;
using System.Xml;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Server.Base;

using Microsoft.Extensions.Logging;

namespace OpenSim.Region.CoreModules.World.Auction;

/// <summary>
/// Region-side client that calls the Robust /auction endpoint.
///
/// Configuration (OpenSim.ini):
/// <code>
/// [AuctionService]
///     AuctionServerURI = "http://robust-host:8003"
/// </code>
/// </summary>
public class RemoteAuctionServiceConnector : IAuctionService
{
    private static readonly ILogger m_log =
        LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod()!.DeclaringType!);

    private readonly string m_serverURI;
    private readonly IServiceAuth m_auth;

    // auth may be null (no AuthType configured); see ServiceAuth.Create.
    public RemoteAuctionServiceConnector(string serverURI, IServiceAuth auth = null)
    {
        m_serverURI = serverURI.TrimEnd('/');
        m_auth = auth;
    }

    // ── IAuctionService ─────────────────────────────────────────────────────

    public AuctionRecord? CreateAuction(
        UUID parcelID, int parcelLocalID, string parcelName, int parcelArea,
        UUID regionID, UUID originalOwner,
        DateTime startTime, DateTime closeTime,
        int startingBid, int bidIncrement, int commissionPct)
    {
        var body = new Dictionary<string, object>
        {
            ["METHOD"]       = "create_auction",
            ["ParcelID"]     = parcelID.ToString(),
            ["ParcelLocalID"]= parcelLocalID,
            ["ParcelName"]   = parcelName,
            ["ParcelArea"]   = parcelArea,
            ["RegionID"]     = regionID.ToString(),
            ["OriginalOwner"]= originalOwner.ToString(),
            ["StartTime"]    = startTime.ToString("O"),
            ["CloseTime"]    = closeTime.ToString("O"),
            ["StartingBid"]  = startingBid,
            ["BidIncrement"] = bidIncrement,
            ["CommissionPct"]= commissionPct
        };

        var reply = PostAndGetDict(body);
        return reply is not null ? DictToRecord(reply) : null;
    }

    public bool OpenAuction(uint auctionID)
        => PostBool([("METHOD", "open_auction"), ("AuctionID", auctionID.ToString())]);

    public bool CancelAuction(uint auctionID)
        => PostBool([("METHOD", "cancel_auction"), ("AuctionID", auctionID.ToString())]);

    public bool CloseAuction(uint auctionID)
        => PostBool([("METHOD", "close_auction"), ("AuctionID", auctionID.ToString())]);

    public BidResult PlaceBid(uint auctionID, UUID bidderID, int amount,
        out UUID refundBidderID, out int refundAmount)
    {
        refundBidderID = UUID.Zero;
        refundAmount   = 0;

        var body = new Dictionary<string, object>
        {
            ["METHOD"]    = "place_bid",
            ["AuctionID"] = auctionID,
            ["BidderID"]  = bidderID.ToString(),
            ["Amount"]    = amount
        };

        var reply = PostAndGetDict(body);
        if (reply is null)
            return BidResult.ServiceError;

        if (!reply.TryGetValue("RESULT", out var resultObj) ||
            !Enum.TryParse(resultObj?.ToString(), out BidResult result))
            return BidResult.ServiceError;

        if (reply.TryGetValue("RefundBidderID", out var rb) && rb is string rbStr)
            UUID.TryParse(rbStr, out refundBidderID);
        if (reply.TryGetValue("RefundAmount", out var ra) && ra is string raStr)
            int.TryParse(raStr, out refundAmount);

        return result;
    }

    public bool SettleAuction(uint auctionID)
        => PostBool([("METHOD", "settle_auction"), ("AuctionID", auctionID.ToString())]);

    public bool SetSettlementState(uint auctionID, AuctionSettlementState state)
        => PostBool([("METHOD", "set_settlement_state"),
                     ("AuctionID", auctionID.ToString()),
                     ("State", ((int)state).ToString())]);

    public AuctionRecord? GetAuction(uint auctionID)
    {
        var body = new Dictionary<string, object>
        {
            ["METHOD"]    = "get_auction",
            ["AuctionID"] = auctionID
        };
        var reply = PostAndGetDict(body);
        return reply is not null ? DictToRecord(reply) : null;
    }

    public AuctionRecord? GetAuctionByParcel(UUID parcelID)
    {
        var body = new Dictionary<string, object>
        {
            ["METHOD"]   = "get_auction_parcel",
            ["ParcelID"] = parcelID.ToString()
        };
        var reply = PostAndGetDict(body);
        return reply is not null ? DictToRecord(reply) : null;
    }

    public IList<AuctionRecord> GetAuctionsByStatus(params AuctionStatus[] statuses)
        => [];  // Not used region-side; call GetDueToOpen/Close or GetByRegion instead.

    public IList<AuctionRecord> GetAuctionsByRegion(UUID regionID, params AuctionStatus[] statuses)
    {
        var body = new Dictionary<string, object>
        {
            ["METHOD"]   = "get_by_region",
            ["RegionID"] = regionID.ToString()
        };
        return PostAndGetList(body);
    }

    public IList<AuctionRecord> GetDueToOpen()
        => PostAndGetList(new Dictionary<string, object> { ["METHOD"] = "get_due_open" });

    public IList<AuctionRecord> GetDueToClose()
        => PostAndGetList(new Dictionary<string, object> { ["METHOD"] = "get_due_close" });

    // ── HTTP helpers ─────────────────────────────────────────────────────────

    private bool PostBool(IEnumerable<(string key, string val)> pairs)
    {
        var body = pairs.ToDictionary(p => p.key, p => (object)p.val);
        var reply = PostAndGetDict(body);
        if (reply is null) return false;
        return reply.TryGetValue("RESULT", out var r) && r?.ToString() == "Success";
    }

    private Dictionary<string, object>? PostAndGetDict(Dictionary<string, object> body)
    {
        try
        {
            string reply = SynchronousRestFormsRequester.MakeRequest(
                "POST", m_serverURI + "/auction",
                ServerUtils.BuildQueryString(body), m_auth);

            if (string.IsNullOrWhiteSpace(reply))
                return null;

            var doc = new XmlDocument();
            doc.LoadXml(reply);
            return ParseDict(doc);
        }
        catch (Exception ex)
        {
            m_log.LogError(ex, "[REMOTE AUCTION CONNECTOR]: HTTP call failed.");
            return null;
        }
    }

    private IList<AuctionRecord> PostAndGetList(Dictionary<string, object> body)
    {
        try
        {
            string reply = SynchronousRestFormsRequester.MakeRequest(
                "POST", m_serverURI + "/auction",
                ServerUtils.BuildQueryString(body), m_auth);

            if (string.IsNullOrWhiteSpace(reply))
                return [];

            var doc = new XmlDocument();
            doc.LoadXml(reply);
            var root = doc["ServerResponse"];
            if (root is null) return [];

            if (!int.TryParse(root["Count"]?.InnerText, out int count) || count == 0)
                return [];

            var list = new List<AuctionRecord>(count);
            for (int i = 0; i < count; i++)
            {
                var item = root[$"Auction_{i}"];
                if (item is null) continue;
                var itemDict = new Dictionary<string, object>();
                foreach (XmlNode child in item.ChildNodes)
                    itemDict[child.Name] = child.InnerText;
                var record = DictToRecord(itemDict);
                if (record is not null) list.Add(record);
            }
            return list;
        }
        catch (Exception ex)
        {
            m_log.LogError(ex, "[REMOTE AUCTION CONNECTOR]: List call failed.");
            return [];
        }
    }

    private static Dictionary<string, object>? ParseDict(XmlDocument doc)
    {
        var root = doc["ServerResponse"];
        if (root is null) return null;
        var d = new Dictionary<string, object>();
        foreach (XmlNode node in root.ChildNodes)
            d[node.Name] = node.InnerText;
        return d;
    }

    private static AuctionRecord? DictToRecord(Dictionary<string, object> d)
    {
        try
        {
            return new AuctionRecord
            {
                AuctionID     = d.TryGetUInt("AuctionID"),
                ParcelID      = d.TryGetUUID("ParcelID"),
                ParcelLocalID = d.TryGetInt("ParcelLocalID"),
                ParcelName    = d.TryGetStr("ParcelName"),
                ParcelArea    = d.TryGetInt("ParcelArea"),
                RegionID      = d.TryGetUUID("RegionID"),
                OriginalOwner = d.TryGetUUID("OriginalOwner"),
                EscrowAccount = d.TryGetUUID("EscrowAccount"),
                StartTime     = d.TryGetDateTime("StartTime"),
                CloseTime     = d.TryGetDateTime("CloseTime"),
                StartingBid   = d.TryGetInt("StartingBid"),
                BidIncrement  = d.TryGetInt("BidIncrement"),
                CurrentBid    = d.TryGetInt("CurrentBid"),
                HighBidderID  = d.TryGetUUID("HighBidderID"),
                EscrowAmount  = d.TryGetInt("EscrowAmount"),
                CommissionPct = d.TryGetInt("CommissionPct"),
                Status        = (AuctionStatus)d.TryGetInt("Status"),
                SettlementState = (AuctionSettlementState)d.TryGetInt("SettlementState")
            };
        }
        catch
        {
            return null;
        }
    }
}

// ── Parsing extension helpers ────────────────────────────────────────────────
file static class DictExtensions
{
    public static string TryGetStr(this Dictionary<string, object> d, string key)
        => d.TryGetValue(key, out var v) ? v?.ToString() ?? string.Empty : string.Empty;

    public static int TryGetInt(this Dictionary<string, object> d, string key)
        => int.TryParse(d.TryGetStr(key), out int v) ? v : 0;

    public static uint TryGetUInt(this Dictionary<string, object> d, string key)
        => uint.TryParse(d.TryGetStr(key), out uint v) ? v : 0u;

    public static UUID TryGetUUID(this Dictionary<string, object> d, string key)
        => UUID.TryParse(d.TryGetStr(key), out UUID v) ? v : UUID.Zero;

    public static DateTime TryGetDateTime(this Dictionary<string, object> d, string key)
        => DateTime.TryParse(d.TryGetStr(key), null,
               System.Globalization.DateTimeStyles.RoundtripKind, out var v)
           ? v : default;
}
