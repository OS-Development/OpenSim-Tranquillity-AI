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
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Server.Base;

using Microsoft.Extensions.Logging;

namespace OpenSim.Server.Handlers.Auction;

/// <summary>
/// Receives POST /auction calls from region simulators and dispatches them
/// to <see cref="IAuctionService"/>.
///
/// Wire format: URL-encoded key=value pairs (same as Presence, Friends,
/// Estate handlers).  Every request includes a METHOD field.
///
/// Supported methods:
///   create_auction    – schedule a new auction
///   open_auction      – transition Scheduled → Open
///   cancel_auction    – cancel a Scheduled / zero-bid Open auction
///   place_bid         – record a bid (money movement on region side)
///   settle_auction    – mark a Closed auction as Settled
///   get_auction       – fetch a record by AuctionID
///   get_auction_parcel– fetch active record by ParcelID
///   get_due_open      – list Scheduled auctions past their start time
///   get_due_close     – list Open auctions past their close time
///   get_by_region     – list active auctions in a region
/// </summary>
public class AuctionServerPostHandler : BaseStreamHandler
{
    private static readonly ILogger m_log =
        LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod()!.DeclaringType!);

    private readonly IAuctionService m_service;

    public AuctionServerPostHandler(IAuctionService service, IServiceAuth? auth)
        : base("POST", "/auction", auth)
    {
        m_service = service;
    }

    protected override byte[] ProcessRequest(
        string path,
        Stream requestData,
        IOSHttpRequest  httpRequest,
        IOSHttpResponse httpResponse)
    {
        using var sr   = new StreamReader(requestData);
        string    body = sr.ReadToEnd().Trim();

        try
        {
            var req = ServerUtils.ParseQueryString(body);
            if (!req.TryGetValue("METHOD", out object? methodObj) || methodObj is not string method)
                return Failure("Missing METHOD");

            return method switch
            {
                "create_auction"     => HandleCreate(req),
                "open_auction"       => HandleOpen(req),
                "cancel_auction"     => HandleCancel(req),
                "close_auction"      => HandleClose(req),
                "place_bid"          => HandlePlaceBid(req),
                "settle_auction"     => HandleSettle(req),
                "set_settlement_state" => HandleSetSettlementState(req),
                "get_auction"        => HandleGet(req),
                "get_auction_parcel" => HandleGetByParcel(req),
                "get_due_open"       => HandleGetDueToOpen(),
                "get_due_close"      => HandleGetDueToClose(),
                "get_by_region"      => HandleGetByRegion(req),
                _                    => Failure($"Unknown method: {method}")
            };
        }
        catch (Exception ex)
        {
            m_log.LogError(ex, "[AUCTION HANDLER]: Unhandled exception.");
            return Failure("Internal error");
        }
    }

    // ── Method handlers ─────────────────────────────────────────────────────

    private byte[] HandleCreate(Dictionary<string, object> req)
    {
        if (!TryGetUUID(req, "ParcelID",      out var parcelID)      ||
            !TryGetInt (req, "ParcelLocalID", out int localID)       ||
            !TryGetStr (req, "ParcelName",    out string parcelName) ||
            !TryGetInt (req, "ParcelArea",    out int area)          ||
            !TryGetUUID(req, "RegionID",      out var regionID)      ||
            !TryGetUUID(req, "OriginalOwner", out var owner)         ||
            !TryGetDateTime(req, "StartTime", out var start)         ||
            !TryGetDateTime(req, "CloseTime", out var close)         ||
            !TryGetInt (req, "StartingBid",   out int startBid)      ||
            !TryGetInt (req, "BidIncrement",  out int increment)     ||
            !TryGetInt (req, "CommissionPct", out int commission))
            return Failure("Missing parameters");

        var record = m_service.CreateAuction(
            parcelID, localID, parcelName, area,
            regionID, owner, start, close,
            startBid, increment, commission);

        if (record is null)
            return Failure("Auction creation rejected");

        return RecordResult(record);
    }

    private byte[] HandleOpen(Dictionary<string, object> req)
    {
        if (!TryGetUInt(req, "AuctionID", out uint id))
            return Failure("Missing AuctionID");
        return m_service.OpenAuction(id) ? Success() : Failure("OpenAuction failed");
    }

    private byte[] HandleCancel(Dictionary<string, object> req)
    {
        if (!TryGetUInt(req, "AuctionID", out uint id))
            return Failure("Missing AuctionID");
        return m_service.CancelAuction(id) ? Success() : Failure("CancelAuction failed");
    }

    private byte[] HandleClose(Dictionary<string, object> req)
    {
        if (!TryGetUInt(req, "AuctionID", out uint id))
            return Failure("Missing AuctionID");
        return m_service.CloseAuction(id) ? Success() : Failure("CloseAuction failed");
    }

    private byte[] HandlePlaceBid(Dictionary<string, object> req)
    {
        if (!TryGetUInt(req, "AuctionID", out uint id)    ||
            !TryGetUUID(req, "BidderID",  out var bidder) ||
            !TryGetInt (req, "Amount",    out int amount))
            return Failure("Missing parameters");

        var result = m_service.PlaceBid(id, bidder, amount,
            out UUID refundBidder, out int refundAmount);

        var response = new Dictionary<string, object>
        {
            ["RESULT"]         = result.ToString(),
            ["RefundBidderID"] = refundBidder.ToString(),
            ["RefundAmount"]   = refundAmount.ToString()
        };
        return DictResult(response);
    }

    private byte[] HandleSettle(Dictionary<string, object> req)
    {
        if (!TryGetUInt(req, "AuctionID", out uint id))
            return Failure("Missing AuctionID");
        return m_service.SettleAuction(id) ? Success() : Failure("SettleAuction failed");
    }

    private byte[] HandleSetSettlementState(Dictionary<string, object> req)
    {
        if (!TryGetUInt(req, "AuctionID", out uint id) ||
            !TryGetInt (req, "State",     out int state))
            return Failure("Missing parameters");

        if (!Enum.IsDefined(typeof(AuctionSettlementState), state))
            return Failure("Invalid settlement state");

        return m_service.SetSettlementState(id, (AuctionSettlementState)state)
            ? Success() : Failure("SetSettlementState failed");
    }

    private byte[] HandleGet(Dictionary<string, object> req)
    {
        if (!TryGetUInt(req, "AuctionID", out uint id))
            return Failure("Missing AuctionID");
        var record = m_service.GetAuction(id);
        return record is not null ? RecordResult(record) : Failure("Not found");
    }

    private byte[] HandleGetByParcel(Dictionary<string, object> req)
    {
        if (!TryGetUUID(req, "ParcelID", out var parcelID))
            return Failure("Missing ParcelID");
        var record = m_service.GetAuctionByParcel(parcelID);
        return record is not null ? RecordResult(record) : Failure("Not found");
    }

    private byte[] HandleGetDueToOpen()
        => RecordListResult(m_service.GetDueToOpen());

    private byte[] HandleGetDueToClose()
        => RecordListResult(m_service.GetDueToClose());

    private byte[] HandleGetByRegion(Dictionary<string, object> req)
    {
        if (!TryGetUUID(req, "RegionID", out var regionID))
            return Failure("Missing RegionID");
        var records = m_service.GetAuctionsByRegion(
            regionID, AuctionStatus.Scheduled, AuctionStatus.Open, AuctionStatus.Closed);
        return RecordListResult(records);
    }

    // ── Wire serialisation ──────────────────────────────────────────────────
    // XML matches the pattern used by EstateRequestHandler / PresenceHandler.

    private static byte[] Success()    => BoolResult(true);
    private static byte[] Failure(string reason)
    {
        m_log.LogWarning("[AUCTION HANDLER]: {Reason}", reason);
        return BoolResult(false);
    }

    private static byte[] BoolResult(bool value)
    {
        var doc  = new XmlDocument();
        var decl = doc.CreateXmlDeclaration("1.0", "UTF-8", null);
        doc.AppendChild(decl);
        var root = doc.CreateElement("ServerResponse");
        doc.AppendChild(root);
        var result = doc.CreateElement("RESULT");
        result.AppendChild(doc.CreateTextNode(value ? "Success" : "Failure"));
        root.AppendChild(result);
        return DocBytes(doc);
    }

    private static byte[] DictResult(Dictionary<string, object> data)
    {
        var doc  = new XmlDocument();
        var decl = doc.CreateXmlDeclaration("1.0", "UTF-8", null);
        doc.AppendChild(decl);
        var root = doc.CreateElement("ServerResponse");
        doc.AppendChild(root);
        foreach (var (key, val) in data)
        {
            var elem = doc.CreateElement(key);
            elem.AppendChild(doc.CreateTextNode(val?.ToString() ?? string.Empty));
            root.AppendChild(elem);
        }
        return DocBytes(doc);
    }

    private static byte[] RecordResult(AuctionRecord r)
        => DictResult(RecordToDict(r));

    private static byte[] RecordListResult(IList<AuctionRecord> records)
    {
        var doc  = new XmlDocument();
        var decl = doc.CreateXmlDeclaration("1.0", "UTF-8", null);
        doc.AppendChild(decl);
        var root = doc.CreateElement("ServerResponse");
        doc.AppendChild(root);

        var count = doc.CreateElement("Count");
        count.AppendChild(doc.CreateTextNode(records.Count.ToString()));
        root.AppendChild(count);

        for (int i = 0; i < records.Count; i++)
        {
            var item = doc.CreateElement($"Auction_{i}");
            foreach (var (key, val) in RecordToDict(records[i]))
            {
                var elem = doc.CreateElement(key);
                elem.AppendChild(doc.CreateTextNode(val?.ToString() ?? string.Empty));
                item.AppendChild(elem);
            }
            root.AppendChild(item);
        }
        return DocBytes(doc);
    }

    private static Dictionary<string, object> RecordToDict(AuctionRecord r)
        => new()
        {
            ["AuctionID"]     = r.AuctionID,
            ["ParcelID"]      = r.ParcelID.ToString(),
            ["ParcelLocalID"] = r.ParcelLocalID,
            ["ParcelName"]    = r.ParcelName,
            ["ParcelArea"]    = r.ParcelArea,
            ["RegionID"]      = r.RegionID.ToString(),
            ["OriginalOwner"] = r.OriginalOwner.ToString(),
            ["EscrowAccount"] = r.EscrowAccount.ToString(),
            ["StartTime"]     = r.StartTime.ToString("O"),
            ["CloseTime"]     = r.CloseTime.ToString("O"),
            ["StartingBid"]   = r.StartingBid,
            ["BidIncrement"]  = r.BidIncrement,
            ["CurrentBid"]    = r.CurrentBid,
            ["HighBidderID"]  = r.HighBidderID.ToString(),
            ["EscrowAmount"]  = r.EscrowAmount,
            ["CommissionPct"] = r.CommissionPct,
            ["Status"]        = (int)r.Status,
            ["SettlementState"] = (int)r.SettlementState,
        };

    private static byte[] DocBytes(XmlDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // ── Param parsing helpers ───────────────────────────────────────────────

    private static bool TryGetStr(Dictionary<string, object> req, string key, out string val)
    {
        val = string.Empty;
        if (!req.TryGetValue(key, out var obj) || obj is not string s)
            return false;
        val = s;
        return true;
    }

    private static bool TryGetInt(Dictionary<string, object> req, string key, out int val)
    {
        val = 0;
        return TryGetStr(req, key, out string s) && int.TryParse(s, out val);
    }

    private static bool TryGetUInt(Dictionary<string, object> req, string key, out uint val)
    {
        val = 0;
        return TryGetStr(req, key, out string s) && uint.TryParse(s, out val);
    }

    private static bool TryGetUUID(Dictionary<string, object> req, string key, out UUID val)
    {
        val = UUID.Zero;
        return TryGetStr(req, key, out string s) && UUID.TryParse(s, out val);
    }

    private static bool TryGetDateTime(Dictionary<string, object> req, string key, out DateTime val)
    {
        val = default;
        return TryGetStr(req, key, out string s) &&
               DateTime.TryParse(s, null,
                   System.Globalization.DateTimeStyles.RoundtripKind, out val);
    }
}
