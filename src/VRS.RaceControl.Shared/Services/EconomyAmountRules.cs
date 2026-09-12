using System.Linq;
using System.Security.Cryptography;
using System.Text;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>
/// Pure helpers shared by EconomyService — sign derivation, idempotency-key construction,
/// hash-chain computation, and display labels. No I/O, fully testable in isolation.
/// </summary>
public static class EconomyAmountRules
{
    /// <summary>
    /// The fixed direction for every kind except ManualAdjustment/Reversal, which don't
    /// have one — those two are the only places a caller (EconomyService.ManualAdjust /
    /// ReverseEntry) supplies a signed Amount directly. Every other kind's UI trigger only
    /// ever collects an absolute value; the sign is never client- or UI-supplied.
    /// </summary>
    public static int SignFor(EconomyEntryKind kind) => kind switch
    {
        EconomyEntryKind.OpeningBalance
            or EconomyEntryKind.SponsorBase
            or EconomyEntryKind.SponsorPerCar
            or EconomyEntryKind.Bonus
            or EconomyEntryKind.PrizeMoney => 1,
        EconomyEntryKind.EntryFee
            or EconomyEntryKind.Salary
            or EconomyEntryKind.SigningFee
            or EconomyEntryKind.ContractBreachFee
            or EconomyEntryKind.Fine
            or EconomyEntryKind.PenaltyDnf
            or EconomyEntryKind.PenaltyDns
            or EconomyEntryKind.PenaltyDsq => -1,
        EconomyEntryKind.ManualAdjustment or EconomyEntryKind.Reversal =>
            throw new InvalidOperationException(
                $"{kind} nie ma jednego stałego znaku — wywołujący sam ustala podpisaną kwotę."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static long ApplySign(EconomyEntryKind kind, long absoluteAmount)
    {
        if (absoluteAmount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(absoluteAmount), "Kwota musi być nieujemna — znak nadaje system.");
        }
        return SignFor(kind) * absoluteAmount;
    }

    /// <summary>
    /// Deterministic key for a system-triggered, once-per-(round,team,kind[,driver])
    /// operation (round close, per-driver salary) — retrying the exact same operation
    /// (double click, two Hosts closing the same round offline) produces the same key, so
    /// EconomyLedgerStore.AppendBatch silently skips the duplicate instead of double-charging.
    /// </summary>
    public static string BuildBatchIdempotencyKey(
        string leagueId, string seasonId, string roundId, string teamId, EconomyEntryKind kind, string? driverLogin = null) =>
        string.IsNullOrWhiteSpace(driverLogin)
            ? $"{leagueId}:{seasonId}:{roundId}:{teamId}:{kind}"
            : $"{leagueId}:{seasonId}:{roundId}:{teamId}:{kind}:{driverLogin}";

    /// <summary>
    /// Key for a discretionary operation (fine, bonus, manual adjustment) that has no
    /// natural once-per-X identity — the caller must generate <paramref name="operationToken"/>
    /// once, when the confirmation dialog opens, and reuse it across a click plus any
    /// retry, so a retry collapses into the original operation while a deliberate second,
    /// distinct action (a new dialog, a new token) is never blocked.
    /// </summary>
    public static string BuildManualIdempotencyKey(string operationToken) => $"manual:{operationToken}";

    /// <summary>Chains this entry to the previous one in append order — see
    /// EconomyLedgerStore.AppendBatch/VerifyChain. NEVER change this formula once entries
    /// exist in production: it would make every previously-computed hash unverifiable.</summary>
    public static string ComputeEntryHash(EconomyLedgerEntry entry) =>
        ComputeHash($"{entry.PreviousEntryHash}|{entry.Id}|{entry.TeamId}|{entry.Kind}|{entry.Amount}|{entry.CreatedAtUtc:O}|{entry.IdempotencyKey}");

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// Host-computed status label + coverage thresholds (plan D.3: STABILNY/OSTRZEŻENIE/
    /// KRYTYCZNY/NIEWYPŁACALNY) — moved here from EconomyService (Faza 10) so the Client can
    /// classify a Supabase-direct snapshot with the EXACT same thresholds the Host uses. A
    /// second, PL/pgSQL copy of this math would silently drift from this one the first time
    /// a threshold changes — this is the single place it happens, called from both sides.
    /// </summary>
    public static string ClassifyStatus(long balance, IReadOnlyList<TeamContract> activeContracts, LeagueEconomySettings settings) =>
        ClassifyStatus(balance, activeContracts.Sum(c => c.SalaryPerRound), activeContracts.Count, settings);

    /// <summary>
    /// Same classification, taking only the AGGREGATE active-contract salary sum/count —
    /// what a non-privileged team member's Supabase-direct snapshot carries (the per-driver
    /// contract list itself is gated behind ShowDetails, but Status is visible to every team
    /// member regardless, same as on the Host dashboard).
    /// </summary>
    public static string ClassifyStatus(long balance, long activeContractsSalarySum, int activeContractsCount, LeagueEconomySettings settings)
    {
        if (balance < 0)
        {
            return "NIEWYPŁACALNY";
        }
        if (activeContractsCount == 0)
        {
            return "STABILNY";
        }
        var outflow = activeContractsSalarySum + settings.EntryFeePerCar * activeContractsCount;
        var inflow = settings.SponsorBasePerRound + settings.SponsorPerCarPerRound * activeContractsCount;
        var netCost = Math.Max(0, outflow - inflow);
        if (netCost <= 0)
        {
            return "STABILNY";
        }
        var roundsCovered = (double)balance / netCost;
        if (roundsCovered < settings.CriticalRoundsCovered)
        {
            return "KRYTYCZNY";
        }
        if (roundsCovered < settings.WarningRoundsCovered)
        {
            return "OSTRZEŻENIE";
        }
        return "STABILNY";
    }

    public static string LabelFor(EconomyEntryKind kind) => kind switch
    {
        EconomyEntryKind.OpeningBalance => "Saldo startowe",
        EconomyEntryKind.SponsorBase => "Sponsor",
        EconomyEntryKind.SponsorPerCar => "Sponsor za samochód",
        EconomyEntryKind.EntryFee => "Wpisowe",
        EconomyEntryKind.Salary => "Pensja",
        EconomyEntryKind.SigningFee => "Opłata za podpisanie",
        EconomyEntryKind.ContractBreachFee => "Kara umowna",
        EconomyEntryKind.Fine => "Kara",
        EconomyEntryKind.PenaltyDnf => "Kara DNF",
        EconomyEntryKind.PenaltyDns => "Kara DNS",
        EconomyEntryKind.PenaltyDsq => "Kara DSQ",
        EconomyEntryKind.Bonus => "Bonus",
        EconomyEntryKind.PrizeMoney => "Nagroda",
        EconomyEntryKind.ManualAdjustment => "Korekta ręczna",
        EconomyEntryKind.Reversal => "Storno",
        _ => kind.ToString()
    };
}
