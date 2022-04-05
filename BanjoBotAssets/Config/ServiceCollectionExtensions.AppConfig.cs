﻿using BanjoBotAssets.Aes;
using BanjoBotAssets.Artifacts;
using BanjoBotAssets.Artifacts.Helpers;
using BanjoBotAssets.Config;
using BanjoBotAssets.Exporters;
using BanjoBotAssets.Exporters.Helpers;
using BanjoBotAssets.PostExporters;
using CUE4Parse.UE4.Versions;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Sinks.ILogger;

namespace BanjoBotAssets.Extensions
{
    internal static partial class ServiceCollectionExtensions
    {
        public static IServiceCollection AddAesProviders(this IServiceCollection services)
        {
            services
                .AddTransient<IAesProvider, FileAesProvider>()
                .AddTransient<IAesProvider, FortniteApiAesProvider>()
                .AddTransient<IAesCacheUpdater, FileAesProvider>();

            services
                .AddOptions<AesOptions>()
                .Configure<IConfiguration>((options, config) =>
                {
                    options.AesApiUri = "https://fortnite-api.com/v2/aes";
                    options.LocalFilePath = "aes.json";
                    config.GetSection(nameof(AesOptions)).Bind(options);
                });

            return services;
        }

        public static IServiceCollection AddAssetExporters(this IServiceCollection services)
        {
            // all IExporter implementations derived from BaseExporter,
            // and their service aggregator and helpers
            services
                .AddDerivedServices<IExporter, BaseExporter>(ServiceLifetime.Transient)
                .AddTransient<IExporterContext, ExporterContext>()
                .AddTransient<AbilityDescription>();

            // performance options affecting the exporters
            services
                .AddOptions<PerformanceOptions>()
                .Configure<IConfiguration>((options, config) =>
                {
                    options.MaxParallelism = 1;
                    config.GetRequiredSection("PerformanceOptions").Bind(options);
                });

            // post-exporter for image files and its options
            services
                .AddTransient<IPostExporter, ImageFilesPostExporter>()
                .AddOptions<ImageExportOptions>()
                .Configure<IConfiguration>((options, config) =>
                {
                    options.Type = new()
                    {
                        [ImageType.LargePreview] = WantImageExport.Yes,
                        [ImageType.SmallPreview] = WantImageExport.Yes,
                        [ImageType.Icon] = WantImageExport.Yes,
                    };
                    options.OutputDirectory = Resources.File_ExportedImages;
                    config.GetSection(nameof(ImageExportOptions)).Bind(options);
                });

            // all artifacts use global Merge option by default
            services
                .ConfigureAll<ExportedFileOptions, IOptions<ScopeOptions>>(
                        (options, scopeOptions) => options.Merge = scopeOptions.Value.Merge);

            // artifact generator for assets.json and its options
            services
                .AddTransientWithNamedOptions<IExportArtifact, AssetsJsonArtifact, ExportedFileOptions>()
                .Configure<IConfiguration, IOptions<ScopeOptions>>((options, config, scopeOptions) =>
                {
                    options.Path = Resources.File_assets_json;
                    options.Merge = scopeOptions.Value.Merge;
                    config.GetRequiredSection("ExportedAssets").Bind(options);
                });

            // artifact generator for schematics.json and its options
            services
                .AddTransientWithNamedOptions<IExportArtifact, SchematicsJsonArtifact, ExportedFileOptions>()
                .Configure<IConfiguration, IOptions<ScopeOptions>>((options, config, scopeOptions) =>
                {
                    options.Path = Resources.File_schematics_json;
                    options.Merge = scopeOptions.Value.Merge;
                    config.GetRequiredSection("ExportedSchematics").Bind(options);
                });

            // JSON contract resolvers used by the artifact generators
            services
                .AddSingleton<IgnoreImagePathsContractResolver>()
                .AddSingleton<NullToEmptyStringContractResolver>();

            return services;
        }

        public static IServiceCollection ConfigureAll<TOptions, TDep1>(this IServiceCollection services, Action<TOptions, TDep1> configureOptions)
            where TOptions : class
            where TDep1 : class
        {
            return services
                .AddTransient<IConfigureOptions<TOptions>>(sp =>
                    new ConfigureNamedOptions<TOptions, TDep1>(
                        name: null,
                        sp.GetRequiredService<TDep1>(),
                        configureOptions));
        }

        public static OptionsBuilder<TOptions> AddTransientWithNamedOptions<TService, TImplementation, TOptions>(this IServiceCollection services)
            where TService : class
            where TImplementation : TService
            where TOptions : class
        {
            return services
                .AddTransient<TService>(sp =>
                {
                    using var scope = sp.CreateScope();
                    var opts = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<TOptions>>();
                    return ActivatorUtilities.CreateInstance<TImplementation>(sp, opts.Get(typeof(TImplementation).FullName));
                })
                .AddOptions<TOptions>(typeof(TImplementation).FullName);
        }

        public static IServiceCollection AddGameFileProvider(this IServiceCollection services)
        {
            services.AddSingleton((Func<IServiceProvider, AbstractVfsFileProvider>)(sp =>
                 {
                     var options = sp.GetRequiredService<IOptions<GameFileOptions>>();

                     string gameDirectory;
                     try
                     {
                         gameDirectory = options.Value.GameDirectories.First(Directory.Exists);
                     }
                     catch (InvalidOperationException ex)
                     {
                         throw new InvalidOperationException(Resources.Error_GameNotFound, ex);
                     }

                     var cache = sp.GetRequiredService<AssetCache>();
                     var logger = sp.GetRequiredService<ILogger<CachingFileProvider>>();

                     // set up Serilog global logger
                     Log.Logger = new LoggerConfiguration()
                        .WriteTo.ILogger(sp.GetRequiredService<ILogger<CachingFileProvider>>())
                        .CreateLogger();

                     var provider = new CachingFileProvider(
                         cache: cache,
                         logger: logger,
                         directory: gameDirectory,
                         searchOption: SearchOption.TopDirectoryOnly,
                         isCaseInsensitive: true,
                         versions: new VersionContainer(EGame.GAME_UE5_LATEST));

                     provider.Initialize();
                     return provider;
                 }));

            services.AddOptions<GameFileOptions>()
                .Configure<IConfiguration>((options, config) =>
                {
                    options.ELanguage = "";
                    options.GameDirectories = Array.Empty<string>();
                    config.GetSection(nameof(GameFileOptions)).Bind(options);
                });

            services.AddSingleton<AssetCache>();

            services.AddOptions<CacheOptions>()
                .Configure<IConfiguration>((options, config) =>
                {
                    options.SizeLimit = 100 * 1024 * 1024;
                    config.GetSection(nameof(CacheOptions)).Bind(options);
                });

            return services;
        }

        public static IServiceCollection AddMappingsProviders(this IServiceCollection services)
        {
            services
                .AddTransient<ITypeMappingsProviderFactory, CachingBenBotMappingsProviderFactory>();

            services.AddOptions<MappingsOptions>()
                .Configure<IConfiguration>((options, config) =>
                {
                    options.MappingsApiUri = "https://benbot.app/api/v1/mappings";
                    options.LocalFilePath = "mappings.usmap";
                    config.GetSection(nameof(MappingsOptions)).Bind(options);
                });

            return services;
        }
    }
}