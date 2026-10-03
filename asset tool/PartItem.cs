using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace asset_tool
{
    public class PartItem : INotifyPropertyChanged, IDisposable
    {
        private bool _include = true;

        public string Name { get; set; } = string.Empty;

        public bool Include
        {
            get => _include;
            set
            {
                if (_include == value) return;
                _include = value;
                OnPropertyChanged();
            }
        }

        public Bitmap Thumbnail { get; init; } = null!;
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

        // Already-encoded PNG, written out as-is on export (no re-encode).
        public byte[] PngData { get; init; } = Array.Empty<byte>();

        // Extra info shown under the size, e.g. how many leaked pixels were cleaned.
        public string Note { get; init; } = string.Empty;

        public string SizeText => Note.Length == 0
            ? $"{Width} x {Height} px  (원본 위치 {X}, {Y})"
            : $"{Width} x {Height} px  (원본 위치 {X}, {Y})  · {Note}";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void Dispose() => Thumbnail?.Dispose();
    }
}
