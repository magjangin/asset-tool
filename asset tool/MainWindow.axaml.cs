using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace asset_tool
{
    public partial class MainWindow : Window
    {
        private const int ThumbnailSize = 96;

        private RgbaImage? _sourceImage;
        private string? _sourceFileName;
        private List<AtlasPage>? _atlasPages;
        private CancellationTokenSource? _batchCts;
        private bool _busy;
        private readonly StringBuilder _log = new();

        public ObservableCollection<PartItem> Parts { get; } = new();

        public MainWindow()
        {
            InitializeComponent();
            PartsList.ItemsSource = Parts;
            UpdateSelectionCount();
            Closing += (_, _) => _batchCts?.Cancel();
        }

        // ---------------------------------------------------------------- state

        private void SetBusy(bool busy, bool batch = false)
        {
            _busy = busy;
            OpenButton.IsEnabled = !busy;
            AtlasOpenButton.IsEnabled = !busy;
            BatchButton.IsEnabled = !busy;
            CancelBatchButton.IsEnabled = busy && batch;
            UpdateActionButtons();
        }

        private void UpdateActionButtons()
        {
            SplitButton.IsEnabled = !_busy && _sourceImage != null;
            AtlasSplitButton.IsEnabled = !_busy && _sourceImage != null && _atlasPages is { Count: > 0 };
            UpdateSelectionCount();
        }

        private void OnSelectAllClick(object? sender, RoutedEventArgs e) => SetAllIncluded(true);

        private void OnDeselectAllClick(object? sender, RoutedEventArgs e) => SetAllIncluded(false);

        private void SetAllIncluded(bool included)
        {
            foreach (var part in Parts)
                part.Include = included;
            UpdateSelectionCount();
        }

        private void UpdateSelectionCount()
        {
            int selected = Parts.Count(p => p.Include);
            SelectionCountText.Text = Parts.Count == 0 ? "" : $"{selected} / {Parts.Count} 선택됨";
            ExportButton.IsEnabled = !_busy && selected > 0;
        }

        private void OnPartPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(PartItem.Include))
                UpdateSelectionCount();
        }

        private void ShowError(string what, Exception ex) => StatusText.Text = $"{what}: {ex.Message}";

        private ExtractOptions BuildExtractOptions() => new()
        {
            RemoveOverlapLeaks = OverlapCheckBox.IsChecked == true,
            ColorCutout = CutoutCheckBox.IsChecked == true,
            RemoveSmallIslands = IslandCheckBox.IsChecked == true,
            Tolerance = (int)(CutoutToleranceBox.Value ?? 100),
            RestoreOriginalSize = RestoreOrigCheckBox.IsChecked == true
        };

        private NamingMode SelectedNamingMode => NamingComboBox.SelectedIndex switch
        {
            1 => NamingMode.Flatten,
            2 => NamingMode.LeafOnly,
            _ => NamingMode.KeepFolders
        };

        // ---------------------------------------------------------------- parts list

        // Built off the UI thread: the PNG is encoded once and reused for export.
        private sealed record PreparedPart(string Name, int X, int Y, int Width, int Height,
                                           byte[] Png, Bitmap Thumbnail, string Note);

        private static PreparedPart Prepare(string name, int x, int y, RgbaImage image, string note)
        {
            var png = image.EncodePng();
            return new PreparedPart(name, x, y, image.Width, image.Height, png,
                                    CreateThumbnail(png, image.Width, image.Height), note);
        }

        private static Bitmap CreateThumbnail(byte[] png, int width, int height)
        {
            using var stream = new MemoryStream(png);
            if (width <= ThumbnailSize && height <= ThumbnailSize)
                return new Bitmap(stream);
            return width >= height
                ? Bitmap.DecodeToWidth(stream, ThumbnailSize)
                : Bitmap.DecodeToHeight(stream, ThumbnailSize);
        }

        private void ReplaceParts(IEnumerable<PreparedPart> prepared)
        {
            ClearParts();
            foreach (var p in prepared)
            {
                var item = new PartItem
                {
                    Name = p.Name,
                    Thumbnail = p.Thumbnail,
                    X = p.X,
                    Y = p.Y,
                    Width = p.Width,
                    Height = p.Height,
                    PngData = p.Png,
                    Note = p.Note
                };
                item.PropertyChanged += OnPartPropertyChanged;
                Parts.Add(item);
            }
            UpdateSelectionCount();
        }

        private void ClearParts()
        {
            var old = Parts.ToList();
            Parts.Clear();
            foreach (var part in old)
            {
                part.PropertyChanged -= OnPartPropertyChanged;
                part.Dispose();
            }
            UpdateSelectionCount();
        }

        // ---------------------------------------------------------------- single file

        private async void OnOpenClick(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "PNG 열기",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("PNG 이미지") { Patterns = new[] { "*.png" } }
                }
            });

            if (files.Count == 0) return;

            try
            {
                byte[] bytes;
                await using (var stream = await files[0].OpenReadAsync())
                using (var ms = new MemoryStream())
                {
                    await stream.CopyToAsync(ms);
                    bytes = ms.ToArray();
                }

                await LoadSourceAsync(bytes, files[0].Name);
                StatusText.Text = string.Empty;
            }
            catch (Exception ex)
            {
                ShowError("PNG를 열지 못했습니다", ex);
            }
        }

        private async Task LoadSourceAsync(byte[] bytes, string fileName)
        {
            SetBusy(true);
            try
            {
                var image = await Task.Run(() => RgbaImage.Decode(bytes));

                var oldPreview = PreviewImage.Source as IDisposable;
                PreviewImage.Source = new Bitmap(new MemoryStream(bytes));
                oldPreview?.Dispose();

                _sourceImage = image;
                _sourceFileName = fileName;
                FilePathText.Text = fileName;
                ClearParts();
                LeftTabs.SelectedIndex = 0;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private static AtlasPage? FindPage(List<AtlasPage> pages, string? pngFileName) =>
            pngFileName is null
                ? null
                : pages.FirstOrDefault(p => string.Equals(Path.GetFileName(p.ImageFile), pngFileName, StringComparison.OrdinalIgnoreCase));

        private async void OnAtlasOpenClick(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "아틀라스 열기",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Spine Atlas") { Patterns = new[] { "*.atlas", "*.atlas.txt" } }
                }
            });

            if (files.Count == 0) return;

            var file = files[0];
            var localPath = file.TryGetLocalPath();
            if (localPath is null)
            {
                StatusText.Text = "아틀라스 파일 경로를 읽을 수 없습니다.";
                return;
            }

            try
            {
                var pages = AtlasParser.Parse(localPath);
                _atlasPages = pages;
                AtlasPathText.Text = file.Name;

                int regionCount = pages.Sum(p => p.Regions.Count);
                var message = new StringBuilder($"아틀라스 로드됨: 페이지 {pages.Count}개, 리전 {regionCount}개");
                if (pages.Count > 1)
                {
                    var pageNames = string.Join(", ", pages.Select(p => $"{Path.GetFileName(p.ImageFile)}({p.Regions.Count})"));
                    message.Append($" - 페이지별 리전 수: {pageNames}. 여기서는 열린 PNG에 맞는 페이지만 분리하고, '폴더 일괄 처리'는 모든 페이지를 한 번에 처리합니다.");
                }

                // Asset rippers put the texture in a sibling folder (TextAsset\ vs
                // Texture2D\), so find and open the page PNG automatically unless
                // the PNG already open belongs to this atlas.
                if (pages.Count > 0 && FindPage(pages, _sourceFileName) is null)
                {
                    var pngPath = PagePngLocator.FindNear(localPath, pages[0].ImageFile);
                    if (pngPath != null)
                    {
                        await LoadSourceAsync(await File.ReadAllBytesAsync(pngPath), Path.GetFileName(pngPath));
                        message.Append($" · 페이지 PNG를 자동으로 열었습니다: {pngPath}");
                    }
                    else
                    {
                        message.Append($" · 페이지 PNG({pages[0].ImageFile})를 근처 폴더에서 찾지 못했습니다. 'PNG 열기'로 직접 열어 주세요.");
                    }
                }

                StatusText.Text = message.ToString();
            }
            catch (Exception ex)
            {
                ShowError("아틀라스를 읽지 못했습니다", ex);
            }

            UpdateActionButtons();
        }

        private async void OnSplitClick(object? sender, RoutedEventArgs e)
        {
            if (_sourceImage is null) return;

            var source = _sourceImage;
            SetBusy(true);
            try
            {
                var prepared = await Task.Run(() =>
                    PartSplitter.Split(source)
                                .Select((p, i) => Prepare($"part_{i + 1:D2}", p.X, p.Y, p.Image, ""))
                                .ToList());

                ReplaceParts(prepared);
                StatusText.Text = $"{Parts.Count}개 파츠 감지됨";
            }
            catch (Exception ex)
            {
                ShowError("자동 감지 중 오류", ex);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void OnAtlasSplitClick(object? sender, RoutedEventArgs e)
        {
            if (_sourceImage is null || _atlasPages is null) return;

            // Prefer the page whose declared image file name matches the loaded PNG;
            // fall back to the first one otherwise.
            var matchedPage = FindPage(_atlasPages, _sourceFileName);
            var page = matchedPage ?? _atlasPages.FirstOrDefault();
            if (page is null || page.Regions.Count == 0)
            {
                StatusText.Text = "아틀라스에서 사용할 리전을 찾지 못했습니다.";
                return;
            }

            var source = _sourceImage;
            var options = BuildExtractOptions();
            SetBusy(true);
            try
            {
                var (extraction, prepared) = await Task.Run(() =>
                {
                    var result = AtlasExtractor.ExtractPage(source, page, options);
                    var items = result.Parts
                                      .Select(p => Prepare(p.Region.ExportName, p.Region.X, p.Region.Y, p.Image, DescribeCleanup(p)))
                                      .ToList();
                    return (result, items);
                });

                ReplaceParts(prepared);
                StatusText.Text = DescribeExtraction(extraction, options, page, source, matchedPage != null);
            }
            catch (Exception ex)
            {
                ShowError("아틀라스 분리 중 오류", ex);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private static string DescribeCleanup(ExtractedPart part)
        {
            var notes = new List<string>();
            if (part.OverlapPixelsRemoved > 0) notes.Add($"겹친 옆 파츠 {part.OverlapPixelsRemoved}px 제거");
            if (part.ColorPixelsRemoved > 0) notes.Add($"색 누끼 {part.ColorPixelsRemoved}px 제거");
            return string.Join(", ", notes);
        }

        private string DescribeExtraction(PageExtraction extraction, ExtractOptions options, AtlasPage page,
                                          RgbaImage source, bool pageMatched)
        {
            var sb = new StringBuilder($"아틀라스 기준 {extraction.Parts.Count}개 파츠 생성");

            if (options.RemoveOverlapLeaks)
            {
                int cleaned = extraction.Parts.Count(p => p.OverlapPixelsRemoved > 0);
                if (!extraction.PageHasOverlaps)
                    sb.Append(" · 겹치는 리전 없음 (원본 그대로 잘라냄)");
                else if (cleaned > 0)
                    sb.Append($" · 리전 사각형이 겹치는 아틀라스: {cleaned}개 리전에서 끼어든 옆 파츠 조각 제거");
                else
                    sb.Append(" · 리전 사각형이 겹치지만 끼어든 조각은 없음");
            }

            if (extraction.SkippedRegions.Count > 0)
                sb.Append($" · 범위를 벗어나 제외됨: {string.Join(", ", extraction.SkippedRegions)}");

            var duplicates = extraction.Parts
                                       .GroupBy(p => p.Region.ExportName, StringComparer.OrdinalIgnoreCase)
                                       .Where(g => g.Count() > 1)
                                       .Select(g => g.Key)
                                       .ToList();
            if (duplicates.Count > 0)
                sb.Append($" · 이름 중복(내보내기 시 자동으로 구분됨): {string.Join(", ", duplicates)}");

            if (page.Width > 0 && page.Height > 0 && (page.Width != source.Width || page.Height != source.Height))
                sb.Append($" · 경고: 아틀라스의 페이지 크기({page.Width}x{page.Height})와 PNG 크기({source.Width}x{source.Height})가 다릅니다");

            if (!pageMatched && _atlasPages != null)
            {
                var pageList = string.Join(", ", _atlasPages.Select(p => Path.GetFileName(p.ImageFile)));
                sb.Append($" · 파일명이 일치하는 페이지를 찾지 못해 첫 페이지 사용 (아틀라스 페이지 목록: {pageList})");
            }

            return sb.ToString();
        }

        private async void OnExportClick(object? sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "내보낼 폴더 선택",
                AllowMultiple = false
            });

            if (folders.Count == 0) return;

            var folderPath = folders[0].TryGetLocalPath();
            if (folderPath is null)
            {
                StatusText.Text = "선택한 폴더의 경로를 읽을 수 없습니다.";
                return;
            }

            var namer = new ExportNamer(SelectedNamingMode);
            var jobs = Parts.Where(p => p.Include)
                            .Select((p, i) => (Path: Path.Combine(folderPath, namer.Next(p.Name, $"part_{i + 1:D2}") + ".png"), Data: p.PngData))
                            .ToList();

            SetBusy(true);
            try
            {
                int overwritten = await Task.Run(() =>
                {
                    int count = 0;
                    foreach (var (path, data) in jobs)
                    {
                        if (File.Exists(path)) count++;
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllBytes(path, data);
                    }
                    return count;
                });

                var overwriteNote = overwritten > 0 ? $" (기존 파일 {overwritten}개를 덮어씀)" : "";
                StatusText.Text = $"{jobs.Count}개 파일을 저장했습니다: {folderPath}{overwriteNote}";
            }
            catch (Exception ex)
            {
                ShowError("내보내기 중 오류", ex);
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ---------------------------------------------------------------- batch

        private async void OnBatchClick(object? sender, RoutedEventArgs e)
        {
            var inputs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "일괄 처리할 폴더 선택 (.atlas와 PNG가 들어 있는 상위 폴더)",
                AllowMultiple = false
            });
            if (inputs.Count == 0) return;

            var inputPath = inputs[0].TryGetLocalPath();
            if (inputPath is null)
            {
                StatusText.Text = "입력 폴더의 경로를 읽을 수 없습니다.";
                return;
            }

            var outputs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "결과를 저장할 폴더 선택 (입력 폴더와 다른 곳)",
                AllowMultiple = false,
                SuggestedStartLocation = inputs[0]
            });
            if (outputs.Count == 0) return;

            var outputPath = outputs[0].TryGetLocalPath();
            if (outputPath is null)
            {
                StatusText.Text = "출력 폴더의 경로를 읽을 수 없습니다.";
                return;
            }

            var options = new BatchOptions
            {
                Extract = BuildExtractOptions(),
                Naming = SelectedNamingMode,
                CopyUnusedPngs = CopyUnusedCheckBox.IsChecked == true
            };

            _log.Clear();
            LogBox.Text = string.Empty;
            LeftTabs.SelectedItem = LogTab;
            BatchProgressBar.Value = 0;
            BatchProgressText.Text = string.Empty;
            StatusText.Text = "일괄 처리 중...";

            var cts = new CancellationTokenSource();
            _batchCts = cts;
            var progress = new Progress<BatchProgress>(p =>
            {
                if (p.Total > 0)
                {
                    BatchProgressBar.Maximum = p.Total;
                    BatchProgressBar.Value = p.Done;
                    BatchProgressText.Text = $"{p.Done} / {p.Total}";
                }
                if (p.LogLine != null)
                    AppendLog(p.LogLine);
            });

            SetBusy(true, batch: true);
            try
            {
                var summary = await Task.Run(() => BatchProcessor.Run(inputPath, outputPath, options, progress, cts.Token), cts.Token);
                StatusText.Text = $"일괄 처리 완료: 아틀라스 {summary.Atlases}개 → 파츠 {summary.Parts}개 저장 " +
                                  $"(겹침 정리 {summary.CleanedRegions}개 리전, 복사한 PNG {summary.CopiedPngs}개, " +
                                  $"경고 {summary.Warnings}개, 실패 {summary.Failures}개). 로그 파일: {summary.LogPath}";
            }
            catch (OperationCanceledException)
            {
                AppendLog("— 취소됨 —");
                StatusText.Text = "일괄 처리를 취소했습니다. 이미 저장된 파일은 그대로 남아 있습니다.";
            }
            catch (Exception ex)
            {
                ShowError("일괄 처리 중 오류", ex);
            }
            finally
            {
                _batchCts = null;
                cts.Dispose();
                SetBusy(false);
            }
        }

        private void OnCancelBatchClick(object? sender, RoutedEventArgs e) => _batchCts?.Cancel();

        private void AppendLog(string line)
        {
            _log.AppendLine(line);
            LogBox.Text = _log.ToString();
            LogBox.CaretIndex = LogBox.Text.Length;
        }
    }
}
