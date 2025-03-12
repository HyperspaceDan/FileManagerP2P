using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using Syncfusion.Maui.Toolkit.Hosting;

using FileManager.Core.Interfaces;                    // For IFileSystem
using FileManager.Core.Models;                        // For QuotaConfiguration
using FileManagerP2P;              // For WindowsFileSystem
using System.IO;
using Microsoft.Maui.Storage;                        // For FileSystem
using FileManagerP2P.Services;
using System.Security;

#if WINDOWS
using FileManagerP2P.Platforms.Windows;              // Add Windows-specific namespace
#endif


namespace FileManagerP2P;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .ConfigureSyncfusionToolkit()
            .ConfigureMauiHandlers(handlers =>
            {
            })
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                fonts.AddFont("SegoeUI-Semibold.ttf", "SegoeSemibold");
                fonts.AddFont("FluentSystemIcons-Regular.ttf", FluentUI.FontFamily);
            });

#if DEBUG
        builder.Logging.AddDebug();
        builder.Services.AddLogging(configure => configure.AddDebug());
#endif

        builder.Services.AddSingleton<ProjectRepository>();
        builder.Services.AddSingleton<TaskRepository>();
        builder.Services.AddSingleton<CategoryRepository>();
        builder.Services.AddSingleton<TagRepository>();
        builder.Services.AddSingleton<SeedDataService>();
        builder.Services.AddSingleton<ModalErrorHandler>();
        builder.Services.AddSingleton<MainPageModel>();
        builder.Services.AddSingleton<ProjectListPageModel>();
        builder.Services.AddSingleton<ManageMetaPageModel>();

        builder.Services.AddTransientWithShellRoute<ProjectDetailPage, ProjectDetailPageModel>("project");
        builder.Services.AddTransientWithShellRoute<TaskDetailPage, TaskDetailPageModel>("task");

#if WINDOWS
						
						builder.Services.AddSingleton<FileManager.Core.Interfaces.IFileSystem>(sp => 
						{
							var logger = sp.GetRequiredService<ILogger<WindowsFileSystem>>();
							var basePath = Path.Combine(FileSystem.AppDataDirectory, "Files");
							var quotaConfig = new QuotaConfiguration
							{
								MaxSizeBytes = 1024 * 1024 * 1024, // 1GB default
								RootPath = basePath,
								WarningThreshold = 0.9f,
								EnforceQuota = true
							};

							return new WindowsFileSystem(logger, basePath, quotaConfig);
						});
#endif

        builder.Services.AddSingleton<IFileSystemPathProvider>(serviceProvider =>
            new SecureFileSystemPathProvider(FileSystem.AppDataDirectory));
#if WINDOWS
        // Register the file system with the async factory method
        builder.Services.AddSingleton<FileManager.Core.Interfaces.IFileSystem>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<WindowsFileSystem>>();
            var pathProvider = serviceProvider.GetRequiredService<IFileSystemPathProvider>();

            // We need to handle the async factory method since DI doesn't natively support async
            // This is a common pattern to handle async factory methods with sync DI
            // Create immediately with default path
            //Better approach with async factory and initialization tracking
            var fileSystem = new WindowsFileSystem(logger);

            // Start async initialization separately
            _ = InitializeFileSystemAsync(fileSystem, pathProvider, logger);

            return fileSystem;
        });
        static async Task InitializeFileSystemAsync(
            WindowsFileSystem fileSystem,
            IFileSystemPathProvider pathProvider,
            ILogger logger)
        {
            try
            {
                var customPath = await pathProvider.GetRootPathAsync();
                logger.LogInformation("Retrieved root path: {Path}", customPath);

                if (customPath != FileSystem.AppDataDirectory)
                {
                    logger.LogInformation("Custom path detected, starting migration from {OldPath} to {NewPath}",
                FileSystem.AppDataDirectory, customPath);

                    // Migrate data to the custom path
                    await fileSystem.MigrateToNewRoot(customPath);
                    logger.LogInformation("Migration completed successfully");

                }
                else
                {
                    logger.LogInformation("Using default application path");

                }
            }
            catch (SecurityException ex)
            {
                // Security exceptions are important to log with high severity
                logger.LogError(ex, "Security violation during path initialization: {Message}", ex.Message);

                // Consider displaying to the user
                await Shell.Current.DisplayAlert("Security Error",
                    "Cannot access the configured storage location due to security restrictions. Using default location instead.", "OK");
            }
            catch (UnauthorizedAccessException ex)
            {
                // Permission issues
                logger.LogError(ex, "Permission denied accessing custom path: {Message}", ex.Message);
            }
            catch (IOException ex)
            {
                // File system errors
                logger.LogError(ex, "I/O error during path initialization: {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                // Fallback for other errors
                logger.LogError(ex, "Failed to initialize with custom path: {Message}", ex.Message);

            }
        }

#endif
        return builder.Build();
    }
    
}
