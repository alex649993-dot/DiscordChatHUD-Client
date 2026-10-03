namespace DiscordChatHUD.Models;
// Values below use the real-time production windows used by GTA Online's
// business UI. They intentionally track units as well as percentages, so the
// Nightclub rows can retain their useful "current / capacity" form.
internal enum BusinessCategory
{
    Bunker,
    AcidLab,
    Nightclub,
    MotorcycleClub
}

internal sealed record ProductionTiming(double FullStockMinutes, double FullSupplyMinutes);

internal sealed record BusinessSupplyProfile(
    string Key,
    string Name,
    BusinessCategory Category,
    int StockCapacity,
    int SupplyCapacity,
    bool SupportsMansionBoost,
    ProductionTiming BasicTiming,
    ProductionTiming PartialTiming,
    ProductionTiming FullTiming,
    string? SourceBusiness = null,
    int DailyBoostUnits = 0)
{
    public bool HasSupplies => SupplyCapacity > 0;
    public bool SupportsDailyBoost => DailyBoostUnits > 0;
    public bool IsNightclubGood => Category == BusinessCategory.Nightclub;

    public ProductionTiming TimingFor(string? tier) => BusinessSupplyCalculator.NormalizeTier(tier) switch
    {
        "basic" => BasicTiming,
        "partial" => PartialTiming,
        _ => FullTiming
    };
}

internal sealed record BusinessSupplyProgress(
    string Key,
    string Name,
    BusinessCategory Category,
    double StockUnits,
    int StockCapacity,
    int StockPercent,
    double? SupplyUnits,
    int SupplyCapacity,
    int? SupplyPercent,
    bool IsOnline,
    bool MansionBoostActive,
    TimeSpan? UntilSupplyEmpty,
    TimeSpan? UntilStockFull,
    TimeSpan FullStockTime,
    double SupplyBarsToFullStock,
    bool DailyBoostActive = false,
    double DailyBoostUnitsRemaining = 0d,
    TimeSpan? DailyBoostTimeRemaining = null);

