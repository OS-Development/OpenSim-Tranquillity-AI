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

using System.Data;
using System.Reflection;
using MySqlConnector;
using OpenMetaverse;
using OpenSim.Framework;

using Microsoft.Extensions.Logging;

namespace OpenSim.Data.MySQL;

/// <summary>
/// MySQL data-access layer for the auction subsystem.
/// One instance per Robust server process; all methods are synchronous and
/// safe for concurrent calls (each call opens its own connection from the
/// pool).
/// </summary>
public class MySQLAuctionData
{
    private static readonly ILogger m_log =
        LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod()!.DeclaringType!);

    private readonly string m_connectionString;

    // ── Column → property names match the migration exactly. ───────────────
    private const string SelectColumns =
        "AuctionID, ParcelID, ParcelLocalID, ParcelName, ParcelArea, " +
        "RegionID, OriginalOwner, EscrowAccount, " +
        "StartTime, CloseTime, StartingBid, BidIncrement, " +
        "CurrentBid, HighBidderID, EscrowAmount, CommissionPct, " +
        "Status, CreatedAt, UpdatedAt, SettlementState";

    public MySQLAuctionData(string connectionString)
    {
        m_connectionString = connectionString;

        // Same pattern as MySQLGenericTableHandler: migrations get their own
        // opened connection, disposed afterwards.
        using (MySqlConnection dbcon = new MySqlConnection(connectionString))
        {
            dbcon.Open();
            Migration m = new Migration(dbcon, Assembly.GetExecutingAssembly(), "AuctionStore");
            m.Update();
        }
    }

    // ── CRUD ────────────────────────────────────────────────────────────────

    /// <summary>Insert a new auction row and return the assigned AuctionID.</summary>
    public uint Create(AuctionRecord r)
    {
        using var conn = new MySqlConnection(m_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO auctions
                (ParcelID, ParcelLocalID, ParcelName, ParcelArea,
                 RegionID, OriginalOwner, EscrowAccount,
                 StartTime, CloseTime, StartingBid, BidIncrement,
                 CurrentBid, HighBidderID, EscrowAmount, CommissionPct, Status)
            VALUES
                (?ParcelID, ?ParcelLocalID, ?ParcelName, ?ParcelArea,
                 ?RegionID, ?OriginalOwner, ?EscrowAccount,
                 ?StartTime, ?CloseTime, ?StartingBid, ?BidIncrement,
                 ?CurrentBid, ?HighBidderID, ?EscrowAmount, ?CommissionPct, ?Status);
            SELECT LAST_INSERT_ID();";

        BindParams(cmd, r);
        var id = Convert.ToUInt32(cmd.ExecuteScalar());
        r.AuctionID = id;
        return id;
    }

    /// <summary>Persist all mutable fields of an existing record.</summary>
    public bool Update(AuctionRecord r)
    {
        using var conn = new MySqlConnection(m_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE auctions SET
                ParcelName    = ?ParcelName,
                ParcelArea    = ?ParcelArea,
                StartTime     = ?StartTime,
                CloseTime     = ?CloseTime,
                StartingBid   = ?StartingBid,
                BidIncrement  = ?BidIncrement,
                CurrentBid    = ?CurrentBid,
                HighBidderID  = ?HighBidderID,
                EscrowAmount  = ?EscrowAmount,
                CommissionPct = ?CommissionPct,
                Status        = ?Status,
                UpdatedAt     = UTC_TIMESTAMP()
            WHERE AuctionID = ?AuctionID;";

        BindParams(cmd, r);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ── Queries ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Atomically move an auction from one status to another.  Returns false
    /// when the row was not in the expected <paramref name="from"/> status
    /// (another caller got there first).
    /// </summary>
    public bool TransitionStatus(uint auctionID, AuctionStatus from, AuctionStatus to)
    {
        using var conn = new MySqlConnection(m_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE auctions
            SET Status = ?To, UpdatedAt = UTC_TIMESTAMP()
            WHERE AuctionID = ?AuctionID AND Status = ?From;";
        cmd.Parameters.AddWithValue("?AuctionID", auctionID);
        cmd.Parameters.AddWithValue("?From", (int)from);
        cmd.Parameters.AddWithValue("?To", (int)to);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Forward-only update of the settlement progress.  The WHERE clause makes
    /// a lower value a no-op that still reports success (the row exists).
    /// </summary>
    public bool SetSettlementState(uint auctionID, int state)
    {
        using var conn = new MySqlConnection(m_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE auctions SET SettlementState = GREATEST(SettlementState, ?State),
                                UpdatedAt = UTC_TIMESTAMP()
            WHERE AuctionID = ?AuctionID;";
        cmd.Parameters.AddWithValue("?AuctionID", auctionID);
        cmd.Parameters.AddWithValue("?State", state);
        return cmd.ExecuteNonQuery() > 0;
    }

    public AuctionRecord? GetByID(uint auctionID)
    {
        using var conn = new MySqlConnection(m_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM auctions WHERE AuctionID = ?AuctionID LIMIT 1;";
        cmd.Parameters.AddWithValue("?AuctionID", auctionID);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadRecord(r) : null;
    }

    public AuctionRecord? GetByParcel(UUID parcelID)
    {
        using var conn = new MySqlConnection(m_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Most recent non-terminal record wins.
        cmd.CommandText = $@"
            SELECT {SelectColumns} FROM auctions
            WHERE ParcelID = ?ParcelID
              AND Status NOT IN ({(int)AuctionStatus.Settled},{(int)AuctionStatus.Cancelled})
            ORDER BY CreatedAt DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("?ParcelID", parcelID.ToString());
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadRecord(r) : null;
    }

    public IList<AuctionRecord> GetByStatus(IEnumerable<AuctionStatus> statuses)
    {
        var statusInts = string.Join(",", statuses.Select(s => (int)s));
        if (string.IsNullOrEmpty(statusInts))
            return [];

        using var conn = new MySqlConnection(m_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM auctions WHERE Status IN ({statusInts}) ORDER BY CreatedAt;";
        using var r = cmd.ExecuteReader();
        return ReadAll(r);
    }

    public IList<AuctionRecord> GetByRegion(UUID regionID, IEnumerable<AuctionStatus> statuses)
    {
        var statusInts = string.Join(",", statuses.Select(s => (int)s));
        if (string.IsNullOrEmpty(statusInts))
            return [];

        using var conn = new MySqlConnection(m_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT {SelectColumns} FROM auctions
            WHERE RegionID = ?RegionID AND Status IN ({statusInts})
            ORDER BY CreatedAt;";
        cmd.Parameters.AddWithValue("?RegionID", regionID.ToString());
        using var r = cmd.ExecuteReader();
        return ReadAll(r);
    }

    public IList<AuctionRecord> GetDueToOpen()
    {
        using var conn = new MySqlConnection(m_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT {SelectColumns} FROM auctions
            WHERE Status = {(int)AuctionStatus.Scheduled}
              AND StartTime <= UTC_TIMESTAMP();";
        using var r = cmd.ExecuteReader();
        return ReadAll(r);
    }

    public IList<AuctionRecord> GetDueToClose()
    {
        using var conn = new MySqlConnection(m_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT {SelectColumns} FROM auctions
            WHERE Status = {(int)AuctionStatus.Open}
              AND CloseTime <= UTC_TIMESTAMP();";
        using var r = cmd.ExecuteReader();
        return ReadAll(r);
    }

    // ── Internal helpers ────────────────────────────────────────────────────

    private static void BindParams(MySqlCommand cmd, AuctionRecord r)
    {
        cmd.Parameters.AddWithValue("?AuctionID",     r.AuctionID);
        cmd.Parameters.AddWithValue("?ParcelID",      r.ParcelID.ToString());
        cmd.Parameters.AddWithValue("?ParcelLocalID", r.ParcelLocalID);
        cmd.Parameters.AddWithValue("?ParcelName",    r.ParcelName);
        cmd.Parameters.AddWithValue("?ParcelArea",    r.ParcelArea);
        cmd.Parameters.AddWithValue("?RegionID",      r.RegionID.ToString());
        cmd.Parameters.AddWithValue("?OriginalOwner", r.OriginalOwner.ToString());
        cmd.Parameters.AddWithValue("?EscrowAccount", r.EscrowAccount.ToString());
        cmd.Parameters.AddWithValue("?StartTime",     r.StartTime.ToUniversalTime());
        cmd.Parameters.AddWithValue("?CloseTime",     r.CloseTime.ToUniversalTime());
        cmd.Parameters.AddWithValue("?StartingBid",   r.StartingBid);
        cmd.Parameters.AddWithValue("?BidIncrement",  r.BidIncrement);
        cmd.Parameters.AddWithValue("?CurrentBid",    r.CurrentBid);
        cmd.Parameters.AddWithValue("?HighBidderID",  r.HighBidderID.ToString());
        cmd.Parameters.AddWithValue("?EscrowAmount",  r.EscrowAmount);
        cmd.Parameters.AddWithValue("?CommissionPct", r.CommissionPct);
        cmd.Parameters.AddWithValue("?Status",        (int)r.Status);
    }

    private static AuctionRecord ReadRecord(IDataReader r)
    {
        return new AuctionRecord
        {
            AuctionID     = Convert.ToUInt32(r["AuctionID"]),
            ParcelID      = UUID.Parse((string)r["ParcelID"]),
            ParcelLocalID = Convert.ToInt32(r["ParcelLocalID"]),
            ParcelName    = (string)r["ParcelName"],
            ParcelArea    = Convert.ToInt32(r["ParcelArea"]),
            RegionID      = UUID.Parse((string)r["RegionID"]),
            OriginalOwner = UUID.Parse((string)r["OriginalOwner"]),
            EscrowAccount = UUID.Parse((string)r["EscrowAccount"]),
            StartTime     = Convert.ToDateTime(r["StartTime"]),
            CloseTime     = Convert.ToDateTime(r["CloseTime"]),
            StartingBid   = Convert.ToInt32(r["StartingBid"]),
            BidIncrement  = Convert.ToInt32(r["BidIncrement"]),
            CurrentBid    = Convert.ToInt32(r["CurrentBid"]),
            HighBidderID  = UUID.Parse((string)r["HighBidderID"]),
            EscrowAmount  = Convert.ToInt32(r["EscrowAmount"]),
            CommissionPct = Convert.ToInt32(r["CommissionPct"]),
            Status        = (AuctionStatus)Convert.ToInt32(r["Status"]),
            CreatedAt     = Convert.ToDateTime(r["CreatedAt"]),
            UpdatedAt     = Convert.ToDateTime(r["UpdatedAt"]),
            SettlementState = (AuctionSettlementState)Convert.ToInt32(r["SettlementState"])
        };
    }

    private static List<AuctionRecord> ReadAll(IDataReader r)
    {
        var list = new List<AuctionRecord>();
        while (r.Read())
            list.Add(ReadRecord(r));
        return list;
    }
}
