using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace asset_tool
{
    public class PartItem : INotifyPropertyChanged
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

        public Bitmap Thumbnail { get; set; } = null!;
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public SKBitmap Raw { get; set; } = null!;
        public string SizeText => $"{Width} x {Height} px  (offset {X}, {Y})";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
