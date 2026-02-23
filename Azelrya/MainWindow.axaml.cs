using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using AvaloniaColor = Avalonia.Media.Color;
using AvaloniaPoint = Avalonia.Point;
using ImageSharpImage = SixLabors.ImageSharp.Image;

namespace Azelrya;

public partial class MainWindow : Window
{
    private static readonly AvaloniaColor LandColor = AvaloniaColor.FromRgb(34, 139, 34);
    private static readonly AvaloniaColor WaterColor = AvaloniaColor.FromRgb(70, 130, 180);
    private const double MinZoom = 1.0;
    private const double MaxZoom = 4.0;

    private int _mapWidth = 512;
    private int _mapHeight = 512;
    private byte[] _pixels = [];
    private WriteableBitmap? _bitmap;
    private bool _isPainting;
    private double _zoomScale = 1.0;
    private AvaloniaColor _selectedColor = LandColor;
    private readonly Stack<MapState> _undoStates = new();
    private readonly Stack<MapState> _redoStates = new();
    private readonly int _historyLimit;

    private sealed class MapState
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required byte[] Pixels { get; init; }
    }

    public MainWindow()
    {
        InitializeComponent();
        var config = AzelryaConfig.Load(AppContext.BaseDirectory);
        _historyLimit = config.HistoryLimit;
        InitializeMap(_mapWidth, _mapHeight, WaterColor);
        UpdateBrushSizeText();
        UpdateZoomStatusText();
        UpdateHistoryButtons();
        StatusText.Text = $"Ready (history limit: {_historyLimit})";
    }

    private int BrushSize => Math.Max(1, (int)Math.Round(BrushSizeSlider.Value));

    private void InitializeMap(int width, int height, AvaloniaColor fillColor)
    {
        _mapWidth = width;
        _mapHeight = height;
        _pixels = new byte[_mapWidth * _mapHeight * 4];

        FillAll(fillColor);
        _bitmap = new WriteableBitmap(
            new PixelSize(_mapWidth, _mapHeight),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        MapImage.Source = _bitmap;
        ApplyZoomLayout();
        SyncBitmapFromPixels();
    }

    private void ApplyZoomLayout()
    {
        var scaledWidth = _mapWidth * _zoomScale;
        var scaledHeight = _mapHeight * _zoomScale;
        MapImage.Width = scaledWidth;
        MapImage.Height = scaledHeight;
        PaintSurface.Width = scaledWidth;
        PaintSurface.Height = scaledHeight;

        var left = Canvas.GetLeft(BrushPreview);
        var top = Canvas.GetTop(BrushPreview);
        if (!double.IsNaN(left) && !double.IsNaN(top))
        {
            var center = new AvaloniaPoint(left + BrushPreview.Width / 2.0, top + BrushPreview.Height / 2.0);
            UpdateBrushPreview(center);
        }
    }

    private void SetZoom(double zoomScale)
    {
        _zoomScale = Math.Clamp(zoomScale, MinZoom, MaxZoom);
        ApplyZoomLayout();
        UpdateZoomStatusText();
    }

    private void UpdateZoomStatusText()
    {
        ZoomStatusText.Text = $"Zoom: {(int)Math.Round(_zoomScale * 100)}%";
    }

    private void FillAll(AvaloniaColor color)
    {
        for (var i = 0; i < _pixels.Length; i += 4)
        {
            _pixels[i] = color.B;
            _pixels[i + 1] = color.G;
            _pixels[i + 2] = color.R;
            _pixels[i + 3] = 255;
        }
    }

    private void SetPixel(int x, int y, AvaloniaColor color)
    {
        if (x < 0 || y < 0 || x >= _mapWidth || y >= _mapHeight)
        {
            return;
        }

        var index = (y * _mapWidth + x) * 4;
        _pixels[index] = color.B;
        _pixels[index + 1] = color.G;
        _pixels[index + 2] = color.R;
        _pixels[index + 3] = 255;
    }

    private void PaintCircle(int centerX, int centerY)
    {
        var radius = BrushSize / 2;
        var radiusSquared = radius * radius;

        for (var y = centerY - radius; y <= centerY + radius; y++)
        {
            for (var x = centerX - radius; x <= centerX + radius; x++)
            {
                var dx = x - centerX;
                var dy = y - centerY;
                if (dx * dx + dy * dy <= radiusSquared)
                {
                    SetPixel(x, y, _selectedColor);
                }
            }
        }

        SyncBitmapFromPixels();
    }

    private void SyncBitmapFromPixels()
    {
        if (_bitmap is null)
        {
            return;
        }

        using var framebuffer = _bitmap.Lock();
        var stride = _mapWidth * 4;
        for (var y = 0; y < _mapHeight; y++)
        {
            var sourceOffset = y * stride;
            var destinationPtr = framebuffer.Address + y * framebuffer.RowBytes;
            Marshal.Copy(_pixels, sourceOffset, destinationPtr, stride);
        }

        MapImage.InvalidateVisual();
        PaintSurface.InvalidateVisual();
    }

    private (int x, int y)? TryGetPixelFromPoint(AvaloniaPoint point)
    {
        if (_mapWidth <= 0 || _mapHeight <= 0)
        {
            return null;
        }

        var x = (int)Math.Floor(point.X / _zoomScale);
        var y = (int)Math.Floor(point.Y / _zoomScale);

        if (x < 0 || y < 0 || x >= _mapWidth || y >= _mapHeight)
        {
            return null;
        }

        return (x, y);
    }

    private void UpdateBrushSizeText()
    {
        BrushSizeText.Text = $"{BrushSize} px";
    }

    private void UpdateBrushPreview(AvaloniaPoint point)
    {
        var diameter = Math.Max(1.0, BrushSize * _zoomScale);
        BrushPreview.Width = diameter;
        BrushPreview.Height = diameter;
        Canvas.SetLeft(BrushPreview, point.X - diameter / 2.0);
        Canvas.SetTop(BrushPreview, point.Y - diameter / 2.0);
    }

    private MapState CaptureState()
    {
        return new MapState
        {
            Width = _mapWidth,
            Height = _mapHeight,
            Pixels = (byte[])_pixels.Clone()
        };
    }

    private void RestoreState(MapState state)
    {
        if (_mapWidth != state.Width || _mapHeight != state.Height || _bitmap is null)
        {
            InitializeMap(state.Width, state.Height, WaterColor);
        }

        _pixels = (byte[])state.Pixels.Clone();
        SyncBitmapFromPixels();
    }

    private void PushUndoState()
    {
        PushHistoryState(_undoStates, CaptureState());
        _redoStates.Clear();
        UpdateHistoryButtons();
    }

    private void PushHistoryState(Stack<MapState> historyStack, MapState state)
    {
        if (_historyLimit <= 0)
        {
            return;
        }

        if (historyStack.Count >= _historyLimit)
        {
            var newestFirst = historyStack.ToArray();
            var oldestToNewest = newestFirst.Reverse().ToArray();

            historyStack.Clear();
            foreach (var item in oldestToNewest.Skip(1))
            {
                historyStack.Push(item);
            }
        }

        historyStack.Push(state);
    }

    private void UpdateHistoryButtons()
    {
        UndoButton.IsEnabled = _undoStates.Count > 0;
        RedoButton.IsEnabled = _redoStates.Count > 0;
        UndoMenuItem.IsEnabled = _undoStates.Count > 0;
        RedoMenuItem.IsEnabled = _redoStates.Count > 0;
    }

    private bool TryUndo()
    {
        if (_undoStates.Count == 0)
        {
            return false;
        }

        PushHistoryState(_redoStates, CaptureState());
        var state = _undoStates.Pop();
        RestoreState(state);
        UpdateHistoryButtons();
        StatusText.Text = "Undo";
        return true;
    }

    private bool TryRedo()
    {
        if (_redoStates.Count == 0)
        {
            return false;
        }

        PushHistoryState(_undoStates, CaptureState());
        var state = _redoStates.Pop();
        RestoreState(state);
        UpdateHistoryButtons();
        StatusText.Text = "Redo";
        return true;
    }

    private async void ImportPngButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (StorageProvider is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import PNG",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("PNG Image")
                {
                    Patterns = ["*.png"]
                }
            ]
        });

        if (files.Count() == 0)
        {
            return;
        }

        await using var stream = await files[0].OpenReadAsync();
        using var image = await ImageSharpImage.LoadAsync<Rgba32>(stream);

        PushUndoState();

        InitializeMap(image.Width, image.Height, WaterColor);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < _mapHeight; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < _mapWidth; x++)
                {
                    var pixel = row[x];
                    var index = (y * _mapWidth + x) * 4;
                    _pixels[index] = pixel.B;
                    _pixels[index + 1] = pixel.G;
                    _pixels[index + 2] = pixel.R;
                    _pixels[index + 3] = 255;
                }
            }
        });

        SyncBitmapFromPixels();
        StatusText.Text = $"Imported {_mapWidth}x{_mapHeight} PNG";
    }

    private async void ExportPngButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (StorageProvider is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export PNG",
            SuggestedFileName = "azelrya-map.png",
            FileTypeChoices =
            [
                new FilePickerFileType("PNG Image")
                {
                    Patterns = ["*.png"]
                }
            ]
        });

        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        using var image = ImageSharpImage.LoadPixelData<Bgra32>(_pixels, _mapWidth, _mapHeight);
        await image.SaveAsync(stream, new PngEncoder());

        StatusText.Text = "PNG exported";
    }

    private void PaintSurface_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(PaintSurface).Properties.IsLeftButtonPressed is false)
        {
            return;
        }

        PushUndoState();
        _isPainting = true;
        BrushPreview.IsVisible = true;
        e.Pointer.Capture(PaintSurface);
        PaintAtPointer(e);
    }

    private void PaintSurface_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(PaintSurface);
        UpdateBrushPreview(point);

        if (!_isPainting)
        {
            return;
        }

        PaintAtPointer(e);
    }

    private void PaintSurface_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isPainting = false;
        e.Pointer.Capture(null);
    }

    private void PaintSurface_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        BrushPreview.IsVisible = true;
        var point = e.GetPosition(PaintSurface);
        UpdateBrushPreview(point);
    }

    private void PaintSurface_OnPointerExited(object? sender, PointerEventArgs e)
    {
        BrushPreview.IsVisible = false;
        _isPainting = false;
    }

    private void PaintAtPointer(PointerEventArgs e)
    {
        var point = e.GetPosition(PaintSurface);
        UpdateBrushPreview(point);

        var pixel = TryGetPixelFromPoint(point);
        if (pixel is null)
        {
            return;
        }

        PaintCircle(pixel.Value.x, pixel.Value.y);
    }

    private void BrushSizeSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateBrushSizeText();
        var left = Canvas.GetLeft(BrushPreview);
        var top = Canvas.GetTop(BrushPreview);
        if (double.IsNaN(left) || double.IsNaN(top))
        {
            return;
        }

        var center = new AvaloniaPoint(left + BrushPreview.Width / 2.0, top + BrushPreview.Height / 2.0);
        UpdateBrushPreview(center);
    }

    private void UndoButton_OnClick(object? sender, RoutedEventArgs e)
    {
        TryUndo();
    }

    private void RedoButton_OnClick(object? sender, RoutedEventArgs e)
    {
        TryRedo();
    }

    private void LandColorButton_OnChecked(object? sender, RoutedEventArgs e)
    {
        _selectedColor = LandColor;
        StatusText.Text = "Selected land brush";
    }

    private void WaterColorButton_OnChecked(object? sender, RoutedEventArgs e)
    {
        _selectedColor = WaterColor;
        StatusText.Text = "Selected water brush";
    }

    private void ClearButton_OnClick(object? sender, RoutedEventArgs e)
    {
        PushUndoState();
        FillAll(WaterColor);
        SyncBitmapFromPixels();
        StatusText.Text = "Canvas cleared to water";
    }

    private void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        var isControlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (!isControlPressed)
        {
            return;
        }

        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (TryRedo())
            {
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.Z)
        {
            if (TryUndo())
            {
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.Y)
        {
            if (TryRedo())
            {
                e.Handled = true;
            }
        }
    }

    private void NewMapMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        PushUndoState();
        InitializeMap(512, 512, WaterColor);
        StatusText.Text = "New map created";
    }

    private void ExitMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Zoom400MenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        SetZoom(4.0);
    }

    private void Zoom300MenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        SetZoom(3.0);
    }

    private void Zoom200MenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        SetZoom(2.0);
    }

    private void Zoom100MenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        SetZoom(1.0);
    }
}