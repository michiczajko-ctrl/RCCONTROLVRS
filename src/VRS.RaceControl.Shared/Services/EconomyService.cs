using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public enum EconomyChangeKind
{
    RoundSettled,
    PenaltyApplied,
    BonusGranted,
    ContractSigned,
    ContractTerminated,
    ManualAdjustment,
    Reversal
}

public sealed class EconomyChangedEventArgs
{
    public string LeagueId { get; init; } = string.Empty;
    public string SeasonId { get; init; } = "default";
    public IReadOnlyList<string> AffectedTeamIds { get; init; } = Array.Empty<string>();
    public EconomyChangeKind Kind { get; init; }
    public string? RoundId { get; init; }
    public int EntryCount { get; init; }
    public string ActorLogin { get; init; } = "HOST";
}

public sealed record EconomyOperationResult(
    bool Success, string? Error, IReadOnlyList<string> EntryIds, long NewBalance, int SkippedAsDuplicate)
{
    public static EconomyOperationResult Succeeded(IReadOnlyList<string> entryIds, long newBalance, int skipped = 0) =>
        new(true, null, entryIds, newBalance, skipped);

    public static EconomyOperationResult Failed(string error) =>
        new(false, error, Array.Empty<string>(), 0, 0);
}

public sealed record EconomyHealthReport(
    IReadOnlyList<string> TeamsWithMismatchedBalance, string? BrokenChainEntryId, bool IsLedgerQuarantined)
{
    public bool IsHealthy => TeamsWithMismatchedBalance.Count == 0 && BrokenChainEntryId == null && !IsLedgerQuarantined;
}

/// <summary>
/// The only entry point for any financial mutation — mirrors UserAccountStore's
/// result-object style (never throws for a business-rule violation) rather than
/// LeagueProfileStore's throw-on-invalid style, a known inconsistency this codebase
/// already carries; new code standardizes on the former.
///
/// Every method that appends to the ledger recomputes and persists the affected team(s)'
/// TeamEconomy.Balance in the same call (see <see cref="RecomputeBalance"/>) — callers
/// never need to remember to do that separately, and the local view can never drift out
/// of step with what was just written.
/// </summary>
public sealed class EconomyService
{
    private readonly EconomyLedgerStore _ledger;
    private readonly TeamEconomyStore _teamEconomy;

    public EconomyService(EconomyLedgerStore ledger, TeamEconomyStore teamEconomy)
    {
        _ledger = ledger;
        _teamEconomy = teamEconomy;
    }

    /// <summary>Raised after every successful mutation — see MainViewModel.OnEconomyChanged
    /// (Faza 5) for the dual-push (session channel if active, always the Team Hub channel)
    /// this drives, copied from the AcceptJoinRequest precedent.</summary>
    public event Action<EconomyChangedEventArgs>? Changed;

    // ── Balance / reconciliation ──────────────────────────────────────────

    /// <summary>Re-sums a team's balance straight from the ledger and persists it —
    /// the ONLY way TeamEconomy.Balance is ever set. Called automatically after every
    /// mutation below, and also safe to call standalone (e.g. after a cloud pull, or from
    /// the "PRZELICZ Z KSIĘGI" button).</summary>
    public long RecomputeBalance(string leagueId, string seasonId, string teamId)
    {
        var entries = _ledger.LoadForTeam(leagueId, seasonId, teamId);
        var balance = _teamEconomy.GetOrCreateBalance(leagueId, seasonId, teamId);
        balance.Balance = entries.Sum(e => e.Amount);
        balance.LedgerEntryCount = entries.Count;
        var last = entries.Count > 0 ? entries[^1] : null;
        balance.LastEntryId = last?.Id ?? string.Empty;
        balance.LastEntryHash = last?.EntryHash ?? string.Empty;
        balance.ConsecutiveNegativeRounds = balance.Balance < 0 ? balance.ConsecutiveNegativeRounds : 0;
        balance.RecomputedAtUtc = DateTime.UtcNow;
        _teamEconomy.SaveBalance(balance);
        return balance.Balance;
    }

    /// <summary>Health check for the Host dashboard / "Porównaj księgę z chmurą" flow —
    /// never mutates anything, only reports mismatches for the operator to act on.</summary>
    public EconomyHealthReport Verify(string leagueId, string seasonId, IEnumerable<string> teamIds)
    {
        var mismatches = new List<string>();
        foreach (var teamId in teamIds)
        {
            var entries = _ledger.LoadForTeam(leagueId, seasonId, teamId);
            var stored = _teamEconomy.GetOrCreateBalance(leagueId, seasonId, teamId);
            if (stored.Balance != entries.Sum(e => e.Amount) || stored.LedgerEntryCount != entries.Count)
            {
                mismatches.Add(teamId);
            }
        }
        return new EconomyHealthReport(mismatches, _ledger.VerifyChain(), _ledger.IsQuarantined);
    }

    /// <summary>
    /// Host-computed status label + coverage thresholds (plan D.3: STABILNY/OSTRZEŻENIE/
    /// KRYTYCZNY/NIEWYPŁACALNY) — the single place this classification happens, so the Host
    /// dashboard table (MainViewModel) and the Client-facing snapshot (<see cref="BuildSnapshot"/>)
    /// can never disagree about a team's status. Never computed by the Client.
    /// </summary>
    public string ClassifyStatus(string leagueId, string seasonId, string teamId, LeagueEconomySettings settings)
    {
        var balance = _teamEconomy.GetOrCreateBalance(leagueId, seasonId, teamId).Balance;
        var contracts = _teamEconomy.LoadContracts(leagueId, seasonId).Where(c => c.TeamId == teamId && c.IsActive).ToList();
        return EconomyAmountRules.ClassifyStatus(balance, contracts, settings);
    }

    /// <summary>
    /// Builds the Client-facing snapshot pushed as TeamEconomyUpdate (plan D.3/G.2) — every
    /// number in it (Balance, Status, ProjectedBalance, ShowDetails) is computed here, by the
    /// Host, never by the Client. <paramref name="showDetails"/> must be resolved by the
    /// caller from the REQUESTING driver's own TeamMemberPermission (IsCeo || CanManageEconomy)
    /// — this method trusts whatever it's given, so passing a client-asserted value here
    /// would defeat the whole point (see MainViewModel.BuildEconomySnapshotFor).
    /// </summary>
    public TeamEconomySnapshotPayload BuildSnapshot(
        string leagueId, string seasonId, string teamId, string teamName,
        LeagueEconomySettings settings, bool showDetails, string? nextRoundId = null, string? nextRoundName = null)
    {
        var balance = RecomputeBalance(leagueId, seasonId, teamId);
        var contracts = _teamEconomy.LoadContracts(leagueId, seasonId).Where(c => c.TeamId == teamId && c.IsActive).ToList();

        long? netChange = null;
        long? projected = null;
        if (!string.IsNullOrWhiteSpace(nextRoundId))
        {
            var overrides = settings.FindOverride(nextRoundId);
            var entryFee = overrides?.EntryFeePerCar ?? settings.EntryFeePerCar;
            var sponsorBase = overrides?.SponsorBasePerRound ?? settings.SponsorBasePerRound;
            var outflow = contracts.Sum(c => c.SalaryPerRound) + entryFee * contracts.Count;
            var inflow = contracts.Count > 0 ? sponsorBase + settings.SponsorPerCarPerRound * contracts.Count : 0;
            netChange = inflow - outflow;
            projected = balance + netChange;
        }

        var recent = _ledger.LoadForTeam(leagueId, seasonId, teamId)
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(20)
            .ToList();

        return new TeamEconomySnapshotPayload
        {
            TeamId = teamId,
            TeamName = teamName,
            CurrencyCode = settings.CurrencyCode,
            CurrencyName = settings.CurrencyName,
            Balance = balance,
            Status = EconomyAmountRules.ClassifyStatus(balance, contracts, settings),
            NextRoundNetChange = netChange,
            ProjectedBalance = projected,
            NextRoundName = nextRoundName,
            ShowDetails = showDetails,
            Contracts = showDetails ? contracts : new List<TeamContract>(),
            RecentEntries = showDetails ? recent : new List<EconomyLedgerEntry>(),
            PendingOffers = showDetails ? settings.PendingOffers.Where(o => o.TeamId == teamId).ToList() : new List<ContractOffer>(),
            GeneratedAtUtc = DateTime.UtcNow
        };
    }

    // ── Team lifecycle ─────────────────────────────────────────────────────

