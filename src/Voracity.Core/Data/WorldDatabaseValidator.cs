using FluentValidation;
using Voracity.Core.Models;

namespace Voracity.Core.Data;

public sealed class GoodDefinitionValidator : AbstractValidator<GoodDefinition>
{
    public GoodDefinitionValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty();
        RuleFor(x => x.BasePriceGold).GreaterThan(0.0);
        RuleFor(x => x.FoodValue).GreaterThanOrEqualTo(0.0);
        RuleFor(x => x.TransportCost).GreaterThan(0.0);
    }
}

public sealed class SeaZoneDefinitionValidator : AbstractValidator<SeaZoneDefinition>
{
    public SeaZoneDefinitionValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty();
        RuleFor(x => x.SeedX).InclusiveBetween(0.0f, 1.0f);
        RuleFor(x => x.SeedY).InclusiveBetween(0.0f, 1.0f);
        RuleFor(x => x.MaritimePresence).InclusiveBetween(0.0, 1.0);
    }
}

public sealed class SpecialLocationDefinitionValidator : AbstractValidator<SpecialLocationDefinition>
{
    public SpecialLocationDefinitionValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty();
        RuleFor(x => x.Kind).Must(k => k is LocationKind.Impassable or LocationKind.Corridor)
            .WithMessage("Special locations must have Kind Impassable or Corridor.");
        RuleFor(x => x.SeedX).InclusiveBetween(0.0f, 1.0f);
        RuleFor(x => x.SeedY).InclusiveBetween(0.0f, 1.0f);
    }
}

public sealed class MarketDefinitionValidator : AbstractValidator<MarketDefinition>
{
    public MarketDefinitionValidator(IReadOnlySet<string> validLocationIds)
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.NameKey).NotEmpty();
        RuleFor(x => x.CenterLocationId).NotEmpty()
            .Must(validLocationIds.Contains)
            .WithMessage(m => $"Market '{m.Id}' center location '{m.CenterLocationId}' does not exist.");
        RuleFor(x => x.BaseAttraction).GreaterThan(0.0);
    }
}

public sealed class WorldDatabaseValidator : AbstractValidator<WorldDatabase>
{
    public WorldDatabaseValidator()
    {
        RuleFor(x => x.Goods).NotEmpty();
        RuleForEach(x => x.Goods).SetValidator(new GoodDefinitionValidator());
        RuleFor(x => x.Goods)
            .Must(goods => goods.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count() == goods.Count)
            .WithMessage("Duplicate good IDs detected in catalog.");

        RuleForEach(x => x.SeaZones).SetValidator(new SeaZoneDefinitionValidator());
        RuleForEach(x => x.SpecialLocations).SetValidator(new SpecialLocationDefinitionValidator());

        RuleFor(x => x).Custom((db, ctx) =>
        {
            var locIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var loc in db.BuildAllLocations())
            {
                if (!locIds.Add(loc.Id))
                    ctx.AddFailure("Locations", $"Duplicate Location ID '{loc.Id}'.");
            }

            var marketValidator = new MarketDefinitionValidator(locIds);
            foreach (var market in db.Markets)
            {
                var res = marketValidator.Validate(market);
                foreach (var err in res.Errors)
                    ctx.AddFailure($"Markets.{market.Id}.{err.PropertyName}", err.ErrorMessage);
            }
        });
    }
}
