namespace VRS.RaceControl.Shared.Models;

public sealed record WalletTeamPreset(string Key, string Name, string RaceClass, string Color,
    string CeoName, long StartingBudget, long CurrentBalance, long WeeklyIncome, string WalletKey);
public sealed record WalletPreset(string Key, string Name, long StartingBudget, long CurrentBalance, long WeeklyIncome);
public sealed record RaceCostPreset(int Id, long EntryFee, long Maintenance)
{
    public long Total => EntryFee + Maintenance;
    public string Display => $"{Id} · {Total:N0} € ({EntryFee:N0} + {Maintenance:N0})";
}
public sealed class RoundCarResult
{
    public string CarId { get; set; } = "";
    public string TeamId { get; set; } = "";
    public string RaceClass { get; set; } = "HY";
    public string Status { get; set; } = "Finished";
    public int? Position { get; set; }
}
public sealed record WalletSummary(string Id, string Name, long StartingBudget, long Balance,
    long WeeklyIncome, string[] Teams, bool Active);
public sealed record WalletLedgerRow(string Id, string WalletId, string Kind, long Amount,
    string Reason, string? TeamId, string? CarId, string? RaceClass, DateTimeOffset CreatedAt);
public sealed record WalletState(WalletSummary[] Wallets, WalletLedgerRow[] Entries, bool PresetApplied);
public sealed record TeamHubCloudContract(string Id, string TeamId, string DriverLogin,
    long SalaryPerRound, long SigningFee, int RoundsTotal, int RoundsPaid, string Status)
{
    public int RoundsRemaining => Math.Max(0, RoundsTotal - RoundsPaid);
}
public sealed record TeamHubCloudOffer(string Id, string TeamId, string DriverLogin,
    long SalaryPerRound, long SigningFee, int RoundsTotal, string Status);
public sealed record TeamHubAnnouncement(string Id, string Body, DateTimeOffset CreatedAt);
public sealed record TeamHubCloudState(string TeamId, string TeamName, string? WalletId,
    bool CanManageContracts, bool CanAnnounce, TeamHubCloudContract[] Contracts,
    TeamHubCloudOffer[] Offers, TeamHubAnnouncement[] Announcements);
public sealed record WalletRoundPreview(string CarId, string TeamId, string WalletId,
    string RaceClass, string Status, long EntryFee, long Maintenance, long Penalty, long Prize)
{
    public long Net => Prize - EntryFee - Maintenance - Penalty;
}

public static class Beta63EconomyPreset
{
    public const string Id = "beta-6.3.n-screens-v1";
    public const string Name = "BETA 6.3.N — dane ze screenów";
    public static IReadOnlyList<RaceCostPreset> Costs { get; } = [
        new(1,75_000,25_000),new(2,100_000,35_000),new(3,125_000,45_000),
        new(4,150_000,55_000),new(5,225_000,85_000)];
    public static IReadOnlyList<WalletTeamPreset> Teams { get; } = [
        new("cadillac","Cadillac Hertz Team Jota","HY","#FFF200","Michał Czajkowski",12_500_000,500_000,250_000,"cadillac"),
        new("alpine","Alpine Endurance Team","HY","#F15BF2","Ognjen Cerovic",11_000_000,6_250_000,350_000,"alpine"),
        new("bmw-hy","BMW M Team WRT","HY","#00B0F0","Leon Baram",11_000_000,5_000_000,550_000,"bmw"),
        new("genesis","Genesis Magma Racing","HY","#FF9900","Szymonek",10_500_000,6_000_000,450_000,"genesis"),
        new("ferrari-hy","Ferrari AF Corse","HY","#FF0000","Jakub Jacek",9_500_000,9_500_000,650_000,"ferrari"),
        new("peugeot","Team Peugeot TotalEnergies","HY","#92D050","Piotr Kochanowicz",9_000_000,2_000_000,750_000,"peugeot"),
        new("toyota","Toyota Racing","HY","#FFFFFF","Wiktor Kornak",8_500_000,8_500_000,850_000,"toyota-lexus"),
        new("aston-hy","Aston Martin THOR Team","HY","#00B050","Karlo Kruhan",8_000_000,6_500_000,950_000,"aston"),
        new("porsche","Porsche Manthey Racing","GT3","#B87832","Mateusz Mrozik",9_500_000,5_500_000,150_000,"porsche"),
        new("corvette","Corvette GT3 TF Sport","GT3","#FFFF00","Kamil Eremberg",8_000_000,3_500_000,200_000,"corvette"),
        new("mercedes","Mercedes AMG LMGT3","GT3","#BFBFBF","Miłosz Drygalski",7_500_000,5_250_000,250_000,"mercedes"),
        new("bmw-gt3","Team WRT BMW","GT3","#00B0F0","Leon Baram",7_500_000,5_500_000,300_000,"bmw"),
        new("aston-gt3","Aston Martin Heart of Racing","GT3","#00B050","Karlo Kruhan",6_000_000,3_500_000,350_000,"aston"),
        new("mclaren","McLaren Garage 59","GT3","#FF9900","Grzegorz Zelga",6_000_000,1_750_000,350_000,"mclaren"),
        new("ferrari-gt3","Vista AF Corse Ferrari","GT3","#FF0000","Jakub Jacek",5_500_000,2_500_000,400_000,"ferrari"),
        new("ford","Ford Proton Competition","GT3","#00B0F0","Mateusz Kramarczyk",5_500_000,3_500_000,400_000,"ford"),
        new("lexus","Lexus Akkodis ASP Team","GT3","#101010","Wiktor Kornak",3_500_000,3_500_000,500_000,"toyota-lexus")];
    public static IReadOnlyList<WalletPreset> Wallets { get; } = Teams
        .Where(t => t.WalletKey == t.Key).Select(t => new WalletPreset(t.Key,t.Name,t.StartingBudget,t.CurrentBalance,t.WeeklyIncome))
        .Concat(new WalletPreset[] { new("bmw","BMW",18_500_000,1_000_000,850_000),
            new("ferrari","Ferrari",15_000_000,6_250_000,1_050_000),
            new("toyota-lexus","Toyota / Lexus",12_000_000,1_500_000,1_350_000),
            new("aston","Aston Martin",14_000_000,5_250_000,1_300_000) }).ToArray();

    public static (long Penalty, long Prize) Calculate(string raceClass, string status, int? position)
    {
        if (raceClass is not ("HY" or "GT3")) throw new ArgumentException("Class: HY / GT3");
        long dnf = raceClass == "HY" ? 500_000 : 250_000;
        if (status != "Finished") return (status switch {
            "DNF" => dnf, "DNS" => dnf * 5 / 4, "DSQ" => dnf * 2,
            _ => throw new ArgumentException("Status: Finished / DNF / DNS / DSQ") }, 0);
        if (position is null or <= 0) throw new ArgumentException("Finishing position is required.");
        long[] prizes = raceClass == "HY" ? [250_000,200_000,150_000,100_000,50_000] : [200_000,150_000,100_000,50_000,25_000];
        return (0, position <= 5 ? prizes[position.Value - 1] : 0);
    }
}