    /// <summary>Grants a fresh team its starting balance exactly once — a no-op (not a
    /// failure) if the team already has any ledger history, so this is safe to call
    /// unconditionally whenever a team is added. See K.11: a team joining mid-season gets
    /// a balance proportional to the rounds remaining, via <paramref name="startingBalanceOverride"/>.</summary>
    public EconomyOperationResult EnsureTeamAccount(
        string leagueId, string seasonId, string teamId, string teamName, long? startingBalanceOverride = null)
    {
        var existing = _teamEconomy.GetOrCreateBalance(leagueId, seasonId, teamId);
        if (existing.LedgerEntryCount > 0 || existing.Balance != 0)
        {
            return EconomyOperationResult.Succeeded(Array.Empty<string>(), existing.Balance);
        }

        var settings = _teamEconomy.LoadSettings(leagueId, seasonId);
        var amount = startingBalanceOverride ?? settings.StartingBalance;
        if (amount <= 0)
        {
            return EconomyOperationResult.Succeeded(Array.Empty<string>(), 0);
        }

        var entry = NewEntry(leagueId, seasonId, teamId, teamName, EconomyEntryKind.OpeningBalance, amount,
            EconomyAmountRules.BuildBatchIdempotencyKey(leagueId, seasonId, "opening", teamId, EconomyEntryKind.OpeningBalance),
            "Saldo startowe", EconomyActorType.System, "System");
        return AppendMany(new[] { entry });
    }

    // ── Round close (the "one button per round" happy path) ────────────────

    /// <summary>
    /// Charges entry fees, pays salaries, and pays sponsors for every supplied team in one
    /// atomic batch — the entire "ZAMKNIJ RUNDĘ" operation. A team with zero active
    /// contracts is skipped entirely (no sponsor, no fees, no salaries — see K.26/L.22: a
    /// team with no drivers must not be a money faucet with no matching outflow). Safe to
    /// call twice for the same round: the second call is a no-op (SettledRoundIds).
    /// </summary>
    public EconomyOperationResult CloseRound(
        string leagueId, string seasonId, string roundId, string roundName,
        IReadOnlyList<(string TeamId, string TeamName)> teams, string actorLogin)
    {
        var settings = _teamEconomy.LoadSettings(leagueId, seasonId);
        if (settings.SettledRoundIds.Contains(roundId))
        {
            return EconomyOperationResult.Failed($"Runda „{roundName}” jest już rozliczona.");
        }

        var overrides = settings.FindOverride(roundId);
        var entryFee = overrides?.EntryFeePerCar ?? settings.EntryFeePerCar;
        var sponsorBase = overrides?.SponsorBasePerRound ?? settings.SponsorBasePerRound;

        var allContracts = _teamEconomy.LoadContracts(leagueId, seasonId);
        var entries = new List<EconomyLedgerEntry>();

        foreach (var (teamId, teamName) in teams)
        {
            var activeContracts = allContracts.Where(c => c.TeamId == teamId && c.IsActive).ToList();
            if (activeContracts.Count == 0)
            {
                continue;
            }

            if (sponsorBase > 0)
            {
                entries.Add(NewEntry(leagueId, seasonId, teamId, teamName, EconomyEntryKind.SponsorBase,
                    EconomyAmountRules.ApplySign(EconomyEntryKind.SponsorBase, sponsorBase),
                    EconomyAmountRules.BuildBatchIdempotencyKey(leagueId, seasonId, roundId, teamId, EconomyEntryKind.SponsorBase),
                    $"Sponsor bazowy — {roundName}", EconomyActorType.System, actorLogin, roundId, roundName));
            }

            var sponsorPerCarTotal = settings.SponsorPerCarPerRound * activeContracts.Count;
            if (sponsorPerCarTotal > 0)
            {
                entries.Add(NewEntry(leagueId, seasonId, teamId, teamName, EconomyEntryKind.SponsorPerCar,
                    EconomyAmountRules.ApplySign(EconomyEntryKind.SponsorPerCar, sponsorPerCarTotal),
                    EconomyAmountRules.BuildBatchIdempotencyKey(leagueId, seasonId, roundId, teamId, EconomyEntryKind.SponsorPerCar),
                    $"Sponsor za {activeContracts.Count} aut — {roundName}", EconomyActorType.System, actorLogin, roundId, roundName));
            }

            var entryFeeTotal = entryFee * activeContracts.Count;
            if (entryFeeTotal > 0)
            {
                entries.Add(NewEntry(leagueId, seasonId, teamId, teamName, EconomyEntryKind.EntryFee,
                    EconomyAmountRules.ApplySign(EconomyEntryKind.EntryFee, entryFeeTotal),
                    EconomyAmountRules.BuildBatchIdempotencyKey(leagueId, seasonId, roundId, teamId, EconomyEntryKind.EntryFee),
                    $"Wpisowe za {activeContracts.Count} aut — {roundName}", EconomyActorType.System, actorLogin, roundId, roundName));
            }

            foreach (var contract in activeContracts)
            {
                entries.Add(NewEntry(leagueId, seasonId, teamId, teamName, EconomyEntryKind.Salary,
                    EconomyAmountRules.ApplySign(EconomyEntryKind.Salary, contract.SalaryPerRound),
                    EconomyAmountRules.BuildBatchIdempotencyKey(leagueId, seasonId, roundId, teamId, EconomyEntryKind.Salary, contract.DriverLogin),
                    $"Pensja {contract.DriverLogin} — {roundName}", EconomyActorType.System, actorLogin,
                    roundId, roundName, contract.DriverLogin, contract.Id));
            }
        }

        var result = AppendMany(entries);
        if (!result.Success)
        {
            return result;
        }

        foreach (var contract in allContracts.Where(c => c.IsActive && entries.Any(e => e.ContractId == c.Id)))
        {
            contract.RoundsPaid++;
            if (contract.RoundsPaid >= contract.RoundsTotal)
            {
                contract.Status = ContractStatus.Completed;
                contract.EndedAtUtc = DateTime.UtcNow;
            }
            _teamEconomy.UpsertContract(contract);
        }

        settings.SettledRoundIds.Add(roundId);
        _teamEconomy.SaveSettings(settings);

        RaiseChanged(leagueId, seasonId, teams.Select(t => t.TeamId).ToList(), EconomyChangeKind.RoundSettled, roundId, entries.Count, actorLogin);
        return result;
    }

