using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using WpfPixelFormats = System.Windows.Media.PixelFormats;
using GrotheImages;
using Microsoft.Win32;

namespace GrotheImagesPlayground;

public partial class MainWindow : Window
{
    private GrotheImage _image;
    private LayerCompose _compose;
    private string _composeExpression;

    public MainWindow()
    {
        InitializeComponent();
        var formats = Enum.GetValues(typeof(PixelFormat)).Cast<PixelFormat>().ToArray();
        NewFormat.ItemsSource = formats;
        LoadFormat.ItemsSource = formats.Where(IsTransferFormat).ToArray();
        NewFormat.SelectedItem = PixelFormat.Rgba32;
        LoadFormat.SelectedItem = PixelFormat.Bgra32;
        LoadLayer.TextChanged += (_, __) => TryRefresh(RefreshLoadSelector);
        LoadMode.SelectionChanged += (_, __) => TryRefresh(RefreshLoadSelector);
        ComposeExpression.TextChanged += (_, __) => { ResetCompose(); TryRefresh(RefreshLoadSelector); };
        UpdateFile.TextChanged += (_, __) => TryRefresh(RefreshUpdateFileInfo);
        TileFile.TextChanged += (_, __) => TryRefresh(RefreshTileFileInfo);
        RefreshImageUi();
    }

    private void CreateLogicalClick(object sender, RoutedEventArgs e)
    {
        Run(() =>
        {
            var info = new GrotheImageInfo(Long(NewWidth), Long(NewHeight), Int(NewMaxTileWidth), Int(NewMaxTileHeight), Int(NewOverlapX), Int(NewOverlapY));
            CreateImage(info);
        });
    }

    private void CreateExplicitClick(object sender, RoutedEventArgs e)
    {
        Run(() => CreateImage(GrotheImageInfo.FromTiles(Int(TileWidth), Int(TileHeight), Long(TileRows), Long(TileColumns), Int(TileOverlapX), Int(TileOverlapY))));
    }

    private void CreateImage(GrotheImageInfo info)
    {
        ResetCompose();
        _image?.Dispose();
        PixelFormat format = (PixelFormat)NewFormat.SelectedItem;
        string[] names = NewLayers.Text.Split(',').Select(x => x.Trim()).Where(x => x.Length != 0).ToArray();
        _image = new GrotheImage(info, format, names);
        UpdatePerspective.Reset(info.Width, info.Height);
        LoadPerspective.Reset(info.Width, info.Height);
        RefreshImageUi();
        RefreshLoadSelector();
        StatusText.Text = "Created " + info.Width + " x " + info.Height + ", format " + format + ", layers: " + string.Join(", ", names);
    }

    private void BrowseUpdateClick(object sender, RoutedEventArgs e) => Run(() => { ChooseOpen(UpdateFile); RefreshUpdateFileInfo(); });
    private void BrowseTileClick(object sender, RoutedEventArgs e) => Run(() => { ChooseOpen(TileFile); RefreshTileFileInfo(); });
    private void BrowseExportClick(object sender, RoutedEventArgs e) => ChooseImageSave(ExportFile);

    private void UpdateClick(object sender, RoutedEventArgs e)
    {
        Run(() =>
        {
            RequireImage();
            byte[] bitmap = ReadBitmap(UpdateFile.Text, out int width, out int height, out int bitmapStride, out PixelFormat sourceFormat);
            TransformMatrix matrix = UpdatePerspective.CreateMatrix(width, height);
            WithPinned(bitmap, ptr => _image.Update(Int(UpdateLayer), ptr, width, height, bitmapStride, sourceFormat, matrix));
            RefreshLoadSelector();
            StatusText.Text = "Updated layer " + UpdateLayer.Text + ".";
        });
    }

    private void UpdateTileClick(object sender, RoutedEventArgs e)
    {
        Run(() =>
        {
            RequireImage();
            int layer = Int(TileLayer); long row = Long(TileRow); long column = Long(TileColumn);
            byte[] bitmap = ReadBitmap(TileFile.Text, out int width, out int height, out int bitmapStride, out PixelFormat sourceFormat);
            if (width != _image.Info.TileWidth || height != _image.Info.TileHeight)
                throw new InvalidOperationException("The selected image is " + width + " x " + height + ", but every tile must be " + _image.Info.TileWidth + " x " + _image.Info.TileHeight + ".");
            WithPinned(bitmap, ptr => _image.UpdateTile(layer, row, column, ptr, width, height, bitmapStride, sourceFormat));
            RefreshLoadSelector();
            StatusText.Text = "Updated tile (" + row + ", " + column + ").";
        });
    }

    private void LoadExportClick(object sender, RoutedEventArgs e)
    {
        Run(() =>
        {
            RequireImage();
            int width = Int(LoadWidth), height = Int(LoadHeight);
            PixelFormat format = (PixelFormat)LoadFormat.SelectedItem;
            int stride = IntOr(LoadStride, width * BytesPerPixel(format));
            byte[] output = new byte[checked(stride * height)];
            Point[] outputCorners =
            {
                new Point(0, 0),
                new Point(width, 0),
                new Point(width, height),
                new Point(0, height)
            };
            TransformMatrix matrix = LoadPerspective.CreateMatrixTo(outputCorners);
            WithPinned(output, ptr =>
            {
                if (LoadMode.SelectedIndex == 0) _image.Load(Int(LoadLayer), ptr, width, height, stride, format, matrix);
                else _image.Load(GetCompose(), ptr, width, height, stride, format, matrix);
            });
            if (string.IsNullOrWhiteSpace(ExportFile.Text)) throw new InvalidOperationException("Choose an export file first.");
            SaveOutputImage(ExportFile.Text, output, width, height, stride, format);
            StatusText.Text = "Exported " + width + " x " + height + " to " + ExportFile.Text;
        });
    }

    private void SaveImageClick(object sender, RoutedEventArgs e)
    {
        Run(() =>
        {
            RequireImage();
            var dialog = new SaveFileDialog { Filter = "GrotheImage binary (*.gim)|*.gim|All files (*.*)|*.*" };
            if (dialog.ShowDialog() == true) { _image.Save(dialog.FileName); StatusText.Text = "Saved " + dialog.FileName; }
        });
    }

    private void OpenImageClick(object sender, RoutedEventArgs e)
    {
        Run(() =>
        {
            var dialog = new OpenFileDialog { Filter = "GrotheImage binary (*.gim)|*.gim|All files (*.*)|*.*" };
            if (dialog.ShowDialog() == true)
            {
                var opened = GrotheImage.Open(dialog.FileName);
                ResetCompose();
                _image?.Dispose(); _image = opened;
                UpdatePerspective.Reset(_image.Info.Width, _image.Info.Height); LoadPerspective.Reset(_image.Info.Width, _image.Info.Height);
                RefreshImageUi();
                RefreshLoadSelector();
                StatusText.Text = "Opened " + dialog.FileName;
            }
        });
    }

    private void RefreshLoadSelector()
    {
        if (_image == null)
        {
            LoadPerspective.SetImage(null);
            return;
        }

        int layer = GetLoadLayerIndex();
        double scale = Math.Min(420.0 / _image.Info.Width, 260.0 / _image.Info.Height);
        int width = Math.Max(1, (int)Math.Round(_image.Info.Width * scale));
        int height = Math.Max(1, (int)Math.Round(_image.Info.Height * scale));
        var bitmap = new WriteableBitmap(width, height, 96, 96, WpfPixelFormats.Bgra32, null);
        var matrix = new TransformMatrix(width / (double)_image.Info.Width, 0, 0,
            0, height / (double)_image.Info.Height, 0, 0, 0, 1);
        bitmap.Lock();
        try
        {
            if (LoadMode.SelectedIndex == 0)
                _image.Load(layer, bitmap.BackBuffer, width, height, bitmap.BackBufferStride, PixelFormat.Bgra32, matrix);
            else
                _image.Load(GetCompose(), bitmap.BackBuffer, width, height, bitmap.BackBufferStride, PixelFormat.Bgra32, matrix);
            bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally { bitmap.Unlock(); }
        LoadPerspective.SetImage(bitmap);
    }

    private int GetLoadLayerIndex()
    {
        if (_image == null || !int.TryParse(LoadLayer.Text, out int layer)) return 0;
        return Math.Max(0, Math.Min(_image.LayerNames.Count - 1, layer));
    }

    private LayerCompose GetCompose()
    {
        string expression = ComposeExpression.Text;
        if (_compose != null && _composeExpression == expression) return _compose;
        ResetCompose();
        _compose = _image.CreateLayerCompose(expression);
        _composeExpression = expression;
        return _compose;
    }

    private void ResetCompose()
    {
        _compose?.Dispose();
        _compose = null;
        _composeExpression = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        ResetCompose();
        _image?.Dispose();
        base.OnClosed(e);
    }

    private void ChooseOpen(System.Windows.Controls.TextBox target)
    {
        var dialog = new OpenFileDialog(); if (dialog.ShowDialog() == true) target.Text = dialog.FileName;
    }

    private void ChooseImageSave(System.Windows.Controls.TextBox target)
    {
        var dialog = new SaveFileDialog
        {
            DefaultExt = ".png",
            AddExtension = true,
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg;*.jpeg)|*.jpg;*.jpeg"
        };
        if (dialog.ShowDialog() == true) target.Text = dialog.FileName;
    }