internal static class BusinessSupplyCatalog
{
    // Bunker: 100 stock units. 100% staff + equipment creates one unit every
    // seven minutes and consumes a full supplies bar in 140 minutes. One full
    // bar therefore makes 20 stock units; full stock needs five bars.
    // Acid Lab: the equipment upgrade changes a full run from 6h / 3.2 bars
    // to 4h / 1.6 bars. Mansion Boost is a 200% increase (3x total speed).
    // The lab's own daily boost doubles speed until 80 units are made or 24
    // real hours pass: 4h becomes 3h (80 at 2x + 80 at 1x), 1h20 with Mansion
    // Boost becomes 1h, matching the published Acid Lab production times.
    // Nightclub timings are per technician and match the warehouse's seven
    // goods: capacity × time per unit (e.g. Cargo 50 × 70 min = 3500 min).
    public static readonly IReadOnlyList<BusinessSupplyProfile> Profiles =
    [
        new(
            "bunker", "벙커", BusinessCategory.Bunker, 100, 100, true,
            new ProductionTiming(1000d, 100d),
            new ProductionTiming(850d, 85d),
            new ProductionTiming(700d, 140d)),
        new(
            "acid_lab", "LSD 연구소", BusinessCategory.AcidLab, 160, 100, true,
            new ProductionTiming(360d, 112.5d),
            new ProductionTiming(240d, 150d),
            new ProductionTiming(240d, 150d),
            DailyBoostUnits: BusinessSupplyCalculator.DailyBoostUnits),

        new(
            "nightclub_cargo", "패키지와 화물", BusinessCategory.Nightclub, 50, 0, false,
            new ProductionTiming(3500d, 0d),
            new ProductionTiming(3500d, 0d),
            new ProductionTiming(3500d, 0d), "격납고 또는 특수 화물"),
        new(
            "nightclub_sporting", "사냥 용품", BusinessCategory.Nightclub, 100, 0, false,
            new ProductionTiming(4000d, 0d),
            new ProductionTiming(4000d, 0d),
            new ProductionTiming(4000d, 0d), "벙커"),
        new(
            "nightclub_south_american", "남미산 수입품", BusinessCategory.Nightclub, 10, 0, false,
            new ProductionTiming(1200d, 0d),
            new ProductionTiming(1200d, 0d),
            new ProductionTiming(1200d, 0d), "코카인 락업"),
        new(
            "nightclub_pharmaceutical", "연구용 약품", BusinessCategory.Nightclub, 20, 0, false,
            new ProductionTiming(1200d, 0d),
            new ProductionTiming(1200d, 0d),
            new ProductionTiming(1200d, 0d), "필로폰 연구소"),
        new(
            "nightclub_cash", "현금 창출", BusinessCategory.Nightclub, 40, 0, false,
            new ProductionTiming(1200d, 0d),
            new ProductionTiming(1200d, 0d),
            new ProductionTiming(1200d, 0d), "위조지폐 공장"),
        new(
            "nightclub_organic", "유기농 작물", BusinessCategory.Nightclub, 80, 0, false,
            new ProductionTiming(1600d, 0d),
            new ProductionTiming(1600d, 0d),
            new ProductionTiming(1600d, 0d), "대마 농장"),
        new(
            "nightclub_printing", "인쇄 & 복사", BusinessCategory.Nightclub, 60, 0, false,
            new ProductionTiming(900d, 0d),
            new ProductionTiming(900d, 0d),
            new ProductionTiming(900d, 0d), "문서 위조"),

        // Existing Preview 78 MC entries remain compatible. They stay in the
        // data model even though the redesigned UI focuses on Bunker, LSD and
        // the Nightclub warehouse first.
        new("cocaine", "코카인", BusinessCategory.MotorcycleClub, 10, 100, true,
            new ProductionTiming(420d, 120d), new ProductionTiming(360d, 120d), new ProductionTiming(300d, 120d)),
        new("meth", "필로폰", BusinessCategory.MotorcycleClub, 20, 100, true,
            new ProductionTiming(480d, 144d), new ProductionTiming(420d, 144d), new ProductionTiming(360d, 144d)),
        new("cash", "위조지폐", BusinessCategory.MotorcycleClub, 40, 100, true,
            new ProductionTiming(480d, 160d), new ProductionTiming(400d, 160d), new ProductionTiming(320d, 160d)),
        new("weed", "대마", BusinessCategory.MotorcycleClub, 80, 100, true,
            new ProductionTiming(720d, 160d), new ProductionTiming(600d, 160d), new ProductionTiming(480d, 160d)),
        new("documents", "문서 위조", BusinessCategory.MotorcycleClub, 60, 100, true,
            new ProductionTiming(450d, 150d), new ProductionTiming(360d, 150d), new ProductionTiming(300d, 150d))
    ];

    public static IEnumerable<BusinessSupplyProfile> PrimaryProductionProfiles => Profiles.Where(profile =>
        profile.Category is BusinessCategory.Bunker or BusinessCategory.AcidLab);

    /// <summary>
    /// 설정창 사업장 페이지에 카드를 그릴 대상. 벙커와 LSD 연구소에 더해
    /// 오토바이 클럽 사업장(코카인 / 필로폰 / 위조지폐 / 대마 / 문서 위조)까지
    /// 모두 포함한다. 카드는 각자의 체크 표시로 보이고 숨는다.
    /// </summary>
    public static IEnumerable<BusinessSupplyProfile> TrackedProductionProfiles => Profiles.Where(profile =>
        profile.Category is BusinessCategory.Bunker
            or BusinessCategory.AcidLab
            or BusinessCategory.MotorcycleClub);

    public static IEnumerable<BusinessSupplyProfile> NightclubProfiles => Profiles.Where(profile =>
        profile.Category == BusinessCategory.Nightclub);

    public static bool TryFind(string? key, string? name, out BusinessSupplyProfile profile)
    {
        profile = Profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))!;
        return profile is not null;
    }

    public static BusinessSupplyProfile Find(string? key, string? name)
        => TryFind(key, name, out var profile) ? profile : Profiles[0];
}
