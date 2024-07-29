using System.Text.Json.Nodes;

namespace BanjoBotAssets
{
    internal static class PegLegPostProcessor
    {
        public enum ImageMode
        {
            Ignore,
            Move,
            Copy
        }
        public static void PostProcessBanjoAssets(string banjoBotOutputPath, string destinationPath, ImageMode imageMode)
        {
            DirectoryInfo banjoDir = new(banjoBotOutputPath);
            DirectoryInfo destinationDir = new(destinationPath);

            if (!banjoDir.Exists || !destinationDir.Exists)
            {
                Console.WriteLine("no folders");
                return;
            }

            FileInfo banjoFile = new(banjoDir.FullName + "/assets.json");

            if (!banjoFile.Exists)
                return;

            JsonObject resultDatabase = banjoFile.ReadJSON().AsObject();

            KeyValuePair<string, JsonNode?>[] items = [.. (resultDatabase["NamedItems"]?.AsObject() ?? [])];

            object indexLock = new();
            int total = items.Length;
            int nextIndex = 0;

            Dictionary<string, JsonObject> splitItems = [];

            void ProcessItem()
            {
                while (nextIndex < total)
                {
                    int currentIndex;
                    lock (indexLock)
                    {
                        currentIndex = nextIndex;
                        nextIndex++;
                    }
                    if (currentIndex >= total)
                        break;
                    JsonObject? itemValue = JsonNode.Parse(items[currentIndex].Value?.ToString() ?? "{}")?.AsObject();

                    if (itemValue is null)
                        continue;
                    itemValue ??= [];

                    string itemKey = items[currentIndex].Key;

                    string[] splitKey = itemKey.Split(':');
                    if (splitKey.Length == 2)
                    {
                        splitKey[1] = splitKey[1].ToLower(CultureInfo.CurrentCulture);
                        itemKey = splitKey[0] + ":" + splitKey[1];
                    }

                    string? itemType = itemValue["Type"]?.ToString();
                    //itemValue.Remove("Type");

                    if (itemType is null)
                        continue;
                    //if (!itemType.EndsWith("s"))
                    //{
                    //    if (itemType.EndsWith("y"))
                    //        itemType = itemType[..^1] + "ies";
                    //    else
                    //        itemType += "s";
                    //}

                    lock (splitItems)
                    {
                        if (!splitItems.ContainsKey(itemType))
                            splitItems[itemType] = [];
                        splitItems[itemType][itemKey] = itemValue;
                    }
                    Console.WriteLine($"ported \"{itemValue?["DisplayName"]?.ToString()}\" to {itemType} object");
                }
            }

            Thread[] threads = new Thread[8];
            for (int i = 0; i < threads.Length; i++)
            {
                threads[i] = new(new ThreadStart(ProcessItem));
                threads[i].Start();
            }

            foreach (var remainingData in resultDatabase)
            {
                if (remainingData.Key == "NamedItems")
                    continue;
                string? stringified = remainingData.Value?.ToString();
                if (stringified?.StartsWith("{", StringComparison.CurrentCulture) != true)
                    continue;
                JsonObject? remainingObj = JsonNode.Parse(stringified ?? "{}")?.AsObject();
                lock (splitItems)
                {
                    if (remainingObj is not null)
                        splitItems[remainingData.Key] = remainingObj;
                }
            }

            ProcessItem();

            foreach (var item in splitItems)
            {
                if (item.Key.Contains('/'))
                {
                    Console.WriteLine("oops: " + item.Key);
                    continue;
                }
                Console.WriteLine("saved json: " + item.Key);
                FileInfo splitItemFile = new($"{destinationDir.FullName}/{item.Key}.json");
                using var writer = splitItemFile.CreateText();
                writer.WriteLine(item.Value.ToString());
                writer.Flush();
            }

            if (imageMode!=ImageMode.Ignore)
            {
                //Console.WriteLine("press enter to copy images");
                //Console.ReadLine();
                DirectoryInfo imagesSourceFolder = new(banjoDir.FullName + "/ExportedImages");
                DirectoryInfo imagesDestFolder = new(destinationDir.FullName + "/ExportedImages");
                if (!imagesSourceFolder.Exists)
                    return;
                if (!imagesDestFolder.Exists)
                    imagesDestFolder.Create();
                foreach (var oldFile in imagesSourceFolder.GetFiles())
                {
                    FileInfo newFile = new(imagesDestFolder.FullName + "/" + oldFile.Name);
                    if (newFile.Exists)
                    {
                        if (newFile.CreationTime.CompareTo(oldFile.CreationTime) < 0)
                            newFile.Delete();
                        else
                            continue;
                    }
                    if (imageMode == ImageMode.Copy)
                    {
                        Console.WriteLine("copied image: " + oldFile.Name);
                        oldFile.CopyTo(newFile.FullName);
                    }
                    else
                    {
                        Console.WriteLine("moved image: " + oldFile.Name);
                        oldFile.CopyTo(newFile.FullName);
                    }
                }
            }
            Console.WriteLine("all done");
        }

        static JsonNode ReadJSON(this FileInfo file, bool skipSafety = true)
        {
            // if we can guarentee there are no errors
            if (skipSafety)
            {
                using var textStream = file.OpenText();
                return JsonNode.Parse(textStream.ReadToEnd()) ?? new JsonObject();
            }

            string fileContent = "";
            string lastKey = "";
            ///this ugly mess is a workaround for an error in
            ///FModel that causes it to produce multiple key-value
            ///pairs with identical keys in the same context
            using (var textStream = file.OpenText())
            {
                string? line;
                while ((line = textStream.ReadLine()) != null)
                {
                    string[] splitLine = line.Split(':');
                    if (splitLine.Length > 1)
                    {
                        string trimmedKey = splitLine[0].Trim();
                        if (trimmedKey == lastKey)
                            continue;
                        lastKey = trimmedKey;
                    }
                    fileContent += line;
                }
            }
            return JsonNode.Parse(fileContent) ?? new JsonObject();
        }
    }
}
