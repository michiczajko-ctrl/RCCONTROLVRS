using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

public enum ContractValidationOutcome
{
    Success,
    SalaryTooLow,
    SalaryTooHigh,
    RoundsOutOfRange,
    DriverAlreadyHasActiveContract,
    TooManyActiveContracts,
    TeamSalaryCapExceeded,
    InsufficientBalance
}

/// <summary>
/// Pure, stateless contract-signing validation — same shape as TeamAddMemberValidator/
/// TeamJoinDecisionValidator: a static Validate method with no dependency on sockets or
/// storage, independently testable. EconomyService.SignContract is the only caller that
/// matters in production, but nothing here requires that.
/// </summary>
public static class ContractValidator
{
    public static (ContractValidationOutcome Outcome, string? ErrorMessage) Validate(
        TeamContract draft,
        LeagueEconomySettings settings,
        IReadOnlyList<TeamContract> existingTeamContracts,
        long currentBalance)
    {
        if (draft.SalaryPerRound < settings.MinSalaryPerRound)
        {
            return (ContractValidationOutcome.SalaryTooLow,
                $"Pensja musi wynosić co najmniej {settings.MinSalaryPerRound} {settings.CurrencyCode}.");
        }
        if (draft.SalaryPerRound > settings.MaxSalaryPerRound)
        {
            return (ContractValidationOutcome.SalaryTooHigh,
                $"Pensja nie może przekraczać {settings.MaxSalaryPerRound} {settings.CurrencyCode}.");
        }
        if (draft.RoundsTotal is < 1 or > 50)
        {
            return (ContractValidationOutcome.RoundsOutOfRange, "Długość kontraktu musi wynosić od 1 do 50 rund.");
        }

        var otherActiveContracts = existingTeamContracts
            .Where(c => c.IsActive && !string.Equals(c.Id, draft.Id, StringComparison.Ordinal))
            .ToList();

        if (otherActiveContracts.Any(c => string.Equals(c.DriverLogin, draft.DriverLogin, StringComparison.OrdinalIgnoreCase)))
        {
            return (ContractValidationOutcome.DriverAlreadyHasActiveContract,
                "Ten kierowca ma już aktywny kontrakt w tej lidze.");
        }
        if (otherActiveContracts.Count >= settings.MaxContractsPerTeam)
        {
            return (ContractValidationOutcome.TooManyActiveContracts,
                $"Zespół osiągnął limit {settings.MaxContractsPerTeam} aktywnych kontraktów.");
        }

        var totalSalary = otherActiveContracts.Sum(c => c.SalaryPerRound) + draft.SalaryPerRound;
        if (totalSalary > settings.TeamSalaryCapPerRound)
        {
            return (ContractValidationOutcome.TeamSalaryCapExceeded,
                $"Suma pensji {totalSalary} {settings.CurrencyCode} przekracza limit " +
                $"{settings.TeamSalaryCapPerRound} {settings.CurrencyCode} na rundę.");
        }

        if (currentBalance < draft.SigningFee + draft.SalaryPerRound)
        {
            return (ContractValidationOutcome.InsufficientBalance,
                "Saldo zespołu nie wystarcza na opłatę za podpisanie i pierwszą ratę pensji.");
        }

        return (ContractValidationOutcome.Success, null);
    }
}