    // ── Race penalties (DNF/DNS/DSQ — manual, one per round/team/driver/kind) ──────────

    public EconomyOperationResult ApplyRacePenalty(
        string leagueId, string seasonId, string teamId, string teamName, EconomyEntryKind kind,
        string roundId, string roundName, string driverLogin, long? amountOverride, string reason, string actorLogin)
    {
        if (kind is not (EconomyEntryKind.PenaltyDnf or EconomyEntryKind.PenaltyDns or EconomyEntryKind.PenaltyDsq))
        {
            return EconomyOperationResult.Failed("Nieprawidłowy typ kary wyścigowej.");
        }

        var settings = _teamEconomy.LoadSettings(leagueId, seasonId);
        var overrides = settings.FindOverride(roundId);
        var baseAmount = kind switch
        {
            EconomyEntryKind.PenaltyDnf => overrides?.PenaltyDnf ?? settings.PenaltyDnf,
            EconomyEntryKind.PenaltyDns => overrides?.PenaltyDns ?? settings.PenaltyDns,
            EconomyEntryKind.PenaltyDsq => overrides?.PenaltyDsq ?? settings.PenaltyDsq,
            _ => 0
        };
        var amount = amountOverride ?? baseAmount;
        if (amount <= 0)
        {
            return EconomyOperationResult.Succeeded(Array.Empty<string>(), _teamEconomy.GetOrCreateBalance(leagueId, seasonId, teamId).Balance);
        }

        var entry = NewEntry(leagueId, seasonId, teamId, teamName, kind,
            EconomyAmountRules.ApplySign(kind, amount),
            EconomyAmountRules.BuildBatchIdempotencyKey(leagueId, seasonId, roundId, teamId, kind, driverLogin),
            reason, EconomyActorType.HostOperator, actorLogin, roundId, roundName, driverLogin);

        var result = AppendMany(new[] { entry });
        if (result.Success)
        {
            RaiseChanged(leagueId, seasonId, new[] { teamId }, EconomyChangeKind.PenaltyApplied, roundId, result.EntryIds.Count, actorLogin);
        }
        return result;
    }

    // ── Discretionary operations (fine / bonus / manual adjustment) ────────

    public EconomyOperationResult ApplyFine(
        string leagueId, string seasonId, string teamId, string teamName, long amount, string reason,
        string? incidentReportId, string? roundId, string? roundName, string actorLogin, string operationToken)
    {
        if (amount <= 0)
        {
            return EconomyOperationResult.Failed("Kwota kary musi być dodatnia.");
        }
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3)
        {
            return EconomyOperationResult.Failed("Powód kary musi mieć co najmniej 3 znaki.");
        }

