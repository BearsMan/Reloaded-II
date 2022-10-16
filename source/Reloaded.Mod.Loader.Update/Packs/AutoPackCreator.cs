﻿namespace Reloaded.Mod.Loader.Update.Packs;

/// <summary>
/// Utility class for automatic creation of packages based on a given sample input of mods.
/// </summary>
public static class AutoPackCreator
{
    /// <summary>
    /// Checks if all mods in given list can be used in the pack by verifying they have enabled updates.
    /// </summary>
    /// <param name="configurations">List of mod configurations to check.</param>
    /// <param name="incompatibleMods">List of mods to check for compatibility.</param>
    /// <returns>True if mods can be packed, else false.</returns>
    public static bool ValidateCanCreate(IEnumerable<PathTuple<ModConfig>> configurations, out List<PathTuple<ModConfig>> incompatibleMods)
    {
        incompatibleMods = new List<PathTuple<ModConfig>>();
        foreach (var config in configurations)
        {
            if (!ValidateCanCreate(config))
                incompatibleMods.Add(config);
        }

        return incompatibleMods.Count <= 0;
    }

    /// <summary>
    /// Checks if mod can be used in the pack by verifying they have enabled updates.
    /// </summary>
    /// <param name="configuration">The mod to check.</param>
    /// <returns>True if mods can be packed, else false.</returns>
    public static bool ValidateCanCreate(PathTuple<ModConfig> configuration) => PackageResolverFactory.HasAnyConfiguredResolver(configuration);

    /// <summary>
    /// Automatically creates a package.
    /// </summary>
    /// <param name="configurations">The configurations used to create the config. Must at least have update data, ModId and ModName.</param>
    /// <param name="imageConverter">Used for converting images.</param>
    /// <param name="packageProviders">Providers that can be used to search for packages.</param>
    /// <param name="token">Token to cancel the operation.</param>
    public static async Task<ReloadedPackBuilder> CreateAsync(IEnumerable<ModConfig> configurations, IModPackImageConverter imageConverter, IList<IDownloadablePackageProvider> packageProviders, CancellationToken token = default)
    {
        var builder = new ReloadedPackBuilder();
        
        builder.SetName("My Autogenerated Package");
        builder.SetReadme("You should probably add description here.");

        foreach (var config in configurations)
            await CreateMod(builder, config, imageConverter, packageProviders, token);

        return builder;
    }

    /// <summary>
    /// Adds the given mod to the provided builder using autogeneration.
    /// </summary>
    /// <param name="builder">Adds an existing mod to the given builder.</param>
    /// <param name="config">Mod configuration which to add.</param>
    /// <param name="imageConverter">Image converter used to create the contained images.</param>
    /// <param name="packageProviders">Providers used to resolve the extra package details like id and logo.</param>
    /// <param name="token">Cancellation token.</param>
    public static async Task<ReloadedPackItemBuilder> CreateMod(ReloadedPackBuilder builder, ModConfig config, IModPackImageConverter imageConverter, 
        IList<IDownloadablePackageProvider> packageProviders, CancellationToken token)
    {
        var imageDownloader = new ImageCacheService();
        var itemBuilder = builder.AddModItem(config.ModId);
        itemBuilder.SetName(config.ModName);
        itemBuilder.SetPluginData(config.PluginData);

        var bestPkg = await GetBestPackageForTemplateAsync(config.ModId, config.ModName, packageProviders, token);

        // Add images if possible
        if (bestPkg.Images != null)
        {
            foreach (var image in bestPkg.Images)
            {
                var file = await imageDownloader.GetOrDownloadFileFromUrl(image.Uri, imageDownloader.ModPreviewExpiration, false, token);
                var converted = imageConverter.Convert(file, Path.GetExtension(image.Uri.ToString()), out string ext);
                itemBuilder.AddImage(converted, ext, image.Caption);
            }
        }

        // Add readme if possible
        if (!string.IsNullOrEmpty(bestPkg.MarkdownReadme))
            itemBuilder.SetReadme(bestPkg.MarkdownReadme);
        
        // Add summary if possible
        if (!string.IsNullOrEmpty(bestPkg.Summary))
            itemBuilder.SetSummary(bestPkg.Summary);

        return itemBuilder;
    }

    /// <summary>
    /// Automatically obtains an image and readme for the given mod.
    /// </summary>
    /// <param name="modId">ID of the mod.</param>
    /// <param name="modName">Name of the mod.</param>
    /// <param name="downloadablePackageProviders">Providers that can be used to download the package.</param>
    /// <param name="cancellationToken">Token used to cancel the package.</param>
    /// <returns>Tuple of images and readme for the given item.</returns>
    public static async Task<GetBestPackageForTemplateResult> GetBestPackageForTemplateAsync(string modId, string modName,
        IList<IDownloadablePackageProvider> downloadablePackageProviders, CancellationToken cancellationToken)
    {
        GetBestPackageForTemplateResult result = new();
        
        // We will get all packages with highest version and copy images & readme down the road.
        NuGetVersion? highestVersion = null;
        List<IDownloadablePackage> itemsForHighestVersion = new List<IDownloadablePackage>();

        // Note: Some sources don't support e.g. images.
        // We prioritise sources based on whether they contain images, then by readme.
        // If preferred source does not contain both readme and image, we stitch them together from sources that do.
        foreach (var provider in downloadablePackageProviders)
        {
            // Override if better than current best.
            var candidates = await provider.SearchForModAsync(modId, modName, 50, 4, true, cancellationToken);
            foreach (var candidate in candidates)
            {
                // Find items for highest version
                if (candidate.Version != null)
                {
                    if (highestVersion == null)
                        highestVersion = candidate.Version;

                    if (candidate.Version == highestVersion)
                        itemsForHighestVersion.Add(candidate);

                    if (candidate.Version > highestVersion)
                    {
                        itemsForHighestVersion.Clear();
                        highestVersion = candidate.Version;
                        itemsForHighestVersion.Add(candidate);
                    }
                }
                
                // Set images if more than existing best result.
                if (candidate.Images != null)
                {
                    // Assign images if unassigned
                    result.Images ??= candidate.Images;

                    // Prefer source with more images.
                    if (candidate.Images.Length > result.Images.Length)
                        result.Images = candidate.Images;
                }
                    
                // Set description if unassigned.
                if (candidate.MarkdownReadme != null)
                    result.MarkdownReadme ??= candidate.MarkdownReadme;
            }
        }
        
        // Override supported items from 
        foreach (var item in itemsForHighestVersion)
        {
            // Set description if unassigned.
            if (item.MarkdownReadme != null)
                result.MarkdownReadme = item.MarkdownReadme;

            // Set description if unassigned.
            if (item.Description != null)
                result.Summary = item.Description;

            // Set images to ones from newest version in case older version had more images.
            // But only if more than 1 image. We want to filter out entries with only thumbnail.
            if (item.Images is { Length: > 1 })
                result.Images = item.Images;
        }
        
        return result;
    }
    
    /// <summary>
    /// Represents the result of trying to obtain the best package for template.
    /// </summary>
    /// <param name="Images">Images for the mod.</param>
    /// <param name="MarkdownReadme">Full readme (in markdown) for this mod.</param>
    /// <param name="Summary">Short summary of the mod.</param>
    public record struct GetBestPackageForTemplateResult(DownloadableImage[]? Images, string? MarkdownReadme, string? Summary);
}