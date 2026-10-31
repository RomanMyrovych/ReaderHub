using System.Windows;
using BookHub.App.Services;
using BookHub.App.ViewModels;
using BookHub.App.Views;
using BookHub.Core.Data;
using BookHub.Core.Models;
using BookHub.Core.Parsers;
using BookHub.Core.Pdf;
using BookHub.Core.Services;

namespace BookHub.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--initialize-database", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var installerDatabase = new LibraryDatabase();
                await installerDatabase.InitializeAsync();
                if ((await installerDatabase.GetAllBooksAsync()).Count != 0 ||
                    (await installerDatabase.GetRootFoldersAsync()).Count != 0)
                {
                    throw new InvalidOperationException("The installer database must not contain books or library folders.");
                }

                Shutdown(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Could not initialize the installer database:\n{ex.Message}",
                    "ReaderHub installer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }
            return;
        }

        if (e.Args.Contains("--verify", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // Verification harness runs against the source repository root (never a hard-coded drive path).
                string verifyRoot = LibraryDatabase.ResolveProjectRoot();
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(verifyRoot, ".temp"));

                var settings = new UserSettingsService(verifyRoot);
                settings.Current.IsFirstRun = true;
                settings.Current.ThemeName = "Obsidian Dark";
                settings.Current.ReaderZoomMode = "FitWidth";
                settings.Current.ReaderTocWidth = 285;
                settings.Current.LanguageCode = "uk";
                settings.Save();

                foreach (var preset in ThemeManagerService.Presets)
                {
                    ThemeManagerService.ApplyTheme(preset.Name);
                }

                foreach (var lang in LocalizationService.SupportedLanguages)
                {
                    LocalizationService.ApplyLanguage(lang.Code);
                }
                LocalizationService.ApplyLanguage("uk");

                bool hasLicense = System.IO.File.Exists(System.IO.Path.Combine(verifyRoot, "LICENSE")) &&
                                  System.IO.File.Exists(System.IO.Path.Combine(verifyRoot, "THIRD_PARTY_LICENSES.txt"));

                var db = new LibraryDatabase(verifyRoot);
                await db.InitializeAsync();

                string demoDir = System.IO.Path.Combine(verifyRoot, "SampleLibrary");
                SampleLibraryGenerator.EnsureDemoBooksExist(demoDir);

                // Verify First-Run Welcome Wizard ViewModel (Steps 1 -> 5, Language, Theme, Folders, Storage, Finish)
                var wizardVm = new WelcomeWizardViewModel(db, settings);
                await wizardVm.LoadInitialFoldersAsync();
                wizardVm.SelectLanguageCommand.Execute("en");
                wizardVm.SelectLanguageCommand.Execute("uk");
                wizardVm.NextStepCommand.Execute(null); // Step 2
                wizardVm.SelectThemeCommand.Execute("OLED Black");
                wizardVm.SelectThemeCommand.Execute("Obsidian Dark");
                wizardVm.NextStepCommand.Execute(null); // Step 3
                wizardVm.AddFolderPathIfValid(demoDir);
                wizardVm.NextStepCommand.Execute(null); // Step 4
                wizardVm.SelectStorageModeCommand.Execute("Portable");
                wizardVm.NextStepCommand.Execute(null); // Step 5
                bool wizardReachedStep5 = wizardVm.IsStep5;
                await wizardVm.FinishWizardCommand.ExecuteAsync(null);
                bool firstRunClearedAfterWizard = !settings.Current.IsFirstRun;

                using var scanner = new FileScannerService(db)
                {
                    ScanEbooksEnabled = settings.Current.ScanEbooksEnabled,
                    ScanDocumentsEnabled = settings.Current.ScanDocumentsEnabled,
                    EnableBackgroundFileWatcher = settings.Current.EnableBackgroundFileWatcher
                };
                await scanner.ScanAllFoldersAsync();

                var books = await db.GetAllBooksAsync();
                var heavyPdf = books.FirstOrDefault(b => b.FilePath.Contains("Pro_NET_9_High_Performance_Architecture_1250p.pdf", StringComparison.OrdinalIgnoreCase))
                               ?? books.First(b => b.PageCount >= 1200 && b.Extension == ".pdf");

                // Verify FTS5 full-text index & query
                string pdfFtsSample = BookFormatParser.ExtractTextForFtsIndex(heavyPdf.FilePath);
                await db.IndexBookFtsContentAsync(heavyPdf.Id, heavyPdf.Title, heavyPdf.Author, heavyPdf.Tags, pdfFtsSample);
                var ftsHits = await db.SearchFtsBookIdsAsync("PDFium AND Page*");

                var pdfOpenSw = System.Diagnostics.Stopwatch.StartNew();
                using var session = new PdfDocumentSession(heavyPdf.FilePath);
                var toc = session.GetTableOfContents();
                var page1 = session.RenderPageBgra(1, 1.25);
                var page600 = session.RenderPageBgra(600, 1.25);
                string extractedPage1Text = session.ExtractPageText(1);
                pdfOpenSw.Stop();

                // Verify Annotations & Obsidian Markdown Export
                long annId = await db.AddAnnotationAsync(new AnnotationRecord
                {
                    BookId = heavyPdf.Id,
                    PageNumber = 42,
                    QuoteText = "Only visible page and +1/-1 neighbor pages are rendered in background.",
                    CommentText = "Ключовий принцип потокового рендерингу PDFium",
                    HighlightColor = "#FACC15",
                    IsBookmark = false
                });
                var anns = await db.GetAnnotationsForBookAsync(heavyPdf.Id);
                string mdExportPath = System.IO.Path.Combine(verifyRoot, ".temp", "test_obsidian_export.md");
                ExportAndBackupService.ExportAnnotationsToMarkdown(heavyPdf, anns, mdExportPath);

                // Verify One-Click Backup .zip
                string backupZip = await ExportAndBackupService.CreateOneClickBackupZipAsync(db);

                var fb2Book = books.First(b => b.Extension == ".fb2");
                var fb2Sw = System.Diagnostics.Stopwatch.StartNew();
                var (fb2Pages, fb2Toc) = BookFormatParser.LoadFb2PaginatedContent(fb2Book.FilePath);
                fb2Sw.Stop();

                string verifyMsg = $"[VERIFY_OK] TotalBooks={books.Count}; WizardStep5={wizardReachedStep5}; FirstRunCleared={firstRunClearedAfterWizard}; ThemesTested={ThemeManagerService.Presets.Count}; LanguagesTested={LocalizationService.SupportedLanguages.Count}; DefaultZoom={settings.Current.ReaderZoomMode}; LicenseFiles={hasLicense}; FtsHits={ftsHits.Count}; HeavyPdfPages={session.PageCount}; TocCount={toc.Count}; PdfOpenAndRenderMs={pdfOpenSw.ElapsedMilliseconds}; ExtractedChars={extractedPage1Text.Length}; Fb2Pages={fb2Pages.Count}; Fb2ParseMs={fb2Sw.ElapsedMilliseconds}; BackupZipExists={System.IO.File.Exists(backupZip)}";
                System.IO.File.WriteAllText(System.IO.Path.Combine(verifyRoot, ".temp", "verify_result.txt"), verifyMsg);
                Console.WriteLine(verifyMsg);
                Shutdown(0);
                return;
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(LibraryDatabase.ResolveProjectRoot(), ".temp", "verify_result.txt"), $"[VERIFY_ERROR] {ex}");
                Console.Error.WriteLine($"[VERIFY_ERROR] {ex}");
                Shutdown(1);
                return;
            }
        }

        // Normal Interactive Startup: Check First-Run Wizard state before launching MainWindow
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var settingsService = new UserSettingsService();
        ThemeManagerService.ApplyTheme(settingsService.Current.ThemeName);
        LocalizationService.ApplyLanguage(settingsService.Current.LanguageCode);

        var database = new LibraryDatabase();
        await database.InitializeAsync();
        var existingBooks = await database.GetAllBooksAsync();

        bool shouldShowFirstRunWizard = settingsService.Current.IsFirstRun ||
                                        existingBooks.Count == 0 ||
                                        e.Args.Contains("--wizard", StringComparer.OrdinalIgnoreCase);

        if (shouldShowFirstRunWizard)
        {
            var wizardVm = new WelcomeWizardViewModel(database, settingsService);
            var wizardWindow = new WelcomeWizardWindow(wizardVm);
            wizardWindow.ShowDialog();

            if (settingsService.Current.IsFirstRun)
            {
                settingsService.Current.IsFirstRun = false;
                settingsService.Save();
            }
        }

        var mainWin = new MainWindow();
        MainWindow = mainWin;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWin.Show();
    }
}
