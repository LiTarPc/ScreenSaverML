using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Interop;
using System.Collections.Generic;

using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;
using Path = System.IO.Path;

namespace ScreenshotSaver
{
    public partial class MainWindow : Window
    {
        private Point _startPoint;
        private Rectangle? _currentShape;
        private bool _isDrawingShape;
        private bool _isCropping;

        private readonly AppConfig _config;
        private System.Windows.Forms.NotifyIcon _notifyIcon = null!;
        private bool _isExiting;

        private HwndSource? _hwndSource;
        private string _lastImageHash = "";

        // Memory-Efficient Undo History Stack
        private class EditorState
        {
            // Store compressed PNG bytes instead of raw uncompressed BitmapSource
            // This reduces RAM consumption of history by ~95-98% (e.g. 1MB vs 33MB per state)
            public byte[]? CompressedImage { get; set; }
            public StrokeCollection Strokes { get; set; } = null!;
            public double Width { get; set; }
            public double Height { get; set; }
        }

        private readonly List<EditorState> _undoHistory = new();
        private const int MaxHistorySteps = 10;

        // Win32 APIs for memory trimming and clipboard listening
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        public MainWindow(bool startMinimized)
        {
            InitializeComponent();

            _config = AppConfig.Load();

            InitializeTrayIcon();
            LoadSettingsToUI();
            UpdateToolMode();

            this.PreviewKeyDown += MainWindow_KeyDown;
            this.Closing += MainWindow_Closing;

            if (startMinimized)
            {
                // Trim memory immediately after WPF finishes startup layout pass
                Dispatcher.BeginInvoke(new Action(MinimizeMemory), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var helper = new WindowInteropHelper(this);
            IntPtr handle = helper.Handle;

            _hwndSource = HwndSource.FromHwnd(handle);
            if (_hwndSource != null)
            {
                _hwndSource.AddHook(WndProc);
                AddClipboardFormatListener(handle);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_CLIPBOARDUPDATE = 0x031D;
            if (msg == WM_CLIPBOARDUPDATE)
            {
                CheckClipboardAndAutoSave();
            }
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_hwndSource != null)
            {
                var helper = new WindowInteropHelper(this);
                RemoveClipboardFormatListener(helper.Handle);
                _hwndSource.RemoveHook(WndProc);
                _hwndSource = null;
            }

            if (_notifyIcon != null)
            {
                _notifyIcon.Dispose();
            }

            base.OnClosed(e);
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_config.MinimizeToTrayOnClose && !_isExiting)
            {
                e.Cancel = true;
                this.Hide();
                MinimizeMemory(); // Reclaim RAM as soon as window hides
            }
        }

        private void InitializeTrayIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            try
            {
                string processPath = Environment.ProcessPath ?? AppDomain.CurrentDomain.BaseDirectory;
                _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
            }
            catch
            {
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            _notifyIcon.Text = "Screenshot Saver & Editor";
            _notifyIcon.Visible = true;

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();

            var openItem = new System.Windows.Forms.ToolStripMenuItem("Открыть редактор");
            openItem.Click += (s, e) => RestoreWindow();

            var folderItem = new System.Windows.Forms.ToolStripMenuItem("Папка со скриншотами");
            folderItem.Click += (s, e) => OpenSaveFolder();

            var exitItem = new System.Windows.Forms.ToolStripMenuItem("Выход");
            exitItem.Click += (s, e) =>
            {
                _isExiting = true;
                this.Close();
                Application.Current.Shutdown();
            };

            contextMenu.Items.Add(openItem);
            contextMenu.Items.Add(folderItem);
            contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            contextMenu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = contextMenu;

            _notifyIcon.DoubleClick += (s, e) => RestoreWindow();
            _notifyIcon.Click += (s, e) =>
            {
                if (e is System.Windows.Forms.MouseEventArgs m && m.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    RestoreWindow();
                }
            };
        }

        private void RestoreWindow()
        {
            this.Show();
            if (this.WindowState == WindowState.Minimized)
            {
                this.WindowState = WindowState.Normal;
            }
            this.Activate();
        }

        private void OpenSaveFolder()
        {
            try
            {
                _config.EnsureFolderExists();
                if (Directory.Exists(_config.SaveFolder))
                {
                    System.Diagnostics.Process.Start("explorer.exe", _config.SaveFolder);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось открыть папку: " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadSettingsToUI()
        {
            CbAutoSave.IsChecked = _config.AutoSaveEnabled;
            CbRunAtStartup.IsChecked = _config.RunAtStartup;
            CbMinimizeToTray.IsChecked = _config.MinimizeToTrayOnClose;
            TxtSaveFolder.Text = _config.SaveFolder;
        }

        // --- MEMORY OPTIMIZATION UTILITIES ---

        private void MinimizeMemory()
        {
            try
            {
                // Force full garbage collection to release dead handles and screenshot streams
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                // Instruct OS to trim the working set (reclaim physical RAM, swapping out inactive WPF memory pages)
                using (var process = System.Diagnostics.Process.GetCurrentProcess())
                {
                    SetProcessWorkingSetSize(process.Handle, -1, -1);
                }
            }
            catch
            {
                // Safe ignore
            }
        }

        private byte[]? CompressImage(BitmapSource? image)
        {
            if (image == null) return null;
            try
            {
                using (var stream = new MemoryStream())
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(stream);
                    return stream.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        private BitmapSource? DecompressImage(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                using (var stream = new MemoryStream(bytes))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = stream;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze(); // Crucial for performance and thread safety
                    return bitmap;
                }
            }
            catch
            {
                return null;
            }
        }

        // --- UNDO HISTORY LOGIC ---

        private EditorState SaveState()
        {
            return new EditorState
            {
                CompressedImage = CompressImage(ScreenshotImage.Source as BitmapSource),
                Strokes = DrawingCanvas.Strokes.Clone(),
                Width = MainGrid.Width,
                Height = MainGrid.Height
            };
        }

        private void RestoreState(EditorState state)
        {
            var image = DecompressImage(state.CompressedImage);
            ScreenshotImage.Source = image;
            DrawingCanvas.Strokes = state.Strokes;

            MainGrid.Width = state.Width;
            MainGrid.Height = state.Height;
            DrawingCanvas.Width = state.Width;
            DrawingCanvas.Height = state.Height;
            ShapesCanvas.Width = state.Width;
            ShapesCanvas.Height = state.Height;
            CropCanvas.Width = state.Width;
            CropCanvas.Height = state.Height;

            if (image == null)
            {
                PlaceholderGrid.Visibility = Visibility.Visible;
            }
            else
            {
                PlaceholderGrid.Visibility = Visibility.Collapsed;
            }
        }

        private void PushStateToHistory()
        {
            var currentState = SaveState();

            // Avoid redundant clicks pushing duplicate states
            if (_undoHistory.Count > 0)
            {
                var lastState = _undoHistory[_undoHistory.Count - 1];
                if (AreStatesEqual(currentState, lastState))
                {
                    return;
                }
            }

            _undoHistory.Add(currentState);
            if (_undoHistory.Count > MaxHistorySteps)
            {
                _undoHistory.RemoveAt(0);
            }
        }

        private bool AreStatesEqual(EditorState a, EditorState b)
        {
            if (Math.Abs(a.Width - b.Width) > 0.001 || Math.Abs(a.Height - b.Height) > 0.001) return false;
            if (a.Strokes.Count != b.Strokes.Count) return false;

            if (a.CompressedImage == null && b.CompressedImage == null) return true;
            if (a.CompressedImage == null || b.CompressedImage == null) return false;
            if (a.CompressedImage.Length != b.CompressedImage.Length) return false;

            // Fast checksum comparison (first 100 bytes)
            int checkLength = Math.Min(100, a.CompressedImage.Length);
            for (int i = 0; i < checkLength; i++)
            {
                if (a.CompressedImage[i] != b.CompressedImage[i]) return false;
            }

            return true;
        }

        private void Undo()
        {
            if (_undoHistory.Count > 0)
            {
                int lastIndex = _undoHistory.Count - 1;
                var previousState = _undoHistory[lastIndex];
                _undoHistory.RemoveAt(lastIndex);

                RestoreState(previousState);
            }
        }

        private void MainGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ScreenshotImage.Source != null)
            {
                PushStateToHistory();
            }
        }

        // --- CLIPBOARD & EDITING ---

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                PasteImage();
                e.Handled = true;
            }
            else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Undo();
                e.Handled = true;
            }
        }

        private void BtnPaste_Click(object sender, RoutedEventArgs e)
        {
            PasteImage();
        }

        private void PasteImage()
        {
            try
            {
                var source = GetImageFromClipboardWithRetry();
                if (source != null)
                {
                    PushStateToHistory();
                    LoadImageIntoUI(source);
                }
                else
                {
                    MessageBox.Show("Буфер обмена не содержит изображения.", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось вставить изображение: " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private BitmapSource? GetImageFromClipboardWithRetry()
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    // 1. Try modern screenshot data format first (lossless PNG)
                    string[] pngFormats = { "PNG", "Portable Network Graphic" };
                    foreach (var format in pngFormats)
                    {
                        if (Clipboard.ContainsData(format))
                        {
                            var data = Clipboard.GetData(format);
                            if (data is MemoryStream stream)
                            {
                                var bitmap = new BitmapImage();
                                bitmap.BeginInit();
                                bitmap.StreamSource = stream;
                                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                bitmap.EndInit();
                                bitmap.Freeze();
                                return bitmap;
                            }
                        }
                    }

                    // 2. Fallback to default clipboard bitmap
                    if (Clipboard.ContainsImage())
                    {
                        var source = Clipboard.GetImage();
                        if (source != null)
                        {
                            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                            converted.Freeze();
                            return converted;
                        }
                    }
                }
                catch (COMException)
                {
                    System.Threading.Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Clipboard parse error: " + ex.Message);
                }
            }
            return null;
        }

        private void LoadImageIntoUI(BitmapSource source)
        {
            ScreenshotImage.Source = source;

            MainGrid.Width = source.Width;
            MainGrid.Height = source.Height;
            DrawingCanvas.Width = source.Width;
            DrawingCanvas.Height = source.Height;
            ShapesCanvas.Width = source.Width;
            ShapesCanvas.Height = source.Height;
            CropCanvas.Width = source.Width;
            CropCanvas.Height = source.Height;

            ClearDrawings();
            ApplyZoom(1.0); // Reset zoom to native on load

            PlaceholderGrid.Visibility = Visibility.Collapsed;
            this.Focus();
        }

        private string GetImageHash(BitmapSource image)
        {
            int width = image.PixelWidth;
            int height = image.PixelHeight;
            int bytesPerPixel = (image.Format.BitsPerPixel + 7) / 8;
            
            try
            {
                int sourceY = height / 2;
                Int32Rect rect = new Int32Rect(0, sourceY, width, Math.Min(height - sourceY, 2));
                int bufferSize = rect.Width * rect.Height * bytesPerPixel;
                byte[] buffer = new byte[bufferSize];
                image.CopyPixels(rect, buffer, rect.Width * bytesPerPixel, 0);

                long hash = 17;
                foreach (byte b in buffer)
                {
                    hash = hash * 31 + b;
                }
                return $"{width}x{height}_{hash}";
            }
            catch
            {
                return $"{width}x{height}";
            }
        }

        private void CheckClipboardAndAutoSave()
        {
            if (!_config.AutoSaveEnabled) return;

            var image = GetImageFromClipboardWithRetry();
            if (image == null) return;

            string currentHash = GetImageHash(image);
            if (currentHash == _lastImageHash) return;

            _lastImageHash = currentHash;

            PushStateToHistory();
            LoadImageIntoUI(image);
            AutoSaveImage(image);

            // If the app is in the background, trim memory immediately after auto-save is completed
            if (this.Visibility != Visibility.Visible || this.WindowState == WindowState.Minimized)
            {
                MinimizeMemory();
            }
        }

        private void AutoSaveImage(BitmapSource source)
        {
            try
            {
                _config.EnsureFolderExists();
                string fileName = "Screenshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                string filePath = Path.Combine(_config.SaveFolder, fileName);

                BitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    encoder.Save(stream);
                }

                if (_notifyIcon != null)
                {
                    _notifyIcon.ShowBalloonTip(3000, "Скриншот автосохранен", fileName, System.Windows.Forms.ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("AutoSave error: " + ex.Message);
            }
        }

        private Color GetCurrentColor()
        {
            if (RbColorGreen?.IsChecked == true) return Color.FromRgb(52, 199, 89);
            if (RbColorBlue?.IsChecked == true) return Color.FromRgb(0, 122, 255);
            if (RbColorYellow?.IsChecked == true) return Color.FromRgb(255, 204, 0);
            return Color.FromRgb(255, 59, 48); // Red
        }

        private void Color_Checked(object sender, RoutedEventArgs e)
        {
            if (IsLoaded)
                UpdateToolMode();
        }

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (IsLoaded)
                UpdateToolMode();
        }

        private void SliderBrushSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (IsLoaded)
                UpdateToolMode();
        }

        private void UpdateToolMode()
        {
            if (RbPen == null || DrawingCanvas == null || ShapesCanvas == null || CropCanvas == null) return;

            DrawingCanvas.IsHitTestVisible = false;
            ShapesCanvas.IsHitTestVisible = false;
            CropCanvas.Visibility = Visibility.Collapsed;

            var color = GetCurrentColor();

            if (RbPen.IsChecked == true)
            {
                DrawingCanvas.IsHitTestVisible = true;
                DrawingCanvas.EditingMode = InkCanvasEditingMode.Ink;
                DrawingCanvas.DefaultDrawingAttributes.Color = color;
                DrawingCanvas.DefaultDrawingAttributes.Width = SliderBrushSize.Value;
                DrawingCanvas.DefaultDrawingAttributes.Height = SliderBrushSize.Value;
            }
            else if (RbEraser.IsChecked == true)
            {
                DrawingCanvas.IsHitTestVisible = true;
                DrawingCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
            }
            else if (RbRectangle.IsChecked == true)
            {
                ShapesCanvas.IsHitTestVisible = true;
            }
            else if (RbCrop.IsChecked == true)
            {
                CropCanvas.Visibility = Visibility.Visible;
                UpdateCropOverlay(new Rect(0, 0, 0, 0));
            }
        }

        private void MainGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ScreenshotImage.Source == null) return;

            _startPoint = e.GetPosition(MainGrid);

            if (RbRectangle.IsChecked == true)
            {
                _isDrawingShape = true;
                _currentShape = new Rectangle
                {
                    Stroke = new SolidColorBrush(GetCurrentColor()),
                    StrokeThickness = SliderBrushSize.Value / 2 + 1.5
                };
                Canvas.SetLeft(_currentShape, _startPoint.X);
                Canvas.SetTop(_currentShape, _startPoint.Y);
                ShapesCanvas.Children.Add(_currentShape);
                MainGrid.CaptureMouse();
            }
            else if (RbCrop.IsChecked == true)
            {
                _isCropping = true;
                MainGrid.CaptureMouse();
                UpdateCropOverlay(new Rect(_startPoint, _startPoint));
            }
        }

        private void MainGrid_MouseMove(object sender, MouseEventArgs e)
        {
            if (ScreenshotImage.Source == null) return;

            var currentPoint = e.GetPosition(MainGrid);

            if (_isDrawingShape && _currentShape != null)
            {
                var x = Math.Min(currentPoint.X, _startPoint.X);
                var y = Math.Min(currentPoint.Y, _startPoint.Y);
                var width = Math.Max(currentPoint.X, _startPoint.X) - x;
                var height = Math.Max(currentPoint.Y, _startPoint.Y) - y;

                Canvas.SetLeft(_currentShape, x);
                Canvas.SetTop(_currentShape, y);
                _currentShape.Width = width;
                _currentShape.Height = height;
            }
            else if (_isCropping)
            {
                var x = Math.Min(currentPoint.X, _startPoint.X);
                var y = Math.Min(currentPoint.Y, _startPoint.Y);
                var width = Math.Max(currentPoint.X, _startPoint.X) - x;
                var height = Math.Max(currentPoint.Y, _startPoint.Y) - y;

                UpdateCropOverlay(new Rect(x, y, width, height));
            }
        }

        private void MainGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDrawingShape)
            {
                _isDrawingShape = false;
                if (_currentShape != null)
                {
                    double x = Canvas.GetLeft(_currentShape);
                    double y = Canvas.GetTop(_currentShape);
                    double width = _currentShape.Width;
                    double height = _currentShape.Height;

                    if (width > 0 && height > 0)
                    {
                        var pts = new StylusPointCollection(new[] {
                            new StylusPoint(x, y),
                            new StylusPoint(x + width, y),
                            new StylusPoint(x + width, y + height),
                            new StylusPoint(x, y + height),
                            new StylusPoint(x, y)
                        });
                        var drawingAttributes = new DrawingAttributes
                        {
                            Color = GetCurrentColor(),
                            Width = SliderBrushSize.Value,
                            Height = SliderBrushSize.Value,
                            FitToCurve = false
                        };
                        DrawingCanvas.Strokes.Add(new Stroke(pts, drawingAttributes));
                    }
                    ShapesCanvas.Children.Remove(_currentShape);
                }
                _currentShape = null;
                MainGrid.ReleaseMouseCapture();
            }
            else if (_isCropping)
            {
                _isCropping = false;
                MainGrid.ReleaseMouseCapture();

                var currentPoint = e.GetPosition(MainGrid);
                var x = Math.Min(currentPoint.X, _startPoint.X);
                var y = Math.Min(currentPoint.Y, _startPoint.Y);
                var width = Math.Max(currentPoint.X, _startPoint.X) - x;
                var height = Math.Max(currentPoint.Y, _startPoint.Y) - y;

                if (width > 10 && height > 10)
                {
                    PerformCrop(new Rect(x, y, width, height));
                }

                RbPen.IsChecked = true;
            }
        }

        private void UpdateCropOverlay(Rect rect)
        {
            if (MainGrid.Width == 0 || MainGrid.Height == 0 || double.IsNaN(MainGrid.Width) || double.IsNaN(MainGrid.Height))
                return;

            CropSelectionRect.Width = rect.Width;
            CropSelectionRect.Height = rect.Height;
            Canvas.SetLeft(CropSelectionRect, rect.X);
            Canvas.SetTop(CropSelectionRect, rect.Y);

            var geometryGroup = new GeometryGroup();
            geometryGroup.Children.Add(new RectangleGeometry(new Rect(0, 0, MainGrid.Width, MainGrid.Height)));
            geometryGroup.Children.Add(new RectangleGeometry(rect));

            var pathGeometry = PathGeometry.CreateFromGeometry(geometryGroup);
            pathGeometry.FillRule = FillRule.EvenOdd;
            CropOverlay.Data = pathGeometry;
        }

        private RenderTargetBitmap RenderEditedImage()
        {
            var source = ScreenshotImage.Source as BitmapSource;
            if (source == null) throw new InvalidOperationException("No image loaded");

            var drawingVisual = new DrawingVisual();
            using (var drawingContext = drawingVisual.RenderOpen())
            {
                // Draw background image at native DIP scale
                drawingContext.DrawImage(source, new Rect(0, 0, source.Width, source.Height));

                // Draw drawings (strokes) at native DIP scale
                foreach (var stroke in DrawingCanvas.Strokes)
                {
                    stroke.Draw(drawingContext);
                }
            }

            var renderTarget = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, source.DpiX, source.DpiY, PixelFormats.Pbgra32);
            renderTarget.Render(drawingVisual);
            return renderTarget;
        }

        private void PerformCrop(Rect cropRectDips)
        {
            CropCanvas.Visibility = Visibility.Collapsed;

            try
            {
                var renderedSource = RenderEditedImage();

                double dpiScaleX = renderedSource.DpiX / 96.0;
                double dpiScaleY = renderedSource.DpiY / 96.0;

                int pixelX = (int)Math.Round(cropRectDips.X * dpiScaleX);
                int pixelY = (int)Math.Round(cropRectDips.Y * dpiScaleY);
                int pixelWidth = (int)Math.Round(cropRectDips.Width * dpiScaleX);
                int pixelHeight = (int)Math.Round(cropRectDips.Height * dpiScaleY);

                pixelX = Math.Max(0, pixelX);
                pixelY = Math.Max(0, pixelY);
                pixelWidth = Math.Min(pixelWidth, renderedSource.PixelWidth - pixelX);
                pixelHeight = Math.Min(pixelHeight, renderedSource.PixelHeight - pixelY);

                if (pixelWidth <= 0 || pixelHeight <= 0) return;

                var cropRectPixels = new Int32Rect(pixelX, pixelY, pixelWidth, pixelHeight);
                var croppedBitmap = new CroppedBitmap(renderedSource, cropRectPixels);

                PushStateToHistory(); // Save undo state before replacing current image
                LoadImageIntoUI(croppedBitmap);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Crop error: " + ex.Message);
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            PushStateToHistory();
            ClearDrawings();
            ScreenshotImage.Source = null;
            PlaceholderGrid.Visibility = Visibility.Visible;
            this.Focus();
        }

        private void ClearDrawings()
        {
            DrawingCanvas.Strokes.Clear();
            ShapesCanvas.Children.Clear();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ScreenshotImage.Source == null)
                {
                    MessageBox.Show("Нет изображения для сохранения.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _config.EnsureFolderExists();
                string fileName = "Screenshot_Edited_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                string filePath = Path.Combine(_config.SaveFolder, fileName);

                CropCanvas.Visibility = Visibility.Collapsed;
                RbPen.IsChecked = true;

                var renderTarget = RenderEditedImage();

                BitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(renderTarget));

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    encoder.Save(stream);
                }

                MessageBox.Show($"Отредактированное изображение сохранено!\n\nФайл:\n{fileName}", "Сохранено", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка при сохранении: " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                this.Focus();
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                if (e.ClickCount == 2)
                {
                    this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                }
                else
                {
                    this.DragMove();
                }
            }
        }

        private void BtnTitleMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void BtnTitleClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void CbAutoSave_Changed(object sender, RoutedEventArgs e)
        {
            if (_config == null) return;
            _config.AutoSaveEnabled = CbAutoSave.IsChecked == true;
            _config.Save();
        }

        private void CbRunAtStartup_Changed(object sender, RoutedEventArgs e)
        {
            if (_config == null) return;
            _config.RunAtStartup = CbRunAtStartup.IsChecked == true;
            _config.Save();
            StartupManager.SetEnabled(_config.RunAtStartup);
        }

        private void CbMinimizeToTray_Changed(object sender, RoutedEventArgs e)
        {
            if (_config == null) return;
            _config.MinimizeToTrayOnClose = CbMinimizeToTray.IsChecked == true;
            _config.Save();
        }

        private void BtnChooseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Выберите папку для автосохранения скриншотов",
                InitialDirectory = _config.SaveFolder
            };

            if (dialog.ShowDialog() == true)
            {
                _config.SaveFolder = dialog.FolderName;
                _config.Save();
                TxtSaveFolder.Text = _config.SaveFolder;
            }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            OpenSaveFolder();
        }

        // --- CANVAS ZOOM LOGIC ---
        private double _zoomLevel = 1.0;
        private const double ZoomStep = 0.1;
        private const double MinZoom = 0.2;
        private const double MaxZoom = 4.0;

        private void Workspace_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true; // Stop regular scrolling

                if (e.Delta > 0)
                {
                    ApplyZoom(Math.Min(MaxZoom, _zoomLevel + ZoomStep));
                }
                else
                {
                    ApplyZoom(Math.Max(MinZoom, _zoomLevel - ZoomStep));
                }
            }
        }

        private void BtnResetZoom_Click(object sender, RoutedEventArgs e)
        {
            ApplyZoom(1.0);
        }

        private void ApplyZoom(double scale)
        {
            _zoomLevel = scale;
            if (GridScale != null)
            {
                GridScale.ScaleX = _zoomLevel;
                GridScale.ScaleY = _zoomLevel;
            }
            TxtZoomPercent.Text = $"Масштаб: {Math.Round(_zoomLevel * 100)}%";
        }
    }
}