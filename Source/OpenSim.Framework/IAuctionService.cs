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

using OpenMetaverse;

namespace OpenSim.Framework;

/// <summary>
/// State machine for a parcel auction.
/// Scheduled  → parcel reserved, bidding not yet open.
/// Open       → bidding active.
/// Closed     → bidding ended, awaiting settlement.
/// Settled    → land transferred to winner (terminal).
/// Cancelled  → cancelled before any bids were received (terminal).
/// </summary>
public enum AuctionStatus
{
    Scheduled = 0,
    Open      = 1,
    Closed    = 2,
    Settled   = 3,
    Cancelled = 4
}

/// <summary>
/// How far settlement of a Closed auction has got.  Persisted so a retry after
/// a failure only repeats the steps that did not finish (a payout is never
/// paid twice, land is never transferred twice).
/// </summary>
public enum AuctionSettlementState
{
    None            = 0,
    LandTransferred = 1,
    OwnerPaid       = 2
}

/// <summary>
/// Persistent record for one auction.  Stored grid-side so bids survive
/// region restarts and are visible across all regions.
/// </summary>
public class AuctionRecord
{
    /// <summary>Unique auction identifier (maps to LandData.AuctionID).</summary>
    public uint   AuctionID      { get; set; }

    // ── Parcel identity ────────────────────────────────────────────────────
    public UUID   ParcelID       { get; set; }   // LandData.GlobalID
    public int    ParcelLocalID  { get; set; }   // LandData.LocalID (region-scoped)
    public string ParcelName     { get; set; } = string.Empty;
    public int    ParcelArea     { get; set; }   // square metres
    public UUID   RegionID       { get; set; }   // Scene.RegionInfo.RegionID

    // ── Ownership before auction ────────────────────────────────────────────
    /// <summary>
    /// Original parcel owner.  Returned the land if the auction is
    /// cancelled or closes with zero bids.
    /// </summary>
    public UUID   OriginalOwner  { get; set; }

    /// <summary>
    /// Holding account UUID while the auction is active.
    /// Defaults to <see cref="AuctionConstants.AuctionServicesAccountID"/>.
    /// </summary>
    public UUID   EscrowAccount  { get; set; } = AuctionConstants.AuctionServicesAccountID;

    // ── Timing ─────────────────────────────────────────────────────────────
    public DateTime StartTime    { get; set; }
    public DateTime CloseTime    { get; set; }

    // ── Bid configuration ──────────────────────────────────────────────────
    /// <summary>Minimum opening bid (L$).</summary>
    public int    StartingBid    { get; set; }

    /// <summary>
    /// Minimum increment between bids (L$).  Must be a multiple of 10,
    /// range 10–100, matching SL behaviour.
    /// </summary>
    public int    BidIncrement   { get; set; } = 10;

    // ── Live bid state ─────────────────────────────────────────────────────
    public int    CurrentBid     { get; set; }
    public UUID   HighBidderID   { get; set; } = UUID.Zero;

    /// <summary>
    /// Amount currently held in escrow for the high bidder.
    /// Equals CurrentBid when a bid is live.
    /// </summary>
    public int    EscrowAmount   { get; set; }

    // ── Commission ─────────────────────────────────────────────────────────
    /// <summary>
    /// Percentage (0–100) taken as commission from the winning bid before
    /// paying the original owner.  Default 15% mirrors SL behaviour.
    /// </summary>
    public int    CommissionPct  { get; set; } = 15;

    // ── State ──────────────────────────────────────────────────────────────
    public AuctionStatus Status  { get; set; } = AuctionStatus.Scheduled;

    /// <summary>Progress of settlement; only meaningful once Closed.</summary>
    public AuctionSettlementState SettlementState { get; set; } = AuctionSettlementState.None;

