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
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data.MySQL;
using OpenSim.Framework;

using Microsoft.Extensions.Logging;

namespace OpenSim.Services.AuctionService;

public class AuctionService : IAuctionService
{
    private static readonly ILogger m_log =
        LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod()!.DeclaringType!);

    private readonly MySQLAuctionData m_db;
    private readonly UUID             m_escrowAccount;
    private readonly int              m_defaultCommission;

    // Serialises concurrent bid writes for the same auction.
    // Key = AuctionID.
    private readonly Dictionary<uint, object> m_bidLocks = [];
    private readonly object m_bidLocksGuard = new();

    public AuctionService(IConfigSource config)
        : this(config, "AuctionService")
    {
    }

    // The Robust connector loads this class with (config, configName) - see
    // AuctionServiceInConnector - so this overload must exist or LoadPlugin
    // fails with MissingMethodException and returns null.
    public AuctionService(IConfigSource config, string configName)
    {
        if (string.IsNullOrEmpty(configName))
            configName = "AuctionService";

        IConfig cfg = config.Configs[configName]
            ?? throw new Exception($"[AUCTION SERVICE]: Missing [{configName}] config section.");

        string connStr = cfg.GetString("ConnectionString", string.Empty);
        if (string.IsNullOrWhiteSpace(connStr))
            throw new Exception("[AUCTION SERVICE]: ConnectionString not set.");

        m_db = new MySQLAuctionData(connStr);

        string escrowStr = cfg.GetString(
            "AuctionServicesAccountID",
            AuctionConstants.AuctionServicesAccountID.ToString());
        m_escrowAccount = UUID.Parse(escrowStr);

        m_defaultCommission = cfg.GetInt("DefaultCommissionPct", 15);
        m_defaultCommission = Math.Clamp(m_defaultCommission, 0, 100);

        m_log.LogInformation("[AUCTION SERVICE]: Initialised. Escrow account={Escrow} Commission={Pct}%",
            m_escrowAccount, m_defaultCommission);
    }

    // ── IAuctionService: Lifecycle ──────────────────────────────────────────

    public AuctionRecord? CreateAuction(
        UUID   parcelID,
        int    parcelLocalID,
        string parcelName,
        int    parcelArea,
        UUID   regionID,
        UUID   originalOwner,
        DateTime startTime,
        DateTime closeTime,
        int    startingBid,
        int    bidIncrement,
        int    commissionPct)
    {
        // ── Validate ───────────────────────────────────────────────────────
        var now = DateTime.UtcNow;

        if (startTime <= now)
        {
            m_log.LogWarning("[AUCTION SERVICE]: CreateAuction rejected – StartTime is in the past.");
            return null;
        }
        if (startTime > now.AddWeeks(AuctionConstants.MaxScheduleWeeks))
        {
            m_log.LogWarning("[AUCTION SERVICE]: CreateAuction rejected – StartTime too far in the future.");
            return null;
        }
        double durationHours = (closeTime - startTime).TotalHours;
        if (durationHours < AuctionConstants.MinDurationHours)
        {
            m_log.LogWarning("[AUCTION SERVICE]: CreateAuction rejected – duration < {Min}h.", AuctionConstants.MinDurationHours);
            return null;
        }
        if (durationHours > AuctionConstants.MaxDurationHours)
        {
            m_log.LogWarning("[AUCTION SERVICE]: CreateAuction rejected – duration > {Max}h.", AuctionConstants.MaxDurationHours);
            return null;
        }
        if (bidIncrement < AuctionConstants.MinBidIncrement ||
            bidIncrement > AuctionConstants.MaxBidIncrement ||
            bidIncrement % 10 != 0)
        {
            m_log.LogWarning("[AUCTION SERVICE]: CreateAuction rejected – invalid BidIncrement {Inc}.", bidIncrement);
            return null;
        }

        // Reject if this parcel already has an active (non-terminal) auction.
        var existing = m_db.GetByParcel(parcelID);
        if (existing is not null)
        {
            m_log.LogWarning("[AUCTION SERVICE]: CreateAuction rejected – parcel {Parcel} already has active auction {ID}.",
                parcelID, existing.AuctionID);
            return null;
        }

        // ── Create ─────────────────────────────────────────────────────────
        var record = new AuctionRecord
        {
            ParcelID      = parcelID,
            ParcelLocalID = parcelLocalID,
            ParcelName    = parcelName,
            ParcelArea    = parcelArea,
            RegionID      = regionID,
            OriginalOwner = originalOwner,
            EscrowAccount = m_escrowAccount,
            StartTime     = startTime,
            CloseTime     = closeTime,
            StartingBid   = Math.Max(0, startingBid),
            BidIncrement  = bidIncrement,
            CommissionPct = Math.Clamp(commissionPct, 0, 100),
            Status        = AuctionStatus.Scheduled
        };

        uint id = m_db.Create(record);
        m_log.LogInformation("[AUCTION SERVICE]: Created auction {ID} for parcel {Parcel} in region {Region}.",
            id, parcelID, regionID);
        return record;
    }

    public bool OpenAuction(uint auctionID)
    {
        lock (GetAuctionLock(auctionID))
        {
            bool ok = m_db.TransitionStatus(auctionID, AuctionStatus.Scheduled, AuctionStatus.Open);
            if (ok)
                m_log.LogInformation("[AUCTION SERVICE]: Opened auction {ID}.", auctionID);
            return ok;
        }
    }

    public bool CancelAuction(uint auctionID)
    {
        // Same lock as PlaceBid: a bid must not slip in between the "no bids"
        // check below and the status change.
        lock (GetAuctionLock(auctionID))
        {
            var record = m_db.GetByID(auctionID);
            if (record is null)
                return false;

            // Cannot cancel once bidding has started.
            if (record.Status == AuctionStatus.Open && record.CurrentBid > 0)
            {
                m_log.LogWarning("[AUCTION SERVICE]: Cannot cancel auction {ID} – bids already placed.", auctionID);
                return false;
            }
            if (record.Status is not (AuctionStatus.Scheduled or AuctionStatus.Open))
                return false;

            bool ok = m_db.TransitionStatus(auctionID, record.Status, AuctionStatus.Cancelled);
            if (ok)
                m_log.LogInformation("[AUCTION SERVICE]: Cancelled auction {ID}.", auctionID);
            return ok;
        }
    }

    public bool CloseAuction(uint auctionID)
    {
        lock (GetAuctionLock(auctionID))
        {
            var record = m_db.GetByID(auctionID);
            if (record is null || record.Status != AuctionStatus.Open)
                return false;

            if (DateTime.UtcNow < AsUtc(record.CloseTime))
                return false;

            bool ok = m_db.TransitionStatus(auctionID, AuctionStatus.Open, AuctionStatus.Closed);
            if (ok)
                m_log.LogInformation("[AUCTION SERVICE]: Closed auction {ID}.", auctionID);
            return ok;
        }
    }

    // Per-auction lock: serialises bids, open, cancel and close for one auction.
    private object GetAuctionLock(uint auctionID)
    {
        lock (m_bidLocksGuard)
        {
            if (!m_bidLocks.TryGetValue(auctionID, out object? auctionLock))
            {
                auctionLock = new object();
                m_bidLocks[auctionID] = auctionLock;
            }
            return auctionLock;
        }
    }

    // Times are stored as UTC but come back from MySQL with Kind=Unspecified.
    private static DateTime AsUtc(DateTime dt)
        => DateTime.SpecifyKind(dt, DateTimeKind.Utc);

    // ── IAuctionService: Bidding ────────────────────────────────────────────

    public BidResult PlaceBid(
        uint auctionID,
        UUID bidderID,
        int  amount,
        out UUID refundBidderID,
        out int  refundAmount)
    {
        refundBidderID = UUID.Zero;
        refundAmount   = 0;

        // Per-auction lock prevents two simultaneous bids racing.
        lock (GetAuctionLock(auctionID))
        {
            var record = m_db.GetByID(auctionID);
            if (record is null || record.Status != AuctionStatus.Open)
                return BidResult.AuctionNotOpen;

            // The close timer may not have run yet; never accept a late bid.
            if (DateTime.UtcNow >= AsUtc(record.CloseTime))
                return BidResult.AuctionNotOpen;

            if (bidderID.Equals(record.OriginalOwner))
                return BidResult.OwnerCannotBid;

            // Minimum next bid = current bid (or starting bid) + increment.
            int minimumBid = record.CurrentBid > 0
                ? record.CurrentBid + record.BidIncrement
                : record.StartingBid;

            if (amount < minimumBid)
                return BidResult.BidTooLow;

            // Return escrow to the outgoing high bidder.
            if (!record.HighBidderID.IsZero() && record.EscrowAmount > 0)
            {
                refundBidderID = record.HighBidderID;
                refundAmount   = record.EscrowAmount;
            }

            // Update the record – money movement happens in the region module.
            record.CurrentBid    = amount;
            record.HighBidderID  = bidderID;
            record.EscrowAmount  = amount;
            record.UpdatedAt     = DateTime.UtcNow;

            if (!m_db.Update(record))
                return BidResult.ServiceError;

            m_log.LogInformation(
                "[AUCTION SERVICE]: Bid L${Amount} placed on auction {ID} by {Bidder}.",
                amount, auctionID, bidderID);
            return BidResult.Accepted;
        }
    }

    // ── IAuctionService: Settlement ─────────────────────────────────────────

    public bool SettleAuction(uint auctionID)
    {
        // Conditional update: only one caller can move Closed -> Settled.
        bool ok = m_db.TransitionStatus(auctionID, AuctionStatus.Closed, AuctionStatus.Settled);

        // Release the per-auction lock object once fully settled.
        if (ok)
        {
            lock (m_bidLocksGuard)
                m_bidLocks.Remove(auctionID);

            m_log.LogInformation("[AUCTION SERVICE]: Settled auction {ID}.", auctionID);
        }
        return ok;
    }

    public bool SetSettlementState(uint auctionID, AuctionSettlementState state)
        => m_db.SetSettlementState(auctionID, (int)state);

    // ── IAuctionService: Queries ────────────────────────────────────────────

    public AuctionRecord? GetAuction(uint auctionID)
        => m_db.GetByID(auctionID);

    public AuctionRecord? GetAuctionByParcel(UUID parcelID)
        => m_db.GetByParcel(parcelID);

    public IList<AuctionRecord> GetAuctionsByStatus(params AuctionStatus[] statuses)
        => m_db.GetByStatus(statuses);

    public IList<AuctionRecord> GetAuctionsByRegion(UUID regionID, params AuctionStatus[] statuses)
        => m_db.GetByRegion(regionID, statuses);

    public IList<AuctionRecord> GetDueToOpen()
        => m_db.GetDueToOpen();

    public IList<AuctionRecord> GetDueToClose()
        => m_db.GetDueToClose();
}

// Extension so DateTime.AddWeeks compiles without pulling in extra usings.
file static class DateTimeExtensions
{
    public static DateTime AddWeeks(this DateTime dt, int weeks)
        => dt.AddDays(weeks * 7);
}