        var entry = NewEntry(leagueId, seasonId, teamId, teamName, EconomyEntryKind.Fine,
            EconomyAmountRules.ApplySign(EconomyEntryKind.Fine, amount),
            EconomyAmountRules.BuildManualIdempotencyKey(operationToken),
            reason, EconomyActorType.HostOperator, actorLogin, roundId, roundName, incidentReportId: incidentReportId);

        var result = AppendMany(new[] { entry });
        if (result.Success)
        {
            RaiseChanged(leagueId, seasonId, new[] { teamId }, EconomyChangeKind.PenaltyApplied, roundId, result.EntryIds.Count, actorLogin);
        }
        return result;
    }

    public EconomyOperationResult GrantBonus(
        string leagueId, string seasonId, string teamId, string teamName, long amount, string? presetName,
        string reason, string? roundId, string? roundName, string actorLogin, string operationToken)
    {
        if (amount <= 0)
        {
            return EconomyOperationResult.Failed("Kwota bonusu musi być dodatnia.");
        }

        var entry = NewEntry(leagueId, seasonId, teamId, teamName, EconomyEntryKind.Bonus,
            EconomyAmountRules.ApplySign(EconomyEntryKind.Bonus, amount),
            EconomyAmountRules.BuildManualIdempotencyKey(operationToken),
            string.IsNullOrWhiteSpace(presetName) ? reason : $"{presetName}: {reason}",
            EconomyActorType.HostOperator, actorLogin, roundId, roundName);

        var result = AppendMany(new[] { entry });
        if (result.Success)
        {
            RaiseChanged(leagueId, seasonId, new[] { teamId }, EconomyChangeKind.BonusGranted, roundId, result.EntryIds.Count, actorLogin);
        }
        return result;
    }

    /// <summary>The one place a caller (the Host operator, via an explicit "Dopisz/Odejmij"
    /// toggle in the UI — see plan section L.14) supplies a signed amount directly, capped
    /// at LeagueEconomySettings.MaxManualAdjustment and always requiring a real reason —
    /// this is the escape hatch every economy needs for correcting mistakes, and therefore
    /// also the natural place someone would try to abuse; the limit plus the mandatory
    /// reason plus this entry kind's distinct label in the Track-Log are the mitigations.</summary>
    public EconomyOperationResult ManualAdjust(
        string leagueId, string seasonId, string teamId, string teamName,
        long signedAmount, string reason, string actorLogin, string operationToken)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
        {
            return EconomyOperationResult.Failed("Powód korekty musi mieć co najmniej 10 znaków.");
        }
        if (signedAmount == 0)
        {
            return EconomyOperationResult.Failed("Kwota korekty nie może być zerowa.");
        }

        var settings = _teamEconomy.LoadSettings(leagueId, seasonId);
        if (Math.Abs(signedAmount) > settings.MaxManualAdjustment)
        {
            return EconomyOperationResult.Failed(
                $"Korekta {Math.Abs(signedAmount)} {settings.CurrencyCode} przekracza limit " +
                $"{settings.MaxManualAdjustment} {settings.CurrencyCode}. Wykonaj dwie osobne korekty " +
                "z uzasadnieniem albo zmień limit w ustawieniach.");
        }

        var entry = NewEntry(leagueId, seasonId, teamId, teamName, EconomyEntryKind.ManualAdjustment,
            signedAmount, EconomyAmountRules.BuildManualIdempotencyKey(operationToken),
            reason, EconomyActorType.HostOperator, actorLogin);

        var result = AppendMany(new[] { entry });
        if (result.Success)
        {
            RaiseChanged(leagueId, seasonId, new[] { teamId }, EconomyChangeKind.ManualAdjustment, null, result.EntryIds.Count, actorLogin);
        }
        return result;
    }

    // ── Reversal (the only form of correction — see EconomyLedgerStore's own doc comment) ──

    public EconomyOperationResult ReverseEntry(string entryId, string reason, string actorLogin)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return EconomyOperationResult.Failed("Powód storna jest wymagany.");
        }

        var all = _ledger.Load();
        var original = all.FirstOrDefault(e => e.Id == entryId);
        if (original == null)
        {
            return EconomyOperationResult.Failed("Nie znaleziono wpisu do stornowania.");
        }
        if (original.Kind == EconomyEntryKind.Reversal)
        {
            return EconomyOperationResult.Failed("Nie można stornować storna.");
        }
        if (all.Any(e => e.ReversesEntryId == entryId))
        {
            return EconomyOperationResult.Failed("Ten wpis został już stornowany.");
        }

        var reversal = NewEntry(original.LeagueId, original.SeasonId, original.TeamId, original.TeamNameSnapshot,
            EconomyEntryKind.Reversal, -original.Amount,
            EconomyAmountRules.BuildManualIdempotencyKey($"reverse:{entryId}"),
            reason, EconomyActorType.HostOperator, actorLogin,
            original.RoundId, original.RoundNameSnapshot, original.DriverLogin, original.ContractId, original.IncidentReportId,
            reversesEntryId: entryId);

        var result = AppendMany(new[] { reversal });
        if (result.Success)
        {
            RaiseChanged(original.LeagueId, original.SeasonId, new[] { original.TeamId }, EconomyChangeKind.Reversal, original.RoundId, result.EntryIds.Count, actorLogin);
        }
        return result;
    }

    // ── Contracts ────────────────────────────────────────────────────────

    public EconomyOperationResult SignContract(TeamContract draft, string actorLogin)
    {
        var settings = _teamEconomy.LoadSettings(draft.LeagueId, draft.SeasonId);
        var existingContracts = _teamEconomy.LoadContracts(draft.LeagueId, draft.SeasonId)
            .Where(c => c.TeamId == draft.TeamId).ToList();
        var balance = _teamEconomy.GetOrCreateBalance(draft.LeagueId, draft.SeasonId, draft.TeamId).Balance;

        var (outcome, error) = ContractValidator.Validate(draft, settings, existingContracts, balance);
        if (outcome != ContractValidationOutcome.Success)
        {
            return EconomyOperationResult.Failed(error!);
        }

        draft.Status = ContractStatus.Active;
        draft.SignedAtUtc = DateTime.UtcNow;
        draft.CreatedByActor = actorLogin;
        _teamEconomy.UpsertContract(draft);

        var entries = new List<EconomyLedgerEntry>();
        if (draft.SigningFee > 0)
        {
            entries.Add(NewEntry(draft.LeagueId, draft.SeasonId, draft.TeamId, string.Empty, EconomyEntryKind.SigningFee,
                EconomyAmountRules.ApplySign(EconomyEntryKind.SigningFee, draft.SigningFee),
                EconomyAmountRules.BuildManualIdempotencyKey($"signing:{draft.Id}"),
                $"Opłata za podpisanie kontraktu z {draft.DriverLogin}", EconomyActorType.HostOperator, actorLogin,
                contractId: draft.Id, driverLogin: draft.DriverLogin));
        }

        var result = entries.Count > 0
            ? AppendMany(entries)
            : EconomyOperationResult.Succeeded(Array.Empty<string>(), balance);

        if (result.Success)
        {
            RaiseChanged(draft.LeagueId, draft.SeasonId, new[] { draft.TeamId }, EconomyChangeKind.ContractSigned, null, entries.Count, actorLogin);
        }
        return result;
    }

    /// <summary>
    /// Ends a contract. When <paramref name="breachChargeTeamId"/> is supplied, the breach
    /// fee (RoundsRemaining × SalaryPerRound × BreachMultiplierDriver) is charged to THAT
    /// team instead of the contract's own team — the case where a driver under contract
    /// joins a different team (see plan L.12/K.6: AcceptJoinRequest/HandleTeamAddMemberRequestAsync
    /// call this with the driver's NEW team so the poaching team bears the cost, not the
    /// one being left). Without it, the contract's own team is charged at
    /// BreachMultiplierTeam instead (a team voluntarily dissolving a contract).
    /// </summary>
    public EconomyOperationResult TerminateContract(
        string contractId, string reason, string actorLogin, string operationToken,
        bool chargeBreachFee = true, string? breachChargeTeamId = null)
    {
        var contract = _teamEconomy.FindContractById(contractId);
        if (contract == null)
        {
            return EconomyOperationResult.Failed("Nie znaleziono kontraktu.");
        }
        if (!contract.IsActive)
        {
            return EconomyOperationResult.Failed("Ten kontrakt nie jest już aktywny.");
        }

        contract.Status = ContractStatus.Terminated;
        contract.EndedAtUtc = DateTime.UtcNow;
        contract.TerminationReason = reason;
        _teamEconomy.UpsertContract(contract);

        var entries = new List<EconomyLedgerEntry>();
        if (chargeBreachFee)
        {
            var settings = _teamEconomy.LoadSettings(contract.LeagueId, contract.SeasonId);
            var chargeTeamId = breachChargeTeamId ?? contract.TeamId;
            var multiplier = breachChargeTeamId != null ? settings.BreachMultiplierDriver : settings.BreachMultiplierTeam;
            var fee = (long)Math.Round(contract.RoundsRemaining * contract.SalaryPerRound * multiplier);
            if (fee > 0)
            {
                entries.Add(NewEntry(contract.LeagueId, contract.SeasonId, chargeTeamId, string.Empty, EconomyEntryKind.ContractBreachFee,
                    EconomyAmountRules.ApplySign(EconomyEntryKind.ContractBreachFee, fee),
                    EconomyAmountRules.BuildManualIdempotencyKey(operationToken),
                    $"Kara umowna — rozwiązanie kontraktu z {contract.DriverLogin}: {reason}",
                    EconomyActorType.HostOperator, actorLogin, contractId: contract.Id, driverLogin: contract.DriverLogin));
            }
        }

        var result = entries.Count > 0
            ? AppendMany(entries)
            : EconomyOperationResult.Succeeded(
                Array.Empty<string>(), _teamEconomy.GetOrCreateBalance(contract.LeagueId, contract.SeasonId, contract.TeamId).Balance);

        if (result.Success)
        {
            RaiseChanged(contract.LeagueId, contract.SeasonId, new[] { contract.TeamId }, EconomyChangeKind.ContractTerminated, null, entries.Count, actorLogin);
        }
        return result;
    }

    // ── Contract offers (Faza 8 — CEO/manager proposes, Host operator approves) ────

    /// <summary>Queues a CEO/economy-manager-proposed contract for operator review — capped
    /// per team (plan L.17, prevents a nuisance flood of proposals) but NOT otherwise
    /// validated here: salary/limit/balance checks happen at approval time via
    /// ContractValidator (inside SignContract), so the operator can see and explicitly
    /// reject an out-of-range proposal rather than have it silently swallowed unseen.</summary>
    public EconomyOperationResult QueueContractOffer(
        string leagueId, string seasonId, string teamId, string teamName, string driverLogin,
        long salaryPerRound, long signingFee, int roundsTotal, string proposedByLogin)
    {
        if (salaryPerRound <= 0)
        {
            return EconomyOperationResult.Failed("Pensja musi być dodatnia.");
        }
        if (roundsTotal <= 0)
        {
            return EconomyOperationResult.Failed("Liczba rund musi być dodatnia.");
        }

        var settings = _teamEconomy.LoadSettings(leagueId, seasonId);
        const int maxPendingOffersPerTeam = 10;
        if (settings.PendingOffers.Count(o => o.TeamId == teamId) >= maxPendingOffersPerTeam)
        {
            return EconomyOperationResult.Failed(
                $"Zespół ma już {maxPendingOffersPerTeam} oczekujących propozycji — poczekaj, aż operator je rozpatrzy.");
        }

        settings.PendingOffers.Add(new ContractOffer
        {
            TeamId = teamId,
            TeamName = teamName,
            DriverLogin = driverLogin,
            SalaryPerRound = salaryPerRound,
            SigningFee = signingFee,
            RoundsTotal = roundsTotal,
            ProposedByLogin = proposedByLogin
        });
        _teamEconomy.SaveSettings(settings);
        return EconomyOperationResult.Succeeded(Array.Empty<string>(), _teamEconomy.GetOrCreateBalance(leagueId, seasonId, teamId).Balance);
    }

    /// <summary>Approves a pending offer by signing it through the SAME path as a manually-
    /// typed contract (ContractValidator, inside SignContract) — an offer that's gone stale
    /// since it was proposed (team salary cap now exceeded, driver signed elsewhere in the
    /// meantime) is still rejected with a clear reason, never forced through unchecked.</summary>
    public EconomyOperationResult ApproveContractOffer(string leagueId, string seasonId, string offerId, string actorLogin)
    {
        var settings = _teamEconomy.LoadSettings(leagueId, seasonId);
        var offer = settings.PendingOffers.FirstOrDefault(o => o.Id == offerId);
        if (offer == null)
        {
            return EconomyOperationResult.Failed("Nie znaleziono propozycji (mogła zostać już rozpatrzona).");
        }

        var draft = new TeamContract
        {
            LeagueId = leagueId,
            SeasonId = seasonId,
            TeamId = offer.TeamId,
            DriverLogin = offer.DriverLogin,
            SalaryPerRound = offer.SalaryPerRound,
            SigningFee = offer.SigningFee,
            RoundsTotal = offer.RoundsTotal
        };
        var result = SignContract(draft, actorLogin);
        if (!result.Success)
        {
            return result;
        }

        settings.PendingOffers.RemoveAll(o => o.Id == offerId);
        _teamEconomy.SaveSettings(settings);
        return result;
    }

    public EconomyOperationResult RejectContractOffer(string leagueId, string seasonId, string offerId)
    {
        var settings = _teamEconomy.LoadSettings(leagueId, seasonId);
        var offer = settings.PendingOffers.FirstOrDefault(o => o.Id == offerId);
        if (offer == null)
        {
            return EconomyOperationResult.Failed("Nie znaleziono propozycji (mogła zostać już rozpatrzona).");
        }
        settings.PendingOffers.RemoveAll(o => o.Id == offerId);
        _teamEconomy.SaveSettings(settings);
        return EconomyOperationResult.Succeeded(Array.Empty<string>(), _teamEconomy.GetOrCreateBalance(leagueId, seasonId, offer.TeamId).Balance);
    }

    // ── internals ────────────────────────────────────────────────────────

    private static EconomyLedgerEntry NewEntry(
        string leagueId, string seasonId, string teamId, string teamNameSnapshot,
        EconomyEntryKind kind, long amount, string idempotencyKey, string reason,
        EconomyActorType actorType, string actorLogin,
        string? roundId = null, string? roundNameSnapshot = null, string? driverLogin = null,
        string? contractId = null, string? incidentReportId = null, string? reversesEntryId = null) =>
        new()
        {
            LeagueId = leagueId,
            SeasonId = seasonId,
            TeamId = teamId,
            TeamNameSnapshot = teamNameSnapshot,
            Kind = kind,
            Amount = amount,
            IdempotencyKey = idempotencyKey,
            Reason = reason,
            ActorType = actorType,
            ActorLogin = actorLogin,
            RoundId = roundId,
            RoundNameSnapshot = roundNameSnapshot,
            DriverLogin = driverLogin,
            ContractId = contractId,
            IncidentReportId = incidentReportId,
            ReversesEntryId = reversesEntryId
        };

    /// <summary>Appends a batch and recomputes every affected team's balance in the same
    /// call — the only path by which TeamEconomy.Balance is ever updated, so it can never
    /// drift out of step with what was just written to the ledger.</summary>
    private EconomyOperationResult AppendMany(IReadOnlyList<EconomyLedgerEntry> entries)
    {
        if (entries.Count == 0)
        {
            return EconomyOperationResult.Succeeded(Array.Empty<string>(), 0);
        }
        if (_ledger.IsQuarantined)
        {
            return EconomyOperationResult.Failed(
                "Księga jest zablokowana (wykryto uszkodzenie pliku) — wymaga interwencji operatora przed dalszymi zapisami.");
        }

        var appended = _ledger.AppendBatch(entries);
        if (appended == null)
        {
            return EconomyOperationResult.Failed("Nie udało się zapisać do księgi (zablokowana).");
        }

        var skipped = entries.Count - appended.Count;
        long lastBalance = 0;
        foreach (var group in entries.GroupBy(e => (e.LeagueId, e.SeasonId, e.TeamId)))
        {
            lastBalance = RecomputeBalance(group.Key.LeagueId, group.Key.SeasonId, group.Key.TeamId);
        }

        return EconomyOperationResult.Succeeded(appended.Select(e => e.Id).ToList(), lastBalance, skipped);
    }

    private void RaiseChanged(
        string leagueId, string seasonId, IReadOnlyList<string> teamIds, EconomyChangeKind kind,
        string? roundId, int entryCount, string actorLogin) =>
        Changed?.Invoke(new EconomyChangedEventArgs
        {
            LeagueId = leagueId,
            SeasonId = seasonId,
            AffectedTeamIds = teamIds,
            Kind = kind,
            RoundId = roundId,
            EntryCount = entryCount,
            ActorLogin = actorLogin
        });
}