    private static void SaveOutputImage(string path, byte[] output, int width, int height, int stride, PixelFormat format)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Choose a PNG or JPEG export file first.");
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
            throw new InvalidOperationException("The export file must have a .png, .jpg, or .jpeg extension.");

        BitmapSource bitmap = CreateBgraBitmap(output, width, height, stride, format);
        BitmapFrame frame = BitmapFrame.Create(bitmap);
        BitmapEncoder encoder;
        if (extension == ".jpg" || extension == ".jpeg")
        {
            var jpegSource = new FormatConvertedBitmap(bitmap, WpfPixelFormats.Bgr24, null, 0);
            frame = BitmapFrame.Create(jpegSource);
            encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        }
        else
        {
            encoder = new PngBitmapEncoder();
        }
        encoder.Frames.Add(frame);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) encoder.Save(stream);
    }

    private static BitmapSource CreateBgraBitmap(byte[] source, int width, int height, int sourceStride, PixelFormat format)
    {
        byte[] bgra = new byte[checked(width * height * 4)];
        int sourceBytes = BytesPerPixel(format);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int si = y * sourceStride + x * sourceBytes;
            int di = (y * width + x) * 4;
            byte b;
            byte g;
            byte r;
            byte a = 255;
            if (format == PixelFormat.Bgra32)
            {
                b = source[si]; g = source[si + 1]; r = source[si + 2]; a = source[si + 3];
            }
            else if (format == PixelFormat.Rgba32)
            {
                r = source[si]; g = source[si + 1]; b = source[si + 2]; a = source[si + 3];
            }
            else if (format == PixelFormat.Gray8)
            {
                b = g = r = source[si];
            }
            else throw new ArgumentOutOfRangeException(nameof(format));
            bgra[di] = b; bgra[di + 1] = g; bgra[di + 2] = r; bgra[di + 3] = a;
        }
        return BitmapSource.Create(width, height, 96, 96, WpfPixelFormats.Bgra32, null, bgra, width * 4);
    }

    private static byte[] ReadBitmap(string path, out int width, out int height, out int stride, out PixelFormat format)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Choose an input file first.");
        var decoder = new BitmapImage(); decoder.BeginInit(); decoder.UriSource = new Uri(Path.GetFullPath(path)); decoder.CacheOption = BitmapCacheOption.OnLoad; decoder.EndInit();
        BitmapSource source = decoder;
        format = DetectTransferFormat(decoder.Format, out bool needsBgraConversion);
        if (needsBgraConversion)
        {
            source = new FormatConvertedBitmap(decoder, WpfPixelFormats.Bgra32, null, 0);
            format = PixelFormat.Bgra32;
        }
        width = source.PixelWidth;
        height = source.PixelHeight;
        stride = checked(width * BytesPerPixel(format));
        byte[] bytes = new byte[checked(stride * height)];
        source.CopyPixels(bytes, stride, 0);
        return bytes;
    }

    private void RefreshUpdateFileInfo()
    {
        if (string.IsNullOrWhiteSpace(UpdateFile.Text)) { UpdateFileInfo.Text = "Choose an image file."; return; }
        ReadBitmap(UpdateFile.Text, out int width, out int height, out int stride, out PixelFormat format);
        UpdateFileInfo.Text = width + " x " + height + ", stride " + stride + ", format " + format + " (decoded)";
    }

    private void RefreshTileFileInfo()
    {
        if (_image == null) { TileFileInfo.Text = "Create or open a GrotheImage first."; return; }
        bool targetIsPlanar = IsSubsampled(_image.Format);
        if (targetIsPlanar)
            TileFileInfo.Text = "The current storage format is planar " + _image.Format + "; the decoded tile image is converted on the GPU.";
        if (string.IsNullOrWhiteSpace(TileFile.Text))
        {
            if (!targetIsPlanar) TileFileInfo.Text = "Choose an image file.";
            return;
        }
        ReadBitmap(TileFile.Text, out int width, out int height, out int stride, out PixelFormat format);
        TileFileInfo.Text = width + " x " + height + ", stride " + stride + ", source format " + format + "; target format " + _image.Format + ". Tile layout requires " + _image.Info.TileWidth + " x " + _image.Info.TileHeight + ".";
    }

    private void RefreshImageUi()
    {
        if (_image == null)
        {
            PreviewLayer.ItemsSource = null;
            PreviewImage.Source = null;
            LoadPerspective.SetImage(null);
            TileStorageInfo.Text = "Create or open an image first.";
            TileFileInfo.Text = "Create or open a GrotheImage first.";
            return;
        }
        PreviewLayer.ItemsSource = _image.LayerNames;
        PreviewLayer.SelectedIndex = 0;
        PreviewImage.Source = null;
        TileStorageInfo.Text = _image.Format + ", tile " + _image.Info.TileWidth + " x " + _image.Info.TileHeight;
        TryRefresh(RefreshTileFileInfo);
    }

    private void RenderPreviewClick(object sender, RoutedEventArgs e)
    {
        Run(() =>
        {
            RequireImage();
            if (PreviewHost.ActualWidth < 1 || PreviewHost.ActualHeight < 1) throw new InvalidOperationException("The preview area has no usable size.");
            int outputWidth = Math.Max(1, (int)Math.Round(PreviewHost.ActualWidth));
            int outputHeight = Math.Max(1, (int)Math.Round(PreviewHost.ActualHeight));
            double scale = Math.Min(outputWidth / (double)_image.Info.Width, outputHeight / (double)_image.Info.Height);
            double clientWidth = _image.Info.Width * scale;
            double clientHeight = _image.Info.Height * scale;
            double offsetX = (outputWidth - clientWidth) * 0.5;
            double offsetY = (outputHeight - clientHeight) * 0.5;
            var matrix = new TransformMatrix(scale, 0, offsetX, 0, scale, offsetY, 0, 0, 1);
            var bitmap = new WriteableBitmap(outputWidth, outputHeight, 96, 96, WpfPixelFormats.Bgra32, null);
            int layer = PreviewLayer.SelectedIndex;
            if (layer < 0) layer = 0;
            bitmap.Lock();
            try
            {
                _image.Load(layer, bitmap.BackBuffer, outputWidth, outputHeight, bitmap.BackBufferStride, PixelFormat.Bgra32, matrix);
                bitmap.AddDirtyRect(new Int32Rect(0, 0, outputWidth, outputHeight));
            }
            finally { bitmap.Unlock(); }
            PreviewImage.Source = bitmap;
            StatusText.Text = "Rendered layer " + layer + " at " + outputWidth + " x " + outputHeight + ".";
        });
    }

    private static PixelFormat DetectTransferFormat(System.Windows.Media.PixelFormat wpfFormat, out bool needsBgraConversion)
    {
        if (wpfFormat == WpfPixelFormats.Bgra32) { needsBgraConversion = false; return PixelFormat.Bgra32; }
        if (wpfFormat == WpfPixelFormats.Gray8) { needsBgraConversion = false; return PixelFormat.Gray8; }
        needsBgraConversion = true;
        return PixelFormat.Bgra32;
    }

    private static void WithPinned(byte[] data, Action<IntPtr> action)
    {
        GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned); try { action(pin.AddrOfPinnedObject()); } finally { pin.Free(); }
    }

    private void RequireImage() { if (_image == null) throw new InvalidOperationException("Create or open a GrotheImage first."); }
    private static void TryRefresh(Action action) { try { action(); } catch { } }
    private static bool IsSubsampled(PixelFormat format) => format == PixelFormat.Yuv422 || format == PixelFormat.Yuv420;
    private static bool IsTransferFormat(PixelFormat format) => format == PixelFormat.Bgra32 || format == PixelFormat.Rgba32 || format == PixelFormat.Gray8;
    private static int BytesPerPixel(PixelFormat format) => format == PixelFormat.Gray8 ? 1 : 4;
    private static int Int(System.Windows.Controls.TextBox box) => int.Parse(box.Text);
    private static long Long(System.Windows.Controls.TextBox box) => long.Parse(box.Text);
    private static int IntOr(System.Windows.Controls.TextBox box, int fallback) => string.IsNullOrWhiteSpace(box.Text) || box.Text == "0" ? fallback : int.Parse(box.Text);
    private void Run(Action action) { try { action(); } catch (Exception ex) { StatusText.Text = ex.GetType().Name + ": " + ex.Message; MessageBox.Show(this, ex.ToString(), "GrotheImages Playground", MessageBoxButton.OK, MessageBoxImage.Error); } }
}
