using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SkiaSharp;

namespace asset_tool
{
    public partial class MainWindow : Window
    {
        private SKBitmap? _sourceBitmap;
        private string? _sourceFileName;
        private List<AtlasPage>? _atlasPages;
        public ObservableCollection<PartItem> Parts { get; } = new();

        public MainWindow()
        {
            InitializeComponent();
            PartsList.ItemsSource = Parts;
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
        }

        private async void OnOpenClick(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "PNG 열기",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("PNG 이미지") { Patterns = new[] { "*.png" } }
                }
            });

            if (files.Count == 0) return;

            var file = files[0];

            await using var stream = await file.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var bytes = ms.ToArray();

            _sourceBitmap?.Dispose();
            _sourceBitmap = SKBitmap.Decode(bytes);
            _sourceFileName = file.Name;

            PreviewImage.Source = new Bitmap(new MemoryStream(bytes));
            FilePathText.Text = file.Name;

            Parts.Clear();
            SplitButton.IsEnabled = _sourceBitmap != null;
            AtlasSplitButton.IsEnabled = _sourceBitmap != null && _atlasPages != null;
            ExportButton.IsEnabled = false;
            StatusText.Text = string.Empty;
            UpdateSelectionCount();
        }

        private async void OnAtlasOpenClick(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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

            _atlasPages = AtlasParser.Parse(localPath);
            AtlasPathText.Text = file.Name;

            int regionCount = _atlasPages.Sum(p => p.Regions.Count);
            var pageNames = string.Join(", ", _atlasPages.Select(p => $"{Path.GetFileName(p.ImageFile)}({p.Regions.Count})"));
            var multiPageNote = _atlasPages.Count > 1
                ? $" - 페이지별 리전 수: {pageNames}. 페이지가 여러 개면 각 페이지의 PNG를 따로 열어서 분리해야 합니다."
                : "";
            StatusText.Text = $"아틀라스 로드됨: 페이지 {_atlasPages.Count}개, 리전 {regionCount}개{multiPageNote}";

            AtlasSplitButton.IsEnabled = _sourceBitmap != null && _atlasPages.Count > 0;
        }

        private void OnSplitClick(object? sender, RoutedEventArgs e)
        {
            if (_sourceBitmap is null) return;

            Parts.Clear();
            var detected = PartSplitter.Split(_sourceBitmap);

            int i = 1;
            foreach (var part in detected)
            {
                AddPart($"part_{i:D2}", part.X, part.Y, part.Image);
                i++;
            }

            StatusText.Text = $"{Parts.Count}개 파츠 감지됨";
            ExportButton.IsEnabled = Parts.Count > 0;
            UpdateSelectionCount();
        }

        private void OnAtlasSplitClick(object? sender, RoutedEventArgs e)
        {
            if (_sourceBitmap is null || _atlasPages is null) return;

            // Prefer the page whose declared image file name matches the loaded PNG;
            // fall back to the only page (or the first one) otherwise.
            var page = _atlasPages.FirstOrDefault(p =>
                           string.Equals(Path.GetFileName(p.ImageFile), _sourceFileName, StringComparison.OrdinalIgnoreCase))
                       ?? _atlasPages.FirstOrDefault();

            if (page is null || page.Regions.Count == 0)
            {
                StatusText.Text = "아틀라스에서 사용할 리전을 찾지 못했습니다.";
                return;
            }

            bool pageMatched = string.Equals(Path.GetFileName(page.ImageFile), _sourceFileName, StringComparison.OrdinalIgnoreCase);

            Parts.Clear();
            var skippedNames = new List<string>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicateNames = new List<string>();

            foreach (var region in page.Regions)
            {
                int rawWidth = region.SwapsDimensions ? region.Height : region.Width;
                int rawHeight = region.SwapsDimensions ? region.Width : region.Height;

                if (region.X < 0 || region.Y < 0 ||
                    region.X + rawWidth > _sourceBitmap.Width ||
                    region.Y + rawHeight > _sourceBitmap.Height ||
                    rawWidth <= 0 || rawHeight <= 0)
                {
                    skippedNames.Add(region.ExportName);
                    continue;
                }

                using var cropped = new SKBitmap(rawWidth, rawHeight, _sourceBitmap.ColorType, _sourceBitmap.AlphaType);
                _sourceBitmap.ExtractSubset(cropped, new SKRectI(region.X, region.Y, region.X + rawWidth, region.Y + rawHeight));

                // Texture packers rotate regions when packing to save space;
                // rotate back by the same declared amount to restore orientation
                // (confirmed empirically: "rotate: true"/90 needs +90 here, not -90).
                var final = region.RotateDegrees != 0 ? RotateBitmap(cropped, region.RotateDegrees) : cropped.Copy();

                // Atlas rectangles are axis-aligned bounding boxes, so an irregular
                // shape (hair, etc.) can leak a bit of a neighboring part/packing
                // background at its corners; clean that up unless the user disabled it.
                if (CutoutCheckBox.IsChecked == true && int.TryParse(CutoutToleranceBox.Text, out var tolerance))
                {
                    BackgroundCutout.RemoveBorderBackground(final, tolerance);

                    // A leak fully enclosed inside the part (e.g. an eye poking
                    // through a hair crop) never touches the border, so the pass
                    // above can't reach it - fall back to dropping color-connected
                    // blobs that are much smaller than the main shape (comparable-
                    // sized blobs, like both eyes in one "eyes" part, are kept).
                    if (IslandCheckBox.IsChecked == true)
                        BackgroundCutout.KeepDominantIslands(final, tolerance);
                }

                var name = region.ExportName;
                if (!usedNames.Add(name))
                    duplicateNames.Add(name);

                AddPart(name, region.X, region.Y, final);
            }

            var pageList = string.Join(", ", _atlasPages!.Select(p => Path.GetFileName(p.ImageFile)));
            var matchNote = pageMatched ? "" : $" (파일명이 일치하는 페이지를 찾지 못해 첫 페이지 사용 - 아틀라스 페이지 목록: {pageList})";
            var skipNote = skippedNames.Count > 0 ? $", 범위를 벗어나 제외됨: {string.Join(", ", skippedNames)}" : "";
            var dupNote = duplicateNames.Count > 0 ? $", 이름 중복(내보내기 시 자동으로 구분됨): {string.Join(", ", duplicateNames.Distinct())}" : "";
            StatusText.Text = $"아틀라스 기준 {Parts.Count}개 파츠 생성{skipNote}{dupNote}{matchNote}";
            ExportButton.IsEnabled = Parts.Count > 0;
            UpdateSelectionCount();
        }

        private static SKBitmap RotateBitmap(SKBitmap source, float degrees)
        {
            var rotated = new SKBitmap(source.Height, source.Width, source.ColorType, source.AlphaType);
            using var canvas = new SKCanvas(rotated);
            canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
            canvas.RotateDegrees(degrees);
            canvas.Translate(-source.Width / 2f, -source.Height / 2f);
            canvas.DrawBitmap(source, 0, 0);
            return rotated;
        }

        private void AddPart(string name, int x, int y, SKBitmap image)
        {
            using var skImage = SKImage.FromBitmap(image);
            using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);

            var item = new PartItem
            {
                Name = name,
                Thumbnail = new Bitmap(new MemoryStream(data.ToArray())),
                X = x,
                Y = y,
                Width = image.Width,
                Height = image.Height,
                Raw = image
            };
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(PartItem.Include))
                    UpdateSelectionCount();
            };
            Parts.Add(item);
        }

        private async void OnExportClick(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "내보낼 폴더 선택",
                AllowMultiple = false
            });

            if (folders.Count == 0) return;

            var folderPath = folders[0].Path.LocalPath;
            var invalidChars = Path.GetInvalidFileNameChars();
            var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int exported = 0;

            foreach (var part in Parts.Where(p => p.Include))
            {
                var name = string.IsNullOrWhiteSpace(part.Name) ? $"part_{exported + 1:D2}" : part.Name;
                var safeName = string.Join("_", name.Split(invalidChars));

                // Never let two parts silently overwrite each other on disk just
                // because they ended up with the same name.
                var finalName = safeName;
                int suffix = 2;
                while (!usedFileNames.Add(finalName))
                    finalName = $"{safeName}_{suffix++}";

                var outPath = Path.Combine(folderPath, finalName + ".png");

                using var image = SKImage.FromBitmap(part.Raw);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                await using var fs = File.Create(outPath);
                data.SaveTo(fs);
                exported++;
            }

            StatusText.Text = $"{exported}개 파일을 저장했습니다: {folderPath}";
        }
    }
}
