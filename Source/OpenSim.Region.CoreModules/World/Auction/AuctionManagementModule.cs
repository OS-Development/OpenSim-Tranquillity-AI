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
using System.Timers;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using Microsoft.Extensions.Logging;

namespace OpenSim.Region.CoreModules.World.Auction;

/// <summary>
/// Per-region module that implements land auctions.
///
/// Responsibilities:
///   • Receives the <c>ViewerStartAuction</c> UDP packet from estate-level
///     clients and initiates an auction via <see cref="IAuctionService"/>.
///   • Runs a periodic timer to open Scheduled auctions and settle Closed
///     ones automatically.
///   • On bid: validates funds via <see cref="IMoneyModule"/>, holds escrow,
///     refunds the displaced bidder.
///   • On settlement: transfers land using the existing
///     <see cref="ILandObject.UpdateLandSold"/> path.
///   • Fixes the parcel overlay so auctioned parcels display as
///     <c>LAND_TYPE_IS_BEING_AUCTIONED</c>.
///   • Exposes <see cref="IAuctionModule"/> so other modules can query state.
///
/// Configuration (OpenSim.ini):
/// <code>
/// [AuctionService]
///     Enabled              = true
///     AuctionServerURI     = "http://robust-host:8003"
///     TimerIntervalSeconds = 60
///     DefaultCommissionPct = 15
/// </code>
/// </summary>
public class AuctionManagementModule : INonSharedRegionModule, IAuctionModule
{
    private static readonly ILogger m_log =
        LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod()!.DeclaringType!);

    private const string LogHeader = "[AUCTION MODULE]";

    // ── Module state ────────────────────────────────────────────────────────
    private Scene?          m_scene;
    private IMoneyModule?   m_money;
    private IDialogModule?  m_dialog;
    private IAuctionService? m_service;

    private bool m_enabled;
    private int  m_defaultCommission = 15;

    private System.Timers.Timer? m_timer;
    private double m_timerIntervalMs = 60_000;

    // Timer ticks must not overlap, and settlement of one auction must not run
    // twice at once (timer retry vs. the 'auction settle' console command).
    private int    m_tickRunning;
    private readonly object m_settleLock = new();
    private readonly Dictionary<uint, DateTime> m_nextSettleAttempt = new();
    private double m_settleRetrySeconds = 300;

    // ── INonSharedRegionModule ──────────────────────────────────────────────

    public string Name => "AuctionManagementModule";
    public Type?  ReplaceableInterface => null;

    public void Initialise(IConfigSource config)
    {
        IConfig cfg = config.Configs["AuctionService"];
        if (cfg is null)
        {
            m_log.LogDebug("{Header}: No [AuctionService] config section – disabled.", LogHeader);
            return;
        }

        m_enabled = cfg.GetBoolean("Enabled", false);
        if (!m_enabled)
        {
            m_log.LogInformation("{Header}: Disabled by configuration.", LogHeader);
            return;
        }

        string uri = cfg.GetString("AuctionServerURI", string.Empty);
        if (string.IsNullOrWhiteSpace(uri))
        {
            m_log.LogError("{Header}: AuctionServerURI not set – disabled.", LogHeader);
            m_enabled = false;
            return;
        }

        m_service           = new RemoteAuctionServiceConnector(uri, ServiceAuth.Create(config, "AuctionService"));
        m_defaultCommission = cfg.GetInt("DefaultCommissionPct", 15);
        m_timerIntervalMs   = cfg.GetDouble("TimerIntervalSeconds", 60) * 1000.0;
        m_settleRetrySeconds = cfg.GetDouble("SettlementRetrySeconds", 300);

        m_log.LogInformation("{Header}: Initialised. Robust={URI}", LogHeader, uri);
    }

    public void AddRegion(Scene scene)
    {
        if (!m_enabled) return;

        m_scene = scene;
        scene.RegisterModuleInterface<IAuctionModule>(this);

        // Register the ViewerStartAuction packet handler for each new client.
        scene.EventManager.OnNewClient           += OnNewClient;
        scene.EventManager.OnClientClosed        += OnClientClosed;

        RegisterCommands();
    }

    public void RegionLoaded(Scene scene)
    {
        if (!m_enabled) return;

        // Fetch optional modules – null-safe throughout.
        m_money  = scene.RequestModuleInterface<IMoneyModule>();
        m_dialog = scene.RequestModuleInterface<IDialogModule>();

        if (m_money is null)
            m_log.LogWarning("{Header}: No IMoneyModule found – bid escrow will not work.", LogHeader);

        // Start the auction lifecycle timer.
        m_timer = new System.Timers.Timer(m_timerIntervalMs) { AutoReset = true };
        m_timer.Elapsed += OnTimerElapsed;
        m_timer.Start();

        m_log.LogInformation("{Header}: Region {Region} loaded. Timer interval {Ms}ms.",
            LogHeader, scene.Name, m_timerIntervalMs);
    }

    public void RemoveRegion(Scene scene)
    {
        if (!m_enabled) return;

        m_timer?.Stop();
        m_timer?.Dispose();
        m_timer = null;

        scene.EventManager.OnNewClient    -= OnNewClient;
        scene.EventManager.OnClientClosed -= OnClientClosed;
        scene.UnregisterModuleInterface<IAuctionModule>(this);
    }

    public void Close() { }

    // ── Client connect / disconnect ─────────────────────────────────────────

    private void OnNewClient(IClientAPI client)
    {
        client.OnViewerStartAuction += HandleViewerStartAuction;
    }

    private void OnClientClosed(UUID clientID, Scene scene)
    {
        // Nothing per-client to clean up; all state is in m_service.
    }

    // ── ViewerStartAuction packet ───────────────────────────────────────────
    //
    // Sent by the viewer when an estate owner or manager right-clicks a
    // parcel → About Land → Auction.  Packet fields:
    //   AgentData  { AgentID, SessionID }
    //   ParcelData { LocalID, SnapshotID }
    //
    // The viewer provides no schedule parameters; we launch with sensible
    // defaults (starts immediately, closes in 7 days).  The admin can use
    // console commands to fine-tune before the start time.

    private void HandleViewerStartAuction(UUID agentID, int localID, UUID snapshotID)
    {
        if (m_scene is null || m_service is null) return;

        // Fired by LLClientView when it receives a ViewerStartAuction UDP packet
        // (low-frequency, PacketType 228) from the viewer.

        // ── Permission check ───────────────────────────────────────────────
        // Administrators only.  The viewer shows its auction button only to a god
        // (llfloaterland.cpp: setVisible(gAgent.isGodlike())) and WhiteCore checks
        // IsGod.  IsAdministrator already encodes this grid's own settings
        // (region_owner_is_admin / region_manager_is_admin / grid admins);
        // testing IsEstateManagerOrOwner as well would bypass them.
        if (!m_scene.Permissions.IsAdministrator(agentID))
        {
            m_scene.GetScenePresence(agentID)?.ControllingClient
                .SendAlertMessage("You do not have permission to start an auction.");
            return;
        }

        ILandObject? land = m_scene.LandChannel.GetLandObject(localID);
        if (land is null)
        {
            m_scene.GetScenePresence(agentID)?.ControllingClient
                .SendAlertMessage("Parcel not found.");
            return;
        }

        // Parcel must not already be on auction.
        if (land.LandData.AuctionID != 0)
        {
            m_scene.GetScenePresence(agentID)?.ControllingClient
                .SendAlertMessage("This parcel already has an active auction.");
            return;
        }

        // Default schedule: start now, close in 7 days.
        var startTime = DateTime.UtcNow.AddMinutes(5);   // 5-min grace for admin to adjust
        var closeTime = startTime.AddHours(AuctionConstants.MaxDurationHours);
        int startBid  = (int)(land.LandData.Area * 0.5); // L$0.50 / m² default
        startBid      = Math.Max(0, startBid);

        StartAuction(land, agentID, startTime, closeTime, startBid,
            bidIncrement: AuctionConstants.MinBidIncrement);
    }

    // ── IAuctionModule ──────────────────────────────────────────────────────

    public uint StartAuction(
        ILandObject parcel,
        UUID        adminID,
        DateTime    startTime,
        DateTime    closeTime,
        int         startingBid,
        int         bidIncrement)
    {
        if (m_scene is null || m_service is null) return 0;

        LandData ld = parcel.LandData;

        // For a group-owned parcel OwnerID is the group's UUID, which no money
        // module can pay out to, so the sale proceeds could never reach the owner.
        if (ld.IsGroupOwned)
        {
            m_log.LogWarning("{Header}: '{Name}' is group-owned; group parcels cannot be auctioned.",
                LogHeader, ld.Name);
            return 0;
        }

        LandData newData = ld.Copy();
        newData.AuthBuyerID  = UUID.Zero;
        newData.Flags       &= ~(uint)(ParcelFlags.ForSale | ParcelFlags.ForSaleObjects |
                                       ParcelFlags.SellParcelObjects);

        // Create the service record first to get an AuctionID.
        var record = m_service.CreateAuction(
            ld.GlobalID,
            ld.LocalID,
            ld.Name,
            ld.Area,
            m_scene.RegionInfo.RegionID,
            ld.OwnerID,       // the real original owner before escrow transfer
            startTime,
            closeTime,
            startingBid,
            bidIncrement,
            m_defaultCommission);

        if (record is null)
        {
            m_log.LogWarning("{Header}: CreateAuction rejected for parcel {Name}.", LogHeader, ld.Name);
            return 0;
        }

        // Stamp the AuctionID onto the parcel and hand it to the escrow account
        // recorded for this auction - the one the service is configured with - so
        // the land and the bidders' money are held by the same account.
        newData.OwnerID   = record.EscrowAccount;
        newData.AuctionID = record.AuctionID;
        m_scene.LandChannel.UpdateLandObject(ld.LocalID, newData);
        RefreshParcelOverlay();

        m_log.LogInformation("{Header}: Auction {ID} scheduled for '{Parcel}' starting {Start}.",
            LogHeader, record.AuctionID, ld.Name, startTime);

        // Notify the region.
        m_dialog?.SendGeneralAlert(
            $"Land auction scheduled: '{ld.Name}' — bidding opens {startTime:u}.");

        return record.AuctionID;
    }

    public BidResult PlaceBid(uint auctionID, UUID bidderID, int amount)
    {
        if (m_scene is null || m_service is null)
            return BidResult.ServiceError;

        // Bids are backed by an escrow hold, so without a money module there is
        // nothing to hold and no bid may be accepted.
        if (m_money is null)
        {
            m_log.LogError("{Header}: PlaceBid refused - no IMoneyModule, cannot hold escrow.", LogHeader);
            return BidResult.ServiceError;
        }

        var record = m_service.GetAuction(auctionID);
        if (record is null || record.Status != AuctionStatus.Open)
            return BidResult.AuctionNotOpen;

        // Cheap pre-checks so a doomed bid never moves money.  The service
        // repeats them under its lock, which is what actually decides races.
        if (bidderID.Equals(record.OriginalOwner))
            return BidResult.OwnerCannotBid;

        int minimumBid = record.CurrentBid > 0
            ? record.CurrentBid + record.BidIncrement
            : record.StartingBid;
        if (amount < minimumBid)
            return BidResult.BidTooLow;

        // 1. Take the escrow hold first.  MoveMoney's result is authoritative
        //    (the money server rejects insufficient funds).  AmountCovered is
        //    deliberately not used: the DTL/NSL module implements it by looking
        //    the bidder up in this simulator's scene, so it fails for any
        //    bidder who is not standing in a local region.
        if (!m_money.MoveMoney(bidderID, record.EscrowAccount, amount,
                MoneyTransactionType.LandAuction,
                $"Escrow hold – auction {auctionID}"))
            return BidResult.InsufficientFunds;

        // 2. Record the bid.
        var result = m_service.PlaceBid(auctionID, bidderID, amount,
            out UUID refundBidder, out int refundAmount);

        if (result != BidResult.Accepted)
        {
            // Lost a race (outbid, closed, cancelled) - give the hold back.
            if (!m_money.MoveMoney(record.EscrowAccount, bidderID, amount,
                    MoneyTransactionType.LandAuction,
                    $"Escrow release – auction {auctionID}"))
                m_log.LogError(
                    "{Header}: MANUAL ACTION: bid rejected ({Result}) but escrow release of L${Amount} to {Bidder} failed (auction {ID}).",
                    LogHeader, result, amount, bidderID, auctionID);
            return result;
        }

        // 3. Refund the displaced high bidder.
        if (!refundBidder.IsZero() && refundAmount > 0)
        {
            if (!m_money.MoveMoney(record.EscrowAccount, refundBidder, refundAmount,
                    MoneyTransactionType.LandAuction,
                    $"Escrow refund – auction {auctionID}"))
                m_log.LogError(
                    "{Header}: MANUAL ACTION: outbid refund of L${Amount} to {Bidder} failed (auction {ID}).",
                    LogHeader, refundAmount, refundBidder, auctionID);
        }

        // Notify the new high bidder (only reaches them if they are in-region).
        m_dialog?.SendAlertToUser(bidderID,
            $"Your bid of L${amount} on '{record.ParcelName}' was accepted. You are the high bidder.");

        m_log.LogInformation("{Header}: Bid L${Amount} on auction {ID} by {Bidder} accepted.",
            LogHeader, amount, auctionID, bidderID);

        return BidResult.Accepted;
    }

    public bool CancelAuction(uint auctionID)
    {
        if (m_service is null) return false;

        var record = m_service.GetAuction(auctionID);
        if (record is null) return false;

        bool ok = m_service.CancelAuction(auctionID);
        if (!ok) return false;

        // Return parcel to original owner.
        ReturnParcelToOwner(record);
        return true;
    }

    public AuctionRecord? GetAuctionForParcel(UUID parcelID)
        => m_service?.GetAuctionByParcel(parcelID);

    // ── Timer: open + settle ────────────────────────────────────────────────

    private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        if (m_scene is null || m_service is null) return;

        // System.Timers.Timer does not wait for a slow callback to finish
        // (remote calls can be slow), so never run two ticks at once.
        if (System.Threading.Interlocked.Exchange(ref m_tickRunning, 1) == 1)
            return;

        try
        {
            OpenDueAuctions();
            CloseDueAuctions();
            RetryClosedAuctions();
        }
        catch (Exception ex)
        {
            m_log.LogError(ex, "{Header}: Timer callback threw.", LogHeader);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref m_tickRunning, 0);
        }
    }

    private void OpenDueAuctions()
    {
        foreach (var record in m_service!.GetDueToOpen())
        {
            // Only act on parcels in this region.
            if (!record.RegionID.Equals(m_scene!.RegionInfo.RegionID)) continue;

            if (m_service.OpenAuction(record.AuctionID))
            {
                m_log.LogInformation("{Header}: Auction {ID} is now open.", LogHeader, record.AuctionID);
                m_dialog?.SendGeneralAlert(
                    $"Auction open: '{record.ParcelName}' — bidding closes {record.CloseTime:u}.");
                RefreshParcelOverlay();
            }
        }
    }

    private void CloseDueAuctions()
    {
        foreach (var due in m_service!.GetDueToClose())
        {
            if (!due.RegionID.Equals(m_scene!.RegionInfo.RegionID)) continue;

            // Exactly one caller wins the Open -> Closed transition.  Once it
            // has, the service accepts no more bids, so the record is final.
            // Without this step the auction stayed Open forever and every timer
            // tick re-ran settlement (transfer + payout) again.
            if (!m_service.CloseAuction(due.AuctionID)) continue;

            // Re-read: a bid may have landed between GetDueToClose and
            // CloseAuction, so 'due' can be stale.
            var final = m_service.GetAuction(due.AuctionID);
            if (final is null)
            {
                m_log.LogError("{Header}: Auction {ID} vanished after closing.", LogHeader, due.AuctionID);
                continue;
            }

            TrySettle(final);
        }
    }

    // Serialised settlement attempt; remembers when to try again if it does not
    // finish (a failure leaves the auction Closed).
    private void TrySettle(AuctionRecord record)
    {
        lock (m_settleLock)
        {
            m_nextSettleAttempt[record.AuctionID] = DateTime.UtcNow.AddSeconds(m_settleRetrySeconds);
            SettleClosedAuction(record);
        }
    }

    // Closed auctions that are still here did not finish settling (parcel not
    // found, payout failed, region was down...).  Settlement resumes from the
    // saved SettlementState, so repeating it is safe.
    private void RetryClosedAuctions()
    {
        var now         = DateTime.UtcNow;
        var stillClosed = new HashSet<uint>();

        // The remote connector does not apply the status filter (the Robust
        // handler returns Scheduled/Open/Closed), so filter here as well.
        foreach (var r in m_service!.GetAuctionsByRegion(
                     m_scene!.RegionInfo.RegionID, AuctionStatus.Closed))
        {
            if (r.Status != AuctionStatus.Closed) continue;
            stillClosed.Add(r.AuctionID);

            lock (m_settleLock)
            {
                if (m_nextSettleAttempt.TryGetValue(r.AuctionID, out DateTime next) && now < next)
                    continue;
            }

            m_log.LogWarning("{Header}: Retrying settlement of auction {ID} (progress: {State}).",
                LogHeader, r.AuctionID, r.SettlementState);
            TrySettle(r);
        }

        // Forget auctions that are no longer Closed.
        lock (m_settleLock)
        {
            var stale = new List<uint>();
            foreach (uint id in m_nextSettleAttempt.Keys)
                if (!stillClosed.Contains(id))
                    stale.Add(id);
            foreach (uint id in stale)
                m_nextSettleAttempt.Remove(id);
        }
    }

    private void MarkSettlement(AuctionRecord record, AuctionSettlementState state)
    {
        if (!m_service!.SetSettlementState(record.AuctionID, state))
            m_log.LogWarning(
                "{Header}: Could not record settlement progress {State} for auction {ID}.",
                LogHeader, state, record.AuctionID);
    }

    private void SettleClosedAuction(AuctionRecord record)
    {
        if (m_scene is null || m_service is null) return;

        // Find the parcel by GlobalID.  LocalIDs are assigned when the region
        // loads and change on restart or subdivide/join, so the ParcelLocalID
        // saved at auction start must not be used to locate it now.
        ILandObject? land = m_scene.LandChannel.GetLandObject(record.ParcelID);
        if (land is null)
        {
            m_log.LogError(
                "{Header}: Cannot settle auction {ID} – parcel {Parcel} ('{Name}') not found. Left in Closed state for manual handling.",
                LogHeader, record.AuctionID, record.ParcelID, record.ParcelName);
            return;
        }

        // The parcel must still be held by the escrow account it was handed to.
        // If its ownership changed behind our back (the LandManagementModule now
        // refuses abandon/reclaim/force-owner/divide/join during an auction, but
        // other code paths exist) do not hand land over, or back, on stale data.
        // Once land has been transferred the owner is the winner, so only check
        // before that step.
        if (record.SettlementState < AuctionSettlementState.LandTransferred &&
            !land.LandData.OwnerID.Equals(record.EscrowAccount))
        {
            if (record.CurrentBid == 0 || record.HighBidderID.IsZero())
            {
                m_log.LogWarning(
                    "{Header}: Auction {ID} had no bids and parcel '{Name}' is no longer held by escrow (owner {Owner}); nothing to return.",
                    LogHeader, record.AuctionID, record.ParcelName, land.LandData.OwnerID);
                m_service.SettleAuction(record.AuctionID);
            }
            else
            {
                m_log.LogError(
                    "{Header}: MANUAL ACTION: auction {ID} has a winning bid of L${Bid} by {Winner} but parcel '{Name}' is no longer held by escrow (owner {Owner}). Land NOT transferred; the bid is still in escrow and must be refunded manually. Left Closed.",
                    LogHeader, record.AuctionID, record.CurrentBid, record.HighBidderID, record.ParcelName, land.LandData.OwnerID);
            }
            return;
        }

        if (record.CurrentBid == 0 || record.HighBidderID.IsZero())
        {
            // No bids — return parcel to original owner.
            m_log.LogInformation("{Header}: Auction {ID} closed with no bids.", LogHeader, record.AuctionID);
            ReturnParcelToOwner(record);
            m_service.SettleAuction(record.AuctionID);
            return;
        }

        // ── Transfer land (skipped if an earlier attempt already did it) ────
        // Use the existing path: UpdateLandSold clears the sale flags, hands
        // over ownership, persists via UpdateLandObject and refreshes prim
        // counts.  Passing AuctionID 0 clears the auction marker in the same
        // update instead of needing a second one.
        if (record.SettlementState < AuctionSettlementState.LandTransferred)
        {
            land.UpdateLandSold(
                record.HighBidderID,
                UUID.Zero,          // no group
                false,
                0,
                record.CurrentBid,
                record.ParcelArea);

            MarkSettlement(record, AuctionSettlementState.LandTransferred);
        }

        // ── Money: pay original owner minus commission ──────────────────────
        if (m_money is null)
        {
            m_log.LogError(
                "{Header}: MANUAL ACTION: land of auction {ID} transferred to {Winner} but there is no IMoneyModule to pay {Owner}. Left Closed.",
                LogHeader, record.AuctionID, record.HighBidderID, record.OriginalOwner);
            RefreshParcelOverlay();
            return;
        }

        int commission  = (int)((long)record.CurrentBid * record.CommissionPct / 100);
        int ownerPayout = record.CurrentBid - commission;

        // Skipped if an earlier attempt already paid.  Residual risk: a crash in
        // the instant between a successful payout and MarkSettlement below would
        // pay once more on retry (the money server offers no idempotency key).
        if (record.SettlementState < AuctionSettlementState.OwnerPaid)
        {
            if (ownerPayout > 0 &&
                !m_money.MoveMoney(record.EscrowAccount, record.OriginalOwner, ownerPayout,
                    MoneyTransactionType.LandAuction,
                    $"Auction {record.AuctionID} payout (commission {record.CommissionPct}%)"))
            {
                m_log.LogError(
                    "{Header}: MANUAL ACTION: land of auction {ID} transferred to {Winner} but payout of L${Payout} to {Owner} FAILED. Left Closed; will retry.",
                    LogHeader, record.AuctionID, record.HighBidderID, ownerPayout, record.OriginalOwner);
                RefreshParcelOverlay();
                return;
            }

            MarkSettlement(record, AuctionSettlementState.OwnerPaid);
        }

        m_service.SettleAuction(record.AuctionID);
        RefreshParcelOverlay();

        m_log.LogInformation("{Header}: Auction {ID} settled. Winner={Winner} Bid=L${Bid}.",
            LogHeader, record.AuctionID, record.HighBidderID, record.CurrentBid);

        m_dialog?.SendAlertToUser(record.HighBidderID,
            $"Congratulations! You won the auction for '{record.ParcelName}' with a bid of L${record.CurrentBid}.");
        m_dialog?.SendAlertToUser(record.OriginalOwner,
            $"Your auction for '{record.ParcelName}' has closed. " +
            $"Winning bid: L${record.CurrentBid} — payout: L${ownerPayout}.");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a parcel to its original owner and clears the AuctionID.
    /// Used on cancel or no-bid close.
    /// </summary>
    private void ReturnParcelToOwner(AuctionRecord record)
    {
        if (m_scene is null) return;

        // By GlobalID, not the saved LocalID (see SettleClosedAuction).
        ILandObject? land = m_scene.LandChannel.GetLandObject(record.ParcelID);
        if (land is null)
        {
            m_log.LogWarning("{Header}: ReturnParcelToOwner – parcel {Parcel} ('{Name}') not found.",
                LogHeader, record.ParcelID, record.ParcelName);
            return;
        }

        LandData newData = land.LandData.Copy();
        newData.OwnerID   = record.OriginalOwner;
        newData.AuctionID = 0;
        m_scene.LandChannel.UpdateLandObject(land.LandData.LocalID, newData);
        RefreshParcelOverlay();

        m_log.LogInformation("{Header}: Parcel '{Name}' returned to {Owner}.",
            LogHeader, record.ParcelName, record.OriginalOwner);
    }

    /// <summary>
    /// Pushes a fresh parcel overlay to all clients in the region.
    /// The overlay bytes are built by LandManagementModule; we trigger the
    /// update the same way a land sale does.
    /// </summary>
    private void RefreshParcelOverlay()
    {
        if (m_scene is null) return;
        ILandChannel? lc = m_scene.LandChannel;
        m_scene.ForEachClient(client =>
        {
            if (client.SceneAgent?.PresenceType != PresenceType.Npc)
                lc.SendParcelsOverlay(client);
        });
    }

    // ── Console commands ────────────────────────────────────────────────────

    private void RegisterCommands()
    {
        var commands = MainConsole.Instance.Commands;

        commands.AddCommand(
            "Auction", false,
            "auction list",
            "auction list",
            "List all active auctions in the current region.",
            HandleListCommand);

        commands.AddCommand(
            "Auction", false,
            "auction start <localID> <startBid> <bidIncrement> <durationHours>",
            "auction start <localID> <startBid> <bidIncrement> <durationHours>",
            "Schedule an auction for the parcel with the given local ID.\n" +
            "  startBid      – minimum opening bid in L$\n" +
            "  bidIncrement  – must be 10–100 and a multiple of 10\n" +
            "  durationHours – 48–168 (2–7 days)",
            HandleStartCommand);

        commands.AddCommand(
            "Auction", false,
            "auction cancel <auctionID>",
            "auction cancel <auctionID>",
            "Cancel an auction (only if no bids have been placed).",
            HandleCancelCommand);

        commands.AddCommand(
            "Auction", false,
            "auction settle <auctionID>",
            "auction settle <auctionID>",
            "Retry settlement of a Closed auction whose land transfer or payout did not finish.\n" +
            "Finished steps are not repeated.",
            HandleSettleCommand);

        commands.AddCommand(
            "Auction", false,
            "auction bid <auctionID> <bidderUUID> <amount>",
            "auction bid <auctionID> <bidderUUID> <amount>",
            "Place a bid on behalf of a resident (admin use only).",
            HandleBidCommand);
    }

    private void HandleListCommand(string module, string[] args)
    {
        if (!IsConsoleForThisRegion()) return;
        if (m_service is null || m_scene is null) return;

        var records = m_service.GetAuctionsByRegion(
            m_scene.RegionInfo.RegionID,
            AuctionStatus.Scheduled, AuctionStatus.Open, AuctionStatus.Closed);

        if (records.Count == 0)
        {
            MainConsole.Instance.Output("No active auctions in {0}.", m_scene.Name);
            return;
        }

        MainConsole.Instance.Output("Auctions in {0}:", m_scene.Name);
        // Console.Output(format, args) goes through string.Format, which rejects
        // named placeholders like {ID} (and would choke on braces in a parcel
        // name).  A single interpolated string is printed as-is.
        foreach (var r in records)
            MainConsole.Instance.Output(
                $"  [{r.AuctionID}] '{r.ParcelName}' Status={r.Status} Settlement={r.SettlementState} " +
                $"CurrentBid=L${r.CurrentBid} HighBidder={r.HighBidderID} Closes={r.CloseTime:u}");
    }

    private void HandleStartCommand(string module, string[] args)
    {
        if (!IsConsoleForThisRegion() || m_scene is null) return;

        if (args.Length < 6 ||
            !int.TryParse(args[2], out int localID)        ||
            !int.TryParse(args[3], out int startBid)       ||
            !int.TryParse(args[4], out int bidIncrement)   ||
            !double.TryParse(args[5], out double durationH))
        {
            MainConsole.Instance.Output(
                "Usage: auction start <localID> <startBid> <bidIncrement> <durationHours>");
            return;
        }

        ILandObject? land = m_scene.LandChannel.GetLandObject(localID);
        if (land is null)
        {
            MainConsole.Instance.Output("Parcel {0} not found.", localID);
            return;
        }

        var startTime = DateTime.UtcNow.AddMinutes(1);
        var closeTime = startTime.AddHours(durationH);

        uint id = StartAuction(land,
            m_scene.RegionInfo.EstateSettings.EstateOwner,
            startTime, closeTime, startBid, bidIncrement);

        MainConsole.Instance.Output(id > 0
            ? $"Auction {id} scheduled for '{land.LandData.Name}'."
            : "Auction creation failed – check logs.");
    }

    private void HandleSettleCommand(string module, string[] args)
    {
        if (!IsConsoleForThisRegion() || m_service is null || m_scene is null) return;

        if (args.Length < 3 || !uint.TryParse(args[2], out uint id))
        {
            MainConsole.Instance.Output("Usage: auction settle <auctionID>");
            return;
        }

        var record = m_service.GetAuction(id);
        if (record is null || !record.RegionID.Equals(m_scene.RegionInfo.RegionID))
        {
            MainConsole.Instance.Output($"Auction {id} not found in this region.");
            return;
        }
        if (record.Status != AuctionStatus.Closed)
        {
            MainConsole.Instance.Output($"Auction {id} is {record.Status}, not Closed - nothing to settle.");
            return;
        }

        TrySettle(record);

        var after = m_service.GetAuction(id);
        MainConsole.Instance.Output(
            $"Auction {id} is now {after?.Status} (settlement progress: {after?.SettlementState}). See the log for details.");
    }

    private void HandleCancelCommand(string module, string[] args)
    {
        if (!IsConsoleForThisRegion()) return;

        if (args.Length < 3 || !uint.TryParse(args[2], out uint id))
        {
            MainConsole.Instance.Output("Usage: auction cancel <auctionID>");
            return;
        }

        bool ok = CancelAuction(id);
        MainConsole.Instance.Output(ok
            ? $"Auction {id} cancelled."
            : $"Could not cancel auction {id} (may have bids or not exist).");
    }

    private void HandleBidCommand(string module, string[] args)
    {
        if (!IsConsoleForThisRegion()) return;

        if (args.Length < 5 ||
            !uint.TryParse(args[2], out uint id)       ||
            !UUID.TryParse(args[3], out UUID bidder)   ||
            !int.TryParse(args[4], out int amount))
        {
            MainConsole.Instance.Output("Usage: auction bid <auctionID> <bidderUUID> <amount>");
            return;
        }

        var result = PlaceBid(id, bidder, amount);
        MainConsole.Instance.Output($"Bid result: {result}");
    }

    private bool IsConsoleForThisRegion()
        => MainConsole.Instance.ConsoleScene is null ||
           MainConsole.Instance.ConsoleScene == m_scene;
}
