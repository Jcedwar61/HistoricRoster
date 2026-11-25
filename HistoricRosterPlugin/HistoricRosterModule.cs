using AssettoServer.Server.Plugin;
using Autofac;
using Microsoft.Extensions.Hosting;

namespace HistoricRosterPlugin;

public class HistoricRosterModule : AssettoServerModule<HistoricRosterConfiguration>
{
    protected override void Load(ContainerBuilder builder)
    {
        // Shared code logs through RosterLog; send it to the server log.
        RosterLog.Info = m => Serilog.Log.Information("HistoricRoster: {Message}", m);
        RosterLog.Warn = m => Serilog.Log.Warning("HistoricRoster: {Message}", m);

        builder.RegisterType<RosterStore>().AsSelf().SingleInstance();
        builder.Register(c =>
        {
            var cfg = c.Resolve<HistoricRosterConfiguration>();
            var store = c.Resolve<RosterStore>();
            // '_' files in the roster folder are not rosters.
            RealConditions.CachePath = Path.Combine(store.Folder, "_conditions.json");
            Serilog.Log.Information("HistoricRoster: using content folder {Folder}", cfg.EffectiveContentFolder);
            return new ContentResolver(new ResolverOptions
            {
                ContentFolder = cfg.EffectiveContentFolder,
                CatalogFolder = store.Folder,
                CatalogFile = cfg.CatalogFile,
                DownloadSearchUrl = cfg.DownloadSearchUrl,
                KunosContentUrl = cfg.KunosContentUrl,
                ExtraSearchUrls = cfg.ExtraDownloadSearchUrls,
                FallbackSkin = cfg.FallbackSkin,
            });
        }).AsSelf().SingleInstance();
        builder.RegisterType<RosterInstaller>().AsSelf().SingleInstance();
        builder.RegisterType<F1Importer>().AsSelf().SingleInstance();
        builder.RegisterType<NascarImporter>().AsSelf().SingleInstance();
        builder.RegisterType<HistoricRosterService>().AsSelf().As<IHostedService>().SingleInstance();
    }
}
