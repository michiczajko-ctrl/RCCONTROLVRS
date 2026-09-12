namespace VRS.RaceControl.Shared.Models;

public enum EconomyEntryKind
{
    OpeningBalance,
    SponsorBase,
    SponsorPerCar,
    EntryFee,
    Salary,
    SigningFee,
    ContractBreachFee,
    Fine,
    PenaltyDnf,
    PenaltyDns,
    PenaltyDsq,
    Bonus,
    PrizeMoney,
    ManualAdjustment,
    Reversal
}

public enum EconomyActorType { HostOperator, TeamCeo, System }

public enum PrizePoolMode { FromEntryFees, Fixed, Off }

public enum ContractStatus { Pending, Active, Completed, Terminated, Breached }

/// <summary>
/// One append-only ledger row — the sole source of truth for a team's finances (see
/// EconomyService.RecomputeBalance: TeamEconomy.Balance is a materialized view of these
/// rows, never edited directly). A correction is always a new Reversal row referencing the
/// original via ReversesEntryId, never an edit or delete — see EconomyLedgerStore, which
/// has no Update/Delete method at all.
/// </summary>
public sealed class EconomyLedgerEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Unique per logical operation — see EconomyAmountRules.Build*IdempotencyKey.
    /// A duplicate key is silently skipped by EconomyLedgerStore.AppendBatch.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;
    public string LeagueId { get; set; } = string.Empty;
    public string SeasonId { get; set; } = "default";
    public string TeamId { get; set; } = string.Empty;
    public string TeamNameSnapshot { get; set; } = string.Empty;
    public EconomyEntryKind Kind { get; set; }
    /// <summary>Signed — negative is a charge against the team, positive a credit. The
    /// sign is always system-derived from Kind (EconomyAmountRules.SignFor), except for
    /// ManualAdjustment and Reversal, the only two kinds where a caller supplies it
    /// directly (see their doc comments) — the UI itself never accepts a signed value.</summary>
    public long Amount { get; set; }
    /// <summary>Snapshot of the team's balance immediately after this entry — for audit
    /// display only, never used to derive the authoritative balance (that is always
    /// re-summed from the ledger, see EconomyService.RecomputeBalance).</summary>
    public long BalanceAfter { get; set; }
    public string? RoundId { get; set; }
    public string? RoundNameSnapshot { get; set; }
    public string? DriverLogin { get; set; }
    public string? ContractId { get; set; }
    public string? IncidentReportId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public EconomyActorType ActorType { get; set; } = EconomyActorType.HostOperator;
    public string ActorLogin { get; set; } = "HOST";
    /// <summary>Set only when Kind == Reversal — the entry this one reverses. At most one
    /// reversal may exist per original entry (EconomyService.ReverseEntry enforces this).</summary>
    public string? ReversesEntryId { get; set; }
    public string PreviousEntryHash { get; set; } = string.Empty;
    public string EntryHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A team's balance — a LOCAL, NEVER-SYNCHRONIZED materialized view derived from the
