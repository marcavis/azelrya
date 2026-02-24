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
    private const byte MinElevationShade = 48;
    private const byte MaxElevationShade = 235;
    private const int ElevationStep = 4;

    private enum MapLayer
    {
        Elevation,
        BaseTerrain
    }

    private int _mapWidth = 512;
    private int _mapHeight = 512;
    private byte[] _terrainPixels = [];
    private byte[] _displayPixels = [];
    private byte[] _elevation = [];
    private WriteableBitmap? _bitmap;
    private bool _isPainting;
    private double _zoomScale = 1.0;
    private AvaloniaColor _selectedColor = LandColor;
    private readonly Stack<MapState> _undoStates = new();
    private readonly Stack<MapState> _redoStates = new();
    private readonly int _historyLimit;
    private MapLayer _activeLayer = MapLayer.BaseTerrain;
    private int _elevationDirection;

    private sealed class MapState
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required byte[] TerrainPixels { get; init; }
        public required byte[] Elevation { get; init; }
    }

    public MainWindow()
    {
        InitializeComponent();
        var config = AzelryaConfig.Load(AppContext.BaseDirectory);
        _historyLimit = config.HistoryLimit;
        InitializeMap(_mapWidth, _mapHeight, WaterColor);
        UpdateBrushSizeText();
        UpdateZoomStatusText();
        UpdateLayerOptions();
        UpdateHistoryButtons();
        StatusText.Text = $"Ready (history limit: {_historyLimit})";
    }

    private int BrushSize => Math.Max(1, (int)Math.Round(BrushSizeSlider.Value));

    private void InitializeMap(int width, int height, AvaloniaColor fillColor)
    {
        _mapWidth = width;
        _mapHeight = height;
        _terrainPixels = new byte[_mapWidth * _mapHeight * 4];
        _displayPixels = new byte[_mapWidth * _mapHeight * 4];
        _elevation = new byte[_mapWidth * _mapHeight];

        FillAll(fillColor);
        _bitmap = new WriteableBitmap(
            new PixelSize(_mapWidth, _mapHeight),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        MapImage.Source = _bitmap;
        ApplyZoomLayout();
        RenderActiveLayer();
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
        for (var i = 0; i < _terrainPixels.Length; i += 4)
        {
            _terrainPixels[i] = color.B;
            _terrainPixels[i + 1] = color.G;
            _terrainPixels[i + 2] = color.R;
            _terrainPixels[i + 3] = 255;
        }

        Array.Fill(_elevation, (byte)0);
    }

    private void SetTerrainPixel(int x, int y, AvaloniaColor color)
    {
        if (x < 0 || y < 0 || x >= _mapWidth || y >= _mapHeight)
        {
            return;
        }

        var index = (y * _mapWidth + x) * 4;
        _terrainPixels[index] = color.B;
        _terrainPixels[index + 1] = color.G;
        _terrainPixels[index + 2] = color.R;
        _terrainPixels[index + 3] = 255;

        if (color.B == WaterColor.B && color.G == WaterColor.G && color.R == WaterColor.R)
        {
            _elevation[y * _mapWidth + x] = 0;
        }
    }

    private static byte ElevationToShade(byte elevation)
    {
        return (byte)(MinElevationShade + elevation * (MaxElevationShade - MinElevationShade) / 255);
    }

    private static bool IsColor(byte[] pixels, int colorIndex, AvaloniaColor color)
    {
        return pixels[colorIndex] == color.B &&
               pixels[colorIndex + 1] == color.G &&
               pixels[colorIndex + 2] == color.R;
    }

    private bool IsLandAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= _mapWidth || y >= _mapHeight)
        {
            return false;
        }

        var colorIndex = (y * _mapWidth + x) * 4;
        return !IsColor(_terrainPixels, colorIndex, WaterColor);
    }

    private void SetDisplayPixel(int x, int y, AvaloniaColor color)
    {
        var index = (y * _mapWidth + x) * 4;
        _displayPixels[index] = color.B;
        _displayPixels[index + 1] = color.G;
        _displayPixels[index + 2] = color.R;
        _displayPixels[index + 3] = 255;
    }

    private void PaintTerrainCircle(int centerX, int centerY)
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
                    SetTerrainPixel(x, y, _selectedColor);
                }
            }
        }

        RenderActiveLayer();
    }

    private void PaintElevationCircle(int centerX, int centerY, int direction)
    {
        if (direction == 0)
        {
            return;
        }

        var radius = BrushSize / 2;
        var radiusSquared = radius * radius;

        for (var y = centerY - radius; y <= centerY + radius; y++)
        {
            for (var x = centerX - radius; x <= centerX + radius; x++)
            {
                if (x < 0 || y < 0 || x >= _mapWidth || y >= _mapHeight)
                {
                    continue;
                }

                var dx = x - centerX;
                var dy = y - centerY;
                if (dx * dx + dy * dy > radiusSquared || !IsLandAt(x, y))
                {
                    continue;
                }

                var elevationIndex = y * _mapWidth + x;
                var updated = Math.Clamp(_elevation[elevationIndex] + direction * ElevationStep, 0, 255);
                _elevation[elevationIndex] = (byte)updated;
            }
        }

        RenderActiveLayer();
    }

    private void RenderActiveLayer()
    {
        if (_activeLayer == MapLayer.BaseTerrain)
        {
            Buffer.BlockCopy(_terrainPixels, 0, _displayPixels, 0, _terrainPixels.Length);
            SyncBitmapFromPixels();
            return;
        }

        for (var y = 0; y < _mapHeight; y++)
        {
            for (var x = 0; x < _mapWidth; x++)
            {
                if (!IsLandAt(x, y))
                {
                    SetDisplayPixel(x, y, WaterColor);
                    continue;
                }

                var elevation = _elevation[y * _mapWidth + x];
                var shade = ElevationToShade(elevation);
                SetDisplayPixel(x, y, AvaloniaColor.FromRgb(shade, shade, shade));
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
            Marshal.Copy(_displayPixels, sourceOffset, destinationPtr, stride);
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
            TerrainPixels = (byte[])_terrainPixels.Clone(),
            Elevation = (byte[])_elevation.Clone()
        };
    }

    private void RestoreState(MapState state)
    {
        if (_mapWidth != state.Width || _mapHeight != state.Height || _bitmap is null)
        {
            InitializeMap(state.Width, state.Height, WaterColor);
        }

        _terrainPixels = (byte[])state.TerrainPixels.Clone();
        _elevation = (byte[])state.Elevation.Clone();
        RenderActiveLayer();
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
                    _terrainPixels[index] = pixel.B;
                    _terrainPixels[index + 1] = pixel.G;
                    _terrainPixels[index + 2] = pixel.R;
                    _terrainPixels[index + 3] = 255;
                }
            }
        });

        Array.Fill(_elevation, (byte)0);
        RenderActiveLayer();
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
        using var image = ImageSharpImage.LoadPixelData<Bgra32>(_terrainPixels, _mapWidth, _mapHeight);
        await image.SaveAsync(stream, new PngEncoder());

        StatusText.Text = "PNG exported";
    }

    private void PaintSurface_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(PaintSurface).Properties;
        if (_activeLayer == MapLayer.BaseTerrain && !properties.IsLeftButtonPressed)
        {
            return;
        }

        if (_activeLayer == MapLayer.Elevation && !properties.IsLeftButtonPressed && !properties.IsRightButtonPressed)
        {
            return;
        }

        PushUndoState();
        _isPainting = true;
        _elevationDirection = properties.IsLeftButtonPressed ? 1 : properties.IsRightButtonPressed ? -1 : 0;
        BrushPreview.IsVisible = true;
        e.Pointer.Capture(PaintSurface);
        PaintAtPointer(e);
    }

    private void PaintSurface_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(PaintSurface);
        UpdateBrushPreview(point);
        UpdateCursorStatusAtPoint(point);

        if (!_isPainting)
        {
            return;
        }

        PaintAtPointer(e);
    }

    private void PaintSurface_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isPainting = false;
        _elevationDirection = 0;
        e.Pointer.Capture(null);
    }

    private void PaintSurface_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        BrushPreview.IsVisible = true;
        var point = e.GetPosition(PaintSurface);
        UpdateBrushPreview(point);
        UpdateCursorStatusAtPoint(point);
    }

    private void PaintSurface_OnPointerExited(object? sender, PointerEventArgs e)
    {
        BrushPreview.IsVisible = false;
        _isPainting = false;
        _elevationDirection = 0;
        SetCursorStatusUnknown();
    }

    private void PaintAtPointer(PointerEventArgs e)
    {
        var point = e.GetPosition(PaintSurface);
        UpdateBrushPreview(point);
        UpdateCursorStatusAtPoint(point);

        var pixel = TryGetPixelFromPoint(point);
        if (pixel is null)
        {
            return;
        }

        if (_activeLayer == MapLayer.BaseTerrain)
        {
            PaintTerrainCircle(pixel.Value.x, pixel.Value.y);
            return;
        }

        var properties = e.GetCurrentPoint(PaintSurface).Properties;
        var direction = properties.IsLeftButtonPressed ? 1 : properties.IsRightButtonPressed ? -1 : _elevationDirection;
        PaintElevationCircle(pixel.Value.x, pixel.Value.y, direction);
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
        RenderActiveLayer();
        StatusText.Text = "Canvas cleared to water";
    }

    private void ElevationLayerButton_OnChecked(object? sender, RoutedEventArgs e)
    {
        _activeLayer = MapLayer.Elevation;
        UpdateLayerOptions();
        RenderActiveLayer();
        SetCursorStatusUnknown();
        StatusText.Text = "Elevation layer selected";
    }

    private void BaseTerrainLayerButton_OnChecked(object? sender, RoutedEventArgs e)
    {
        _activeLayer = MapLayer.BaseTerrain;
        UpdateLayerOptions();
        RenderActiveLayer();
        SetCursorStatusUnknown();
        StatusText.Text = "Base terrain layer selected";
    }

    private void UpdateLayerOptions()
    {
        var isTerrain = _activeLayer == MapLayer.BaseTerrain;
        TerrainOptionsPanel.IsVisible = isTerrain;
        ElevationOptionsPanel.IsVisible = !isTerrain;
    }

    private void UpdateCursorStatusAtPoint(AvaloniaPoint point)
    {
        var pixel = TryGetPixelFromPoint(point);
        if (pixel is null)
        {
            SetCursorStatusUnknown();
            return;
        }

        var x = pixel.Value.x;
        var y = pixel.Value.y;
        CursorPositionText.Text = $"Cursor: {x},{y}";

        if (!IsLandAt(x, y))
        {
            CursorTerrainText.Text = "Water";
            CursorElevationText.Text = "Elevation: N/A";
            return;
        }

        var value = _elevation[y * _mapWidth + x];
        var percent = (int)Math.Round(value / 255.0 * 100.0);
        CursorTerrainText.Text = "Land";
        CursorElevationText.Text = $"Elevation: {value} ({percent}%)";
    }

    private void SetCursorStatusUnknown()
    {
        CursorPositionText.Text = "Cursor: --";
        CursorTerrainText.Text = "--";
        CursorElevationText.Text = "Elevation: --";
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