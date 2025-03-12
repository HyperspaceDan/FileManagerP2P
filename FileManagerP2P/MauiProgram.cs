using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using Syncfusion.Maui.Toolkit.Hosting;

using FileManager.Core.Interfaces;                    // For IFileSystem
using FileManager.Core.Models;                        // For QuotaConfiguration
using FileManagerP2P;              // For WindowsFileSystem
using System.IO;
using Microsoft.Maui.Storage;                        // For FileSystem
using FileManagerP2P.Services;
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
            var task = WindowsFileSystem.CreateWithSecurePathAsync(logger, pathProvider);

            // This is not ideal but necessary for DI integration
            // In production, consider alternatives like IOptions pattern
            return task.GetAwaiter().GetResult();
        });
#endif
        return builder.Build();
    }
}
