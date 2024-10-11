using CUE4Parse.UE4.Assets.Exports;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BanjoBotAssets.Exporters.UObjects
{
    internal sealed class SurvivorPortraitExporter(IExporterContext services) : UObjectExporter(services)
    {
        protected override string Type => "WorkerPortrait";

        protected override bool InterestedInAsset(string name) => name.Contains("/Icon-Worker/IconDefinitions", StringComparison.OrdinalIgnoreCase);

        protected override Task<bool> ExportAssetAsync(UObject asset, NamedItemData itemData, Dictionary<ImageType, string> imagePaths)
        {
            string? smallPreviewPath = asset.GetSoftAssetPathFromDataList("Icon");
            string? largePreviewPath = asset.GetSoftAssetPathFromDataList("LargeIcon") ?? smallPreviewPath;
            smallPreviewPath ??= largePreviewPath;

            if (smallPreviewPath is not null)
                imagePaths.Add(ImageType.SmallPreview, smallPreviewPath);

            if (largePreviewPath is not null)
                imagePaths.Add(ImageType.LargePreview, largePreviewPath);

            return base.ExportAssetAsync(asset, itemData, imagePaths);
        }
    }
}
