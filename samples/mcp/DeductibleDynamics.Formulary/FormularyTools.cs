using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DeductibleDynamics.Formulary;

/// <summary>A drug on the Deductible Dynamics formulary. All data is synthetic.</summary>
public sealed record FormularyDrug(string Name, string BrandNames, string DrugClass, int Tier, bool PriorAuthorization, string? QuantityLimit, string? Notes);

public sealed record FormularyTier(int Tier, string Name, string MemberCost);

/// <summary>
/// The server's two tools. Both are read only, and say so in their annotations, but PriorAuthBot
/// does not take a server's word for that: its own allowlist decides (chapter 10).
/// </summary>
[McpServerToolType]
public sealed class FormularyTools
{
    private FormularyTools()
    {
    }

    [McpServerTool(Name = "search_formulary", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Searches the Deductible Dynamics drug formulary by drug name, brand name, or drug class. Returns each matching drug's tier, whether it needs prior authorization, and any quantity limit.")]
    public static IReadOnlyList<FormularyDrug> SearchFormulary(
        [Description("A drug name, brand name, or drug class, such as atorvastatin, Ozempic, or statin.")] string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var words = query.ToLowerInvariant().Split([' ', ',', '?', '.', '!'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4).ToList();
        return [.. FormularyData.Drugs.Where(d => words.Any(w =>
            d.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
            || d.BrandNames.Contains(w, StringComparison.OrdinalIgnoreCase)
            || d.DrugClass.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(10)];
    }

    [McpServerTool(Name = "list_tiers", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the formulary tiers and what a member pays for a 30-day supply at each tier.")]
    public static IReadOnlyList<FormularyTier> ListTiers() => FormularyData.Tiers;
}

internal static class FormularyData
{
    public static readonly IReadOnlyList<FormularyTier> Tiers =
    [
        new(1, "Preferred generic", "$5 copay"),
        new(2, "Generic", "$15 copay"),
        new(3, "Preferred brand", "$45 copay"),
        new(4, "Non-preferred brand", "$90 copay"),
        new(5, "Specialty", "25% coinsurance, up to $250"),
    ];

    public static readonly IReadOnlyList<FormularyDrug> Drugs =
    [
        new("atorvastatin", "Lipitor", "statin", 1, false, null, null),
        new("rosuvastatin", "Crestor", "statin", 2, false, null, null),
        new("lisinopril", "Zestril, Prinivil", "ACE inhibitor", 1, false, null, null),
        new("metformin", "Glucophage", "biguanide (diabetes)", 1, false, null, null),
        new("sertraline", "Zoloft", "SSRI antidepressant", 1, false, null, null),
        new("insulin glargine", "Lantus, Basaglar", "long-acting insulin", 3, false, "3 pens per 30 days", null),
        new("semaglutide", "Ozempic", "GLP-1 agonist (diabetes)", 3, true, "1 pen per 28 days", "Covered for type 2 diabetes only; not covered for weight loss."),
        new("adalimumab", "Humira", "TNF blocker (specialty)", 5, true, "2 doses per 28 days", "Biosimilars are preferred; filled by the specialty pharmacy."),
        new("apixaban", "Eliquis", "anticoagulant", 3, false, null, null),
        new("montelukast", "Singulair", "leukotriene blocker (asthma)", 1, false, null, null),
        new("albuterol inhaler", "ProAir, Ventolin", "rescue inhaler (asthma)", 2, false, "2 inhalers per 30 days", null),
        new("esomeprazole", "Nexium", "proton pump inhibitor", 4, true, null, "Try omeprazole (tier 1) first."),
        new("omeprazole", "Prilosec", "proton pump inhibitor", 1, false, null, null),
        new("zolpidem", "Ambien", "sleep aid", 2, false, "30 tablets per 30 days", null),
    ];
}