    public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt    { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Result of a <see cref="IAuctionService.PlaceBid"/> call.
/// </summary>
public enum BidResult
{
    /// <summary>Bid accepted; bidder is now the high bidder.</summary>
    Accepted,
    /// <summary>Auction not found or not in Open state.</summary>
    AuctionNotOpen,
    /// <summary>Bid amount is below the required minimum.</summary>
    BidTooLow,
    /// <summary>Bidder does not have enough L$ for the escrow hold.</summary>
    InsufficientFunds,
    /// <summary>Bidder is the current owner; not allowed.</summary>
    OwnerCannotBid,
    /// <summary>Unexpected service-level failure.</summary>
    ServiceError
}

/// <summary>
/// Well-known UUIDs and limits for the auction subsystem.
/// </summary>
public static class AuctionConstants
{
    /// <summary>
    /// Placeholder UUID for the grid's auction-services holding account.
    /// Configure the real value in OpenSim.ini under [AuctionService].
    /// </summary>
    public static readonly UUID AuctionServicesAccountID =
        new UUID("10000000-0000-0000-0000-000000000001");

    /// <summary>Maximum weeks ahead an auction may be scheduled.</summary>
    public const int MaxScheduleWeeks = 4;

    /// <summary>Minimum auction duration in hours (48 h = 2 days).</summary>
    public const int MinDurationHours = 48;

    /// <summary>Maximum auction duration in hours (7 days).</summary>
    public const int MaxDurationHours = 7 * 24;

    /// <summary>Allowed bid increments (must be multiples of 10, 10–100).</summary>
    public const int MinBidIncrement = 10;
    public const int MaxBidIncrement = 100;
}

/// <summary>
/// Grid-level auction service.  One instance per Robust server; called by
/// <see cref="IAuctionModule"/> region modules over the internal network.
/// </summary>
public interface IAuctionService
{
    // ── Lifecycle ───────────────────────────────────────────────────────────

    /// <summary>
    /// Schedule a new auction.
    /// Returns the populated <see cref="AuctionRecord"/> (with AuctionID
    /// assigned) on success, or <c>null</c> on validation failure.
    /// </summary>
    AuctionRecord? CreateAuction(
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
        int    commissionPct);

    /// <summary>
    /// Transition a Scheduled auction to Open.
    /// Called by the region module's expiry timer.
    /// Returns false if the auction was not in Scheduled state.
    /// </summary>
    bool OpenAuction(uint auctionID);

    /// <summary>
    /// Cancel an auction.  Allowed only in Scheduled state, or in Open
    /// state when <see cref="AuctionRecord.CurrentBid"/> is zero.
    /// Returns false if cancellation is not permitted.
    /// </summary>
    bool CancelAuction(uint auctionID);

    /// <summary>
    /// Move an Open auction whose CloseTime has passed to Closed, after which
    /// no further bids are accepted.  Returns false if the auction is not Open,
    /// is not yet due, or was already closed by another caller - so exactly
    /// one caller wins the transition and goes on to settle it.
    /// </summary>
    bool CloseAuction(uint auctionID);

    // ── Bidding ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Record a bid and return the result.
    /// The service is responsible for:
    ///   • validating the bid amount,
    ///   • updating CurrentBid / HighBidderID,
    ///   • recording the outgoing escrow refund amount for the displaced
    ///     bidder (returned via <paramref name="refundBidderID"/> /
    ///     <paramref name="refundAmount"/>).
    /// The <em>money movement itself</em> (escrow hold and refund) is
    /// performed by the region module using IMoneyModule.
    /// </summary>
    BidResult PlaceBid(
        uint auctionID,
        UUID bidderID,
        int  amount,
        out UUID refundBidderID,
        out int  refundAmount);

    // ── Settlement ──────────────────────────────────────────────────────────

    /// <summary>
    /// Transition a Closed auction to Settled.
    /// Called by the region module after land has been transferred.
    /// Returns false if the auction was not in Closed state.
    /// </summary>
    bool SettleAuction(uint auctionID);

    /// <summary>
    /// Record settlement progress.  Only ever moves forward: setting a state
    /// lower than the stored one is ignored.  Returns false if the auction
    /// does not exist.
    /// </summary>
    bool SetSettlementState(uint auctionID, AuctionSettlementState state);

    // ── Queries ─────────────────────────────────────────────────────────────

    AuctionRecord? GetAuction(uint auctionID);
    AuctionRecord? GetAuctionByParcel(UUID parcelID);

    /// <summary>Returns all auctions in the given state(s).</summary>
    IList<AuctionRecord> GetAuctionsByStatus(params AuctionStatus[] statuses);

    /// <summary>Returns all auctions for a specific region.</summary>
    IList<AuctionRecord> GetAuctionsByRegion(UUID regionID, params AuctionStatus[] statuses);

    // ── Scheduling ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns auctions whose StartTime has passed but are still Scheduled.
    /// The region module's timer calls this to open them.
    /// </summary>
    IList<AuctionRecord> GetDueToOpen();

    /// <summary>
    /// Returns auctions whose CloseTime has passed but are still Open.
    /// The region module's timer calls this to close and settle them.
    /// </summary>
    IList<AuctionRecord> GetDueToClose();
}

/// <summary>
/// Per-region module interface.  Other modules (e.g. search) request this
/// from the scene to check auction status without depending on the full
/// implementation assembly.
/// </summary>
public interface IAuctionModule
{
    /// <summary>
    /// Start an auction on a parcel in this region.
    /// The caller must have already verified permissions.
    /// Returns the new AuctionID, or 0 on failure.
    /// </summary>
    uint StartAuction(
        ILandObject parcel,
        UUID        adminID,
        DateTime    startTime,
        DateTime    closeTime,
        int         startingBid,
        int         bidIncrement);

    /// <summary>
    /// Place a bid from an in-region viewer client.
    /// Money movement (escrow hold / refund) is handled here.
    /// </summary>
    BidResult PlaceBid(uint auctionID, UUID bidderID, int amount);

    /// <summary>Cancel an auction by ID.</summary>
    bool CancelAuction(uint auctionID);

    /// <summary>Returns the auction record for a given parcel, or null.</summary>
    AuctionRecord? GetAuctionForParcel(UUID parcelID);
}
