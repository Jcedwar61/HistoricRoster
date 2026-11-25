using FluentValidation;

namespace HistoricRosterPlugin;

public class HistoricRosterConfigurationValidator : AbstractValidator<HistoricRosterConfiguration>
{
    public HistoricRosterConfigurationValidator()
    {
        RuleFor(cfg => cfg.RosterFolder).NotEmpty();
        RuleFor(cfg => cfg.CatalogFile).NotEmpty();
        // ContentFolder may be empty: then it is found automatically (server "content" folder, or Steam's AC install).
        RuleFor(cfg => cfg.MaxSlots).GreaterThanOrEqualTo(0);
        RuleFor(cfg => cfg.DefaultGridOrder).IsInEnum();
        RuleFor(cfg => cfg.MinYear).InclusiveBetween(1950, 2100);
        RuleFor(cfg => cfg.MaxYear).Must(y => y == 0 || y is >= 1950 and <= 2100);
        RuleFor(cfg => cfg.RaceLengthPercent).InclusiveBetween(0, 1000);
        RuleFor(cfg => cfg.StandIns).IsInEnum();
        RuleFor(cfg => cfg.DownloadSearchUrl).Must(u => u.Contains("{query}")).WithMessage("DownloadSearchUrl must contain {query}");
    }
}