/// (synchronized) ledger. Recomputed by EconomyService after every mutation and after
/// every cloud pull. See LeagueProfileStore-era precedent for why: syncing a balance
/// directly would need last-write-wins semantics that silently lose one Host's operations
/// when two Hosts write concurrently — recomputing from an append-only ledger instead
/// makes that class of conflict impossible rather than merely rare.
/// </summary>
public sealed class TeamEconomy
{
    public string TeamId { get; set; } = string.Empty;
    public string LeagueId { get; set; } = string.Empty;
    public string SeasonId { get; set; } = "default";
    public long Balance { get; set; }
    public int LedgerEntryCount { get; set; }
    public string LastEntryId { get; set; } = string.Empty;
    public string LastEntryHash { get; set; } = string.Empty;
    public int ConsecutiveNegativeRounds { get; set; }
    public DateTime RecomputedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class BonusPreset
{
    public string Name { get; set; } = string.Empty;
    public long Amount { get; set; }
}

/// <summary>
/// A CEO/economy-manager-proposed contract, awaiting Host operator approval (plan Faza 8 —
/// "CEO składa ofertę, Host zatwierdza", same shape as the existing TeamJoinRequestEntry/
/// LeagueProfile.PendingJoinRequests queue pattern). Lives in LeagueEconomySettings.
/// PendingOffers rather than a dedicated store: low-frequency, LWW-safe data that already
/// rides the EconomySettings sync path built in Faza 4 — no new table needed. Removed from
/// the list on approval or rejection, never soft-deleted (matches PendingJoinRequests).
/// </summary>
public sealed class ContractOffer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TeamId { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public string DriverLogin { get; set; } = string.Empty;
    public long SalaryPerRound { get; set; }
    public long SigningFee { get; set; }
    public int RoundsTotal { get; set; }
    public string ProposedByLogin { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Per-round overrides for a league's economy settings — any null field
/// inherits from LeagueEconomySettings at the moment a round is closed (see
/// LeagueEconomySettings.FindOverride), and that resolved value is then frozen into
/// every ledger entry the round produces, so a later settings change never rewrites
/// history.</summary>
public sealed class RoundEconomyOverride
{
    public string RoundId { get; set; } = string.Empty;
    public long? EntryFeePerCar { get; set; }
    public long? SponsorBasePerRound { get; set; }
    public long? PenaltyDns { get; set; }
    public long? PenaltyDsq { get; set; }
    public long? PenaltyDnf { get; set; }
    public long? PrizePool { get; set; }
}

public sealed class LeagueEconomySettings
{
    public string LeagueId { get; set; } = string.Empty;
    public string SeasonId { get; set; } = "default";
    public bool Enabled { get; set; }
    public string CurrencyCode { get; set; } = "VRC";
    public string CurrencyName { get; set; } = "kredyt";
    public long StartingBalance { get; set; } = 150_000;
    public long EntryFeePerCar { get; set; } = 2_500;
    public long SponsorBasePerRound { get; set; } = 8_000;
    public long SponsorPerCarPerRound { get; set; } = 1_000;
    public long PenaltyDnf { get; set; } = 0;
    public long PenaltyDns { get; set; } = 1_250;
    public long PenaltyDsq { get; set; } = 2_500;
    public long MinSalaryPerRound { get; set; } = 500;
    public long MaxSalaryPerRound { get; set; } = 20_000;
    public long TeamSalaryCapPerRound { get; set; } = 20_000;
    public int MaxContractsPerTeam { get; set; } = 4;
    public double BreachMultiplierTeam { get; set; } = 0.5;
    public double BreachMultiplierDriver { get; set; } = 1.0;
    public int WarningRoundsCovered { get; set; } = 3;
    public int CriticalRoundsCovered { get; set; } = 1;
    public long MaxManualAdjustment { get; set; } = 50_000;
    public int InsolvencyGraceRounds { get; set; } = 2;
    public PrizePoolMode PrizePoolMode { get; set; } = PrizePoolMode.FromEntryFees;
    public List<BonusPreset> BonusPresets { get; set; } = new();
    public List<RoundEconomyOverride> RoundOverrides { get; set; } = new();
    /// <summary>Rounds already closed via EconomyService.CloseRound — belt-and-suspenders
    /// alongside the ledger's own idempotency keys, and what RemoveSeasonRace checks
    /// before allowing a round to be deleted from the calendar (see MainViewModel).</summary>
    public List<string> SettledRoundIds { get; set; } = new();
    /// <summary>CEO/manager-proposed contracts awaiting Host approval — see <see cref="ContractOffer"/>.</summary>
    public List<ContractOffer> PendingOffers { get; set; } = new();

    public RoundEconomyOverride? FindOverride(string? roundId) =>
        string.IsNullOrWhiteSpace(roundId)
            ? null
            : RoundOverrides.FirstOrDefault(o => string.Equals(o.RoundId, roundId, StringComparison.Ordinal));
}

/// <summary>
/// A driver's paid contract with a team — seasonal salary with a per-round installment
/// (see EconomyService.CloseRound), not payment gated on actual round attendance, since
/// this app has no automatic results/attendance tracking to gate on.
/// </summary>
public sealed class TeamContract
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string LeagueId { get; set; } = string.Empty;
    public string SeasonId { get; set; } = "default";
    public string TeamId { get; set; } = string.Empty;
    public string DriverLogin { get; set; } = string.Empty;
    public string? DriverAccountId { get; set; }
    public long SalaryPerRound { get; set; }
    public long SigningFee { get; set; }
    public string? StartRoundId { get; set; }
    public int RoundsTotal { get; set; }
    public int RoundsPaid { get; set; }
    public ContractStatus Status { get; set; } = ContractStatus.Active;
    public DateTime SignedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAtUtc { get; set; }
    public string TerminationReason { get; set; } = string.Empty;
    public string CreatedByActor { get; set; } = "HOST";

    public bool IsActive => Status == ContractStatus.Active;
    public int RoundsRemaining => Math.Max(0, RoundsTotal - RoundsPaid);
}
