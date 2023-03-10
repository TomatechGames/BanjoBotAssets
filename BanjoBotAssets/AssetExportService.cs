﻿/* Copyright 2023 Tara "Dino" Cassatt
 * 
 * This file is part of BanjoBotAssets.
 * 
 * BanjoBotAssets is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 * 
 * BanjoBotAssets is distributed in the hope that it will be useful, but
 * WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with BanjoBotAssets.  If not, see <http://www.gnu.org/licenses/>.
 */
using BanjoBotAssets.Aes;
using BanjoBotAssets.Artifacts;
using BanjoBotAssets.Artifacts.Models;
using BanjoBotAssets.Config;
using BanjoBotAssets.Exporters;
using BanjoBotAssets.PostExporters;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace BanjoBotAssets
{
    internal sealed class AssetExportService : BackgroundService
    {
        private readonly ILogger<AssetExportService> logger;
        private readonly IHostApplicationLifetime lifetime;
        private readonly List<IExporter> exportersToRun;
        private readonly IOptions<GameFileOptions> options;
        private readonly IEnumerable<IAesProvider> aesProviders;
        private readonly IAesCacheUpdater aesCacheUpdater;
        private readonly IEnumerable<IExportArtifact> exportArtifacts;
        private readonly AbstractVfsFileProvider provider;
        private readonly ITypeMappingsProviderFactory typeMappingsProviderFactory;
        private readonly IOptions<ScopeOptions> scopeOptions;
        private readonly IEnumerable<IPostExporter> allPostExporters;

        private readonly ConcurrentDictionary<string, byte> failedAssets = new();

        public AssetExportService(ILogger<AssetExportService> logger,
            IHostApplicationLifetime lifetime,
            IEnumerable<IExporter> allExporters,
            IOptions<GameFileOptions> options,
            IEnumerable<IAesProvider> aesProviders,
            IAesCacheUpdater aesCacheUpdater,
            IEnumerable<IExportArtifact> exportArtifacts,
            AbstractVfsFileProvider provider,
            ITypeMappingsProviderFactory typeMappingsProviderFactory,
            IOptions<ScopeOptions> scopeOptions,
            IEnumerable<IPostExporter> allPostExporters)
        {
            this.logger = logger;
            this.lifetime = lifetime;
            this.options = options;
            this.aesProviders = aesProviders;
            this.aesCacheUpdater = aesCacheUpdater;
            this.exportArtifacts = exportArtifacts;
            this.provider = provider;
            this.typeMappingsProviderFactory = typeMappingsProviderFactory;
            this.scopeOptions = scopeOptions;
            this.allPostExporters = allPostExporters;
            exportersToRun = new(allExporters);

            if (!string.IsNullOrWhiteSpace(scopeOptions.Value.Only))
            {
                var wanted = scopeOptions.Value.Only.Split(',');
                exportersToRun.RemoveAll(e => !wanted.Contains(e.GetType().Name, StringComparer.OrdinalIgnoreCase));
            }
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            Environment.ExitCode = await RunAsync(cancellationToken);
            lifetime.StopApplication();
        }

        private sealed class CriticalFailureException : ApplicationException
        {
            public CriticalFailureException()
            {
            }

            public CriticalFailureException(string? message) : base(message)
            {
            }

            public CriticalFailureException(string? message, Exception? innerException) : base(message, innerException)
            {
            }
        }

        private struct AssetLoadingStats
        {
            public int AssetsLoaded { get; set; }
            public TimeSpan Elapsed { get; set; }

            public AssetLoadingStats(int assetsLoaded, TimeSpan elapsed)
            {
                AssetsLoaded = assetsLoaded;
                Elapsed = elapsed;
            }

            public static AssetLoadingStats operator +(AssetLoadingStats a, AssetLoadingStats b)
            {
                return new(a.AssetsLoaded + b.AssetsLoaded, a.Elapsed + b.Elapsed);
            }
        }

        private async Task<int> RunAsync(CancellationToken cancellationToken)
        {
            // by the time this method is called, the CUE4Parse file provider has already been created,
            // and the game files have been located but not decrypted. we need to supply the AES keys,
            // from cache or from an external API.
            await DecryptGameFilesAsync(cancellationToken);

            // load the type mappings CUE4Parse uses to parse UE structures
            await LoadMappingsAsync(cancellationToken);

            // load localized resources
            LoadLocalization(cancellationToken);

            // register the export classes used to expose UE structures as strongly-typed C# objects
            RegisterExportTypes();

            // feed the file list to each exporter so they can record the paths they're interested in
            OfferFileListToExporters();

            // run exporters and collect their intermediate results
            var (exportedAssets, exportedRecipes, stats1) = await RunSelectedExportersAsync(cancellationToken);

            // run post-exporters to refine the intermediate results
            var stats2 = await RunSelectedPostExportersAsync(exportedAssets, exportedRecipes, cancellationToken);

            // report assets loaded and time elapsed
            ReportAssetLoadingStats(stats1 + stats2);

            // generate output artifacts
            await GenerateSelectedArtifactsAsync(exportedAssets, exportedRecipes, cancellationToken);

            // report cache stats
            (provider as CachingFileProvider)?.ReportCacheStats();

            // report any export failures
            ReportFailedAssets();

            // done!
            return 0;
        }

        private void ReportAssetLoadingStats(AssetLoadingStats stats)
        {
            logger.LogInformation(Resources.Status_LoadedAssets, stats.AssetsLoaded, stats.Elapsed, stats.Elapsed.TotalMilliseconds / Math.Max(stats.AssetsLoaded, 1));
        }

        private async Task DecryptGameFilesAsync(CancellationToken cancellationToken)
        {
            // get the keys from cache or a web service
            AesApiResponse? aes = null;

            foreach (var ap in aesProviders)
            {
                if (await ap.TryGetAesAsync(cancellationToken) is { } good)
                {
                    aes = good;
                    await aesCacheUpdater.UpdateAesCacheAsync(aes, cancellationToken);
                    break;
                }
            }

            if (aes == null)
                throw new CriticalFailureException(Resources.Error_AesFetchFailed);

            // offer them to CUE4Parse
            logger.LogInformation(Resources.Status_DecryptingGameFiles);

            if (aes.MainKey != null)
            {
                logger.LogDebug(Resources.Status_SubmittingMainKey);
                provider.SubmitKey(new FGuid(), new FAesKey(aes.MainKey));
            }
            else
            {
                logger.LogDebug(Resources.Status_SkippingNullMainKey);
            }

            foreach (var dk in aes.DynamicKeys)
            {
                logger.LogDebug(Resources.Status_SubmittingDynamicKey, dk.PakFilename);
                provider.SubmitKey(new FGuid(dk.PakGuid), new FAesKey(dk.Key));
            }
        }

        private async Task LoadMappingsAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation(Resources.Status_LoadingMappings);

            if (provider.GameName.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase))
            {
                provider.MappingsContainer = typeMappingsProviderFactory.Create();
            }

            // sometimes the mappings don't load, and then nothing works
            if (provider.MappingsForGame is null or { Enums.Count: 0, Types.Count: 0 })
            {
                await Task.Delay(5 * 1000, cancellationToken);
                logger.LogWarning(Resources.Status_RetryingMappings);
                provider.MappingsContainer = typeMappingsProviderFactory.Create();

                if (provider.MappingsForGame is null or { Enums.Count: 0, Types.Count: 0 })
                    throw new CriticalFailureException(Resources.Error_MappingsFetchFailed);
            }
        }

        private void RegisterExportTypes()
        {
            logger.LogInformation(Resources.Status_RegisteringExportTypes);
            ObjectTypeRegistry.RegisterEngine(typeof(UFortItemDefinition).Assembly);
            ObjectTypeRegistry.RegisterClass("FortDefenderItemDefinition", typeof(UFortHeroType));
            ObjectTypeRegistry.RegisterClass("FortTrapItemDefinition", typeof(UFortItemDefinition));
            ObjectTypeRegistry.RegisterClass("FortAlterationItemDefinition", typeof(UFortItemDefinition));
            ObjectTypeRegistry.RegisterClass("FortResourceItemDefinition", typeof(UFortWorldItemDefinition));
            ObjectTypeRegistry.RegisterClass("FortGameplayModifierItemDefinition", typeof(UFortItemDefinition));
            ObjectTypeRegistry.RegisterClass("StWFortAccoladeItemDefinition", typeof(UFortItemDefinition));
            ObjectTypeRegistry.RegisterClass("FortQuestItemDefinition_Campaign", typeof(UFortQuestItemDefinition));
        }

        private async Task GenerateSelectedArtifactsAsync(ExportedAssets exportedAssets, IList<ExportedRecipe> exportedRecipes, CancellationToken cancellationToken)
        {
            foreach (var art in exportArtifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await art.RunAsync(exportedAssets, exportedRecipes, cancellationToken);
            }
        }

        private async Task<AssetLoadingStats> RunSelectedPostExportersAsync(ExportedAssets exportedAssets, IList<ExportedRecipe> exportedRecipes, CancellationToken cancellationToken)
        {
            var stopwatch = new Stopwatch();

            var progress = new Progress<ExportProgress>(HandleProgressReport);
            var postExporters = allPostExporters.ToList();

            stopwatch.Start();

            await Task.WhenAll(postExporters.Select(pe => pe.ProcessExportsAsync(exportedAssets, exportedRecipes, cancellationToken)));

            stopwatch.Stop();

            return new AssetLoadingStats(postExporters.Sum(pe => pe.AssetsLoaded), stopwatch.Elapsed);
        }

        private async Task<(ExportedAssets, IList<ExportedRecipe>, AssetLoadingStats)> RunSelectedExportersAsync(CancellationToken cancellationToken)
        {
            // run the exporters and collect their outputs
            var stopwatch = new Stopwatch();

            // give each exporter its own output object to use,
            // we'll combine the results when the tasks all complete.
            var allPrivateExports = new List<IAssetOutput>(exportersToRun.Select(_ => new AssetOutput()));

            // run the exporters!
            if (!string.IsNullOrWhiteSpace(scopeOptions.Value.Only))
            {
                logger.LogInformation(Resources.Status_RunningSelectedExporters, exportersToRun.Count, string.Join(", ", exportersToRun.Select(t => t.GetType().Name)));
            }
            else
            {
                logger.LogInformation(Resources.Status_RunningAllExporters);
            }

            var progress = new Progress<ExportProgress>(HandleProgressReport);

            stopwatch.Start();

            await Task.WhenAll(
                exportersToRun.Zip(allPrivateExports, (e, r) => e.ExportAssetsAsync(progress, r, cancellationToken)));

            stopwatch.Stop();

            var exportedAssets = new ExportedAssets();
            var exportedRecipes = new List<ExportedRecipe>();
            var assetsLoaded = exportersToRun.Sum(e => e.AssetsLoaded);

            // combine intermediate outputs
            foreach (var privateExport in allPrivateExports)
            {
                cancellationToken.ThrowIfCancellationRequested();
                privateExport.CopyTo(exportedAssets, exportedRecipes, cancellationToken);
            }

            foreach (var privateExport in allPrivateExports)
            {
                cancellationToken.ThrowIfCancellationRequested();
                privateExport.ApplyDisplayNameCorrections(exportedAssets);
            }

            allPrivateExports.Clear();
            return (exportedAssets, exportedRecipes, new AssetLoadingStats { AssetsLoaded = assetsLoaded, Elapsed = stopwatch.Elapsed });
        }

        private void HandleProgressReport(ExportProgress progress)
        {
            if (progress.FailedAssets?.Any() == true)
            {
                foreach (var i in progress.FailedAssets)
                {
                    failedAssets.TryAdd(i, 0);
                }
            }

            // TODO: do something more with progress reports
        }

        private void ReportFailedAssets()
        {
            if (!failedAssets.IsEmpty)
            {
                logger.LogError(Resources.Error_FinishedWithFailedAssets, failedAssets.Count);

                foreach (var i in failedAssets.Keys.OrderBy(i => i))
                {
                    logger.LogError(Resources.Error_FailedAsset, i);
                }
            }
        }

        private void OfferFileListToExporters()
        {
            logger.LogInformation(Resources.Status_AnalyzingFileList);

            foreach (var (name, file) in provider.Files)
            {
                if (name.Contains("/Athena/", StringComparison.OrdinalIgnoreCase) ||
                    (!name.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                     !name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                foreach (var e in exportersToRun)
                {
                    e.ObserveAsset(name);
                }
            }
        }

        private void LoadLocalization(CancellationToken cancellationToken)
        {
            var language = GetLocalizationLanguage();
            logger.LogInformation(Resources.Status_LoadingLocalization, language.ToString());
            provider.LoadLocalization(language, cancellationToken);
        }

        private ELanguage GetLocalizationLanguage()
        {
            if (!string.IsNullOrEmpty(options.Value.ELanguage) && Enum.TryParse<ELanguage>(options.Value.ELanguage, out var result))
                return result;

            return Enum.Parse<ELanguage>(Resources.ELanguage);
        }
    }
}
