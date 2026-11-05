using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Inkveil
{
    public sealed class MainWindow : Window
    {
        private readonly ReaderSettings _settings;
        private readonly string _startupFile;
        private readonly List<FrameworkElement> _chromeElements = new List<FrameworkElement>();
        private readonly DispatcherTimer _visibilityTimer;
        private readonly DispatcherTimer _saveTimer;
        private readonly HashSet<string> _systemFontNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        private NovelBook _book;
        private int _chapterIndex;
        private IntPtr _windowHandle;
        private bool _isVisible = true;
        private bool _isChoosingFile;
        private bool _updatingChapterList;
        private bool _updatingSettingsPanel;
        private bool _lightText;
        private DateTime _initialRevealUntil;
        private DateTime _suppressHideUntil;
        private double _pendingScrollRatio;
        private bool _hasPendingScroll;

        private Grid _root;
        private Border _backgroundLayer;
        private Grid _emptyPanel;
        private TextBlock _fileLabel;
        private TextBlock _chapterButtonText;
        private TextBlock _statusText;
        private TextBlock _hintText;
        private TextBlock _emptyTitle;
        private TextBlock _emptySubtitle;
        private Button _openButton;
        private Button _previousButton;
        private Button _nextButton;
        private Button _chapterButton;
        private Button _moreButton;
        private FlowDocumentScrollViewer _reader;
        private ScrollViewer _readerScrollViewer;
        private Canvas _scrollIndicatorCanvas;
        private Border _scrollIndicatorThumb;
        private Popup _chapterPopup;
        private ListBox _chapterList;
        private Border _settingsPanel;
        private ComboBox _fontCombo;
        private Slider _backgroundOpacitySlider;
        private Slider _textOpacitySlider;
        private TextBlock _backgroundOpacityValue;
        private TextBlock _textOpacityValue;
        private CheckBox _topmostCheckBox;
        private CheckBox _autoHideCheckBox;
        private CheckBox _darkTextCheckBox;
        private SolidColorBrush _textBrush;
        private SolidColorBrush _hoverBrush;
        private Style _normalButtonStyle;
        private Style _dangerButtonStyle;

        public MainWindow(string[] args)
        {
            _settings = ReaderSettings.Load();
            NormalizeSettings();
            _startupFile = FindStartupFile(args);

            ConfigureWindow();
            BuildInterface();
            ApplyTheme();
            UpdateSettingsPanelControls();

            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            Closing += OnClosing;
            Closed += OnClosed;
            PreviewKeyDown += OnPreviewKeyDown;
            PreviewMouseWheel += OnWindowPreviewMouseWheel;
            DragOver += OnDragOver;
            Drop += OnDrop;
            SizeChanged += OnWindowSizeChanged;

            _visibilityTimer = new DispatcherTimer(DispatcherPriority.Background);
            _visibilityTimer.Interval = TimeSpan.FromMilliseconds(90);
            _visibilityTimer.Tick += OnVisibilityTick;

            _saveTimer = new DispatcherTimer(DispatcherPriority.Background);
            _saveTimer.Interval = TimeSpan.FromSeconds(1.1);
            _saveTimer.Tick += OnSaveTick;
        }

        private void ConfigureWindow()
        {
            Title = "墨隐";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowInTaskbar = true;
            ShowActivated = true;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            MinWidth = 620;
            MinHeight = 500;
            Topmost = _settings.Topmost;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

            Rect workArea = SystemParameters.WorkArea;
            double width = Clamp(_settings.Width, MinWidth, Math.Max(MinWidth, workArea.Width - 40));
            double height = Clamp(_settings.Height, MinHeight, Math.Max(MinHeight, workArea.Height - 40));
            Width = width;
            Height = height;

            if (_settings.HasWindowBounds && IsUsableBounds(_settings.Left, _settings.Top, width, height))
            {
                Left = _settings.Left;
                Top = _settings.Top;
            }
            else
            {
                Left = workArea.Left + (workArea.Width - width) / 2;
                Top = workArea.Top + (workArea.Height - height) / 2;
            }
        }

        private void NormalizeSettings()
        {
            if (string.IsNullOrWhiteSpace(_settings.FontFamilyName))
            {
                _settings.FontFamilyName = "Microsoft YaHei UI";
            }
            if (string.IsNullOrWhiteSpace(_settings.TextTheme))
            {
                _settings.TextTheme = "light";
            }
            _settings.FontSize = Clamp(_settings.FontSize, 15, 42);
            _settings.BackgroundOpacity = Clamp(_settings.BackgroundOpacity, 0, 100);
            _settings.TextOpacity = _settings.TextOpacity <= 0
                ? 100
                : Clamp(_settings.TextOpacity, 10, 100);
        }

        private void BuildInterface()
        {
            _root = new Grid { Background = Brushes.Transparent, AllowDrop = true };
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = _root;

            _backgroundLayer = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(18),
                IsHitTestVisible = false
            };
            Grid.SetRowSpan(_backgroundLayer, 3);
            Panel.SetZIndex(_backgroundLayer, -100);
            _root.Children.Add(_backgroundLayer);

            BuildButtonStyles();
            BuildTopBar();
            BuildReadingArea();
            BuildBottomBar();
            BuildChapterPopup();
            BuildSettingsPanel();

            var resizeGrip = new ResizeGrip
            {
                Width = 18,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 2, 2),
                Opacity = 0.45,
                IsTabStop = false
            };
            Panel.SetZIndex(resizeGrip, 100);
            _root.Children.Add(resizeGrip);
        }

        private void BuildTopBar()
        {
            var topBar = new Grid
            {
                Height = 42,
                Margin = new Thickness(16, 12, 16, 0),
                Background = Brushes.Transparent
            };
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topBar.MouseLeftButtonDown += OnDragAreaMouseDown;
            Grid.SetRow(topBar, 0);
            _root.Children.Add(topBar);

            var brand = new TextBlock
            {
                Text = "墨隐",
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                Cursor = Cursors.SizeAll
            };
            brand.MouseLeftButtonDown += OnDragAreaMouseDown;
            RegisterChrome(brand);
            Grid.SetColumn(brand, 0);
            topBar.Children.Add(brand);

            _fileLabel = new TextBlock
            {
                Text = "尚未打开小说",
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 11.5,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Cursor = Cursors.SizeAll
            };
            _fileLabel.MouseLeftButtonDown += OnDragAreaMouseDown;
            RegisterChrome(_fileLabel);
            Grid.SetColumn(_fileLabel, 1);
            topBar.Children.Add(_fileLabel);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(actions, 2);
            topBar.Children.Add(actions);

            _openButton = CreateButton("打开", "打开小说 (Ctrl+O)", 40, 12);
            _openButton.Click += delegate { OpenNovel(); };
            AddAction(actions, _openButton, 4);

            _previousButton = CreateButton("‹", "上一章 (Alt+←)", 27, 18);
            _previousButton.Click += delegate { GoToChapter(_chapterIndex - 1); };
            AddAction(actions, _previousButton, 2);

            _chapterButton = CreateButton(string.Empty, "选择章节", 166, 12);
            _chapterButton.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            _chapterButton.Padding = new Thickness(9, 4, 9, 4);
            _chapterButton.Click += OnChapterButtonClick;
            _chapterButtonText = new TextBlock
            {
                Text = "章节",
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 12
            };
            _chapterButton.Content = _chapterButtonText;
            AddAction(actions, _chapterButton, 2);

            _nextButton = CreateButton("›", "下一章 (Alt+→)", 27, 18);
            _nextButton.Click += delegate { GoToChapter(_chapterIndex + 1); };
            AddAction(actions, _nextButton, 2);

            Button smallerFontButton = CreateButton("A−", "缩小字号 (Ctrl+-)", 29, 11.5);
            smallerFontButton.Click += delegate { ChangeFontSize(-1); };
            AddAction(actions, smallerFontButton, 8);

            Button largerFontButton = CreateButton("A+", "放大字号 (Ctrl++)", 29, 11.5);
            largerFontButton.Click += delegate { ChangeFontSize(1); };
            AddAction(actions, largerFontButton, 2);

            _moreButton = CreateButton("···", "显示设置", 31, 14);
            _moreButton.Click += OnSettingsButtonClick;
            AddAction(actions, _moreButton, 8);

            Button minimizeButton = CreateButton("—", "最小化", 28, 12);
            minimizeButton.Click += delegate { WindowState = WindowState.Minimized; };
            AddAction(actions, minimizeButton, 2);

            Button closeButton = CreateButton("×", "退出", 28, 17, true);
            closeButton.Click += delegate { Close(); };
            AddAction(actions, closeButton, 2);
        }

        private void BuildReadingArea()
        {
            var contentGrid = new Grid
            {
                Margin = new Thickness(22, 4, 10, 0),
                Background = Brushes.Transparent
            };
            Grid.SetRow(contentGrid, 1);
            _root.Children.Add(contentGrid);

            _reader = new FlowDocumentScrollViewer
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                MaxWidth = 920,
                IsToolBarVisible = false,
                IsSelectionEnabled = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Visibility = Visibility.Collapsed
            };
            TextOptions.SetTextFormattingMode(_reader, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(_reader, TextRenderingMode.Grayscale);
            _reader.AddHandler(
                ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler(OnReaderScrollChanged));
            contentGrid.Children.Add(_reader);

            _scrollIndicatorCanvas = new Canvas
            {
                Width = 5,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 18, 1, 30),
                Background = Brushes.Transparent,
                IsHitTestVisible = false,
                ClipToBounds = true
            };
            Panel.SetZIndex(_scrollIndicatorCanvas, 20);
            contentGrid.Children.Add(_scrollIndicatorCanvas);

            _scrollIndicatorThumb = new Border
            {
                Width = 3,
                Background = MakeBrush("#66F7F1E6"),
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Right,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _scrollIndicatorCanvas.Children.Add(_scrollIndicatorThumb);

            _emptyPanel = new Grid
            {
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            _emptyPanel.MouseLeftButtonUp += delegate { OpenNovel(); };
            contentGrid.Children.Add(_emptyPanel);

            var emptyStack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 520
            };
            _emptyTitle = new TextBlock
            {
                Text = "把小说放进这片空白",
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 30,
                FontWeight = FontWeights.Light,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 18)
            };
            RegisterChrome(_emptyTitle);
            emptyStack.Children.Add(_emptyTitle);

            var emptyRule = new Border
            {
                Width = 52,
                Height = 1,
                Background = MakeBrush("#88D8A45B"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 18)
            };
            emptyStack.Children.Add(emptyRule);

            _emptySubtitle = new TextBlock
            {
                Text = "拖入 TXT 文件，或按 Ctrl+O 选择\n鼠标离开后，窗口会完全隐入桌面",
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 14,
                LineHeight = 25,
                TextAlignment = TextAlignment.Center,
                Opacity = 0.78
            };
            RegisterChrome(_emptySubtitle);
            emptyStack.Children.Add(_emptySubtitle);

            _emptyPanel.Children.Add(emptyStack);
        }

        private void BuildBottomBar()
        {
            var bottomBar = new Grid
            {
                Height = 24,
                Margin = new Thickness(22, 0, 24, 12),
                Background = Brushes.Transparent
            };
            bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(bottomBar, 2);
            _root.Children.Add(bottomBar);

            _statusText = new TextBlock
            {
                Text = "尚未打开小说",
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 11,
                Opacity = 0.72,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            RegisterChrome(_statusText);
            Grid.SetColumn(_statusText, 0);
            bottomBar.Children.Add(_statusText);

            _hintText = new TextBlock
            {
                Text = "Alt+← / → 切章   ·   Ctrl+滚轮 字号   ·   鼠标移出即隐身",
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 10.5,
                Opacity = 0.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0)
            };
            RegisterChrome(_hintText);
            Grid.SetColumn(_hintText, 1);
            bottomBar.Children.Add(_hintText);
        }

        private void BuildChapterPopup()
        {
            _chapterList = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = MakeBrush("#FFF4EEE2"),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 13,
                MaxHeight = 390,
                Width = 374,
                Padding = new Thickness(0)
            };
            VirtualizingStackPanel.SetIsVirtualizing(_chapterList, true);
            VirtualizingStackPanel.SetVirtualizationMode(_chapterList, VirtualizationMode.Recycling);
            ScrollViewer.SetVerticalScrollBarVisibility(_chapterList, ScrollBarVisibility.Hidden);
            ScrollViewer.SetHorizontalScrollBarVisibility(_chapterList, ScrollBarVisibility.Disabled);
            _chapterList.ItemContainerStyle = CreateChapterItemStyle();
            _chapterList.SelectionChanged += OnChapterListSelectionChanged;

            var border = new Border
            {
                Background = MakeBrush("#F21B1917"),
                BorderBrush = MakeBrush("#36FFFFFF"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(13),
                Padding = new Thickness(7),
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 22,
                    ShadowDepth = 7,
                    Direction = 270,
                    Opacity = 0.55
                },
                Child = _chapterList
            };

            _chapterPopup = new Popup
            {
                PlacementTarget = _chapterButton,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 7,
                HorizontalOffset = -105,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                StaysOpen = false,
                Child = border
            };
            _chapterPopup.Opened += delegate
            {
                SelectCurrentChapterInList();
            };
            _chapterPopup.Closed += delegate
            {
                _suppressHideUntil = DateTime.UtcNow.AddMilliseconds(450);
            };
        }

        private void BuildSettingsPanel()
        {
            var content = new StackPanel();

            var header = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(header);

            TextBlock title = CreatePanelText("显示设置", 15, FontWeights.SemiBold);
            title.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(title, 0);
            header.Children.Add(title);

            Button closeButton = CreatePanelButton("×", "关闭设置", 28, 15);
            closeButton.Click += delegate { _settingsPanel.Visibility = Visibility.Collapsed; };
            Grid.SetColumn(closeButton, 1);
            header.Children.Add(closeButton);

            content.Children.Add(CreatePanelText("背景不透明度", 12, FontWeights.Normal));
            content.Children.Add(CreateOpacityRow(
                out _backgroundOpacitySlider,
                out _backgroundOpacityValue,
                0,
                100,
                _settings.BackgroundOpacity,
                OnBackgroundOpacityChanged));

            content.Children.Add(CreatePanelSpacer(13));
            content.Children.Add(CreatePanelText("文字不透明度", 12, FontWeights.Normal));
            content.Children.Add(CreateOpacityRow(
                out _textOpacitySlider,
                out _textOpacityValue,
                10,
                100,
                _settings.TextOpacity,
                OnTextOpacityChanged));

            content.Children.Add(CreatePanelSpacer(14));
            content.Children.Add(CreatePanelText("系统字体", 12, FontWeights.Normal));

            _fontCombo = new ComboBox
            {
                Height = 36,
                Margin = new Thickness(0, 6, 0, 0),
                Background = MakeBrush("#FF2A2621"),
                Foreground = MakeBrush("#FFF4EEE2"),
                BorderBrush = MakeBrush("#66D8A45B"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 13,
                Padding = new Thickness(8, 0, 4, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                MaxDropDownHeight = 330,
                IsTextSearchEnabled = true
            };
            _fontCombo.Resources[SystemColors.WindowBrushKey] = MakeBrush("#FF1C1916");
            _fontCombo.Resources[SystemColors.WindowTextBrushKey] = MakeBrush("#FFF4EEE2");
            _fontCombo.Resources[SystemColors.ControlBrushKey] = MakeBrush("#FF2A2621");
            _fontCombo.Resources[SystemColors.ControlTextBrushKey] = MakeBrush("#FFF4EEE2");
            _fontCombo.Resources[SystemColors.HighlightBrushKey] = MakeBrush("#66D8A45B");
            _fontCombo.Resources[SystemColors.HighlightTextBrushKey] = MakeBrush("#FFFFFFFF");
            _fontCombo.ItemContainerStyle = CreateComboBoxItemStyle();
            _fontCombo.Template = CreateFontComboBoxTemplate();
            PopulateSystemFonts();
            _fontCombo.SelectionChanged += OnFontSelectionChanged;
            _fontCombo.DropDownOpened += delegate
            {
                _suppressHideUntil = DateTime.UtcNow.AddSeconds(30);
            };
            _fontCombo.DropDownClosed += delegate
            {
                _suppressHideUntil = DateTime.UtcNow.AddMilliseconds(450);
            };
            content.Children.Add(_fontCombo);

            content.Children.Add(CreatePanelSpacer(15));
            _topmostCheckBox = CreatePanelCheckBox("窗口置顶");
            _topmostCheckBox.Click += delegate { ToggleTopmost(); };
            content.Children.Add(_topmostCheckBox);

            _autoHideCheckBox = CreatePanelCheckBox("鼠标离开时隐藏");
            _autoHideCheckBox.Click += delegate { ToggleAutoHide(); };
            content.Children.Add(_autoHideCheckBox);

            _darkTextCheckBox = CreatePanelCheckBox("使用深色文字");
            _darkTextCheckBox.Click += delegate
            {
                _settings.TextTheme = _darkTextCheckBox.IsChecked == true ? "dark" : "light";
                ApplyTheme();
                QueueSave();
                UpdateSettingsPanelControls();
            };
            content.Children.Add(_darkTextCheckBox);

            content.Children.Add(CreatePanelSpacer(13));
            content.Children.Add(new Border
            {
                Height = 1,
                Background = MakeBrush("#28FFFFFF"),
                Margin = new Thickness(0, 0, 0, 13)
            });

            Button exitButton = CreatePanelButton("退出墨隐", "关闭程序", double.NaN, 12);
            exitButton.Height = 31;
            exitButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            exitButton.Click += delegate { Close(); };
            content.Children.Add(exitButton);

            _settingsPanel = new Border
            {
                Width = 344,
                Padding = new Thickness(18),
                Margin = new Thickness(0, 58, 18, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Background = MakeBrush("#F5211E1A"),
                BorderBrush = MakeBrush("#3DFFFFFF"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(15),
                Child = content,
                Visibility = Visibility.Collapsed,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 24,
                    ShadowDepth = 7,
                    Direction = 270,
                    Opacity = 0.55,
                    RenderingBias = RenderingBias.Performance
                }
            };
            Grid.SetRowSpan(_settingsPanel, 3);
            Panel.SetZIndex(_settingsPanel, 90);
            _root.Children.Add(_settingsPanel);

            UpdateSettingsPanelControls();
        }

        private void BuildButtonStyles()
        {
            _hoverBrush = MakeBrush("#38D8A45B");
            _normalButtonStyle = CreateButtonStyle(_hoverBrush);
            _dangerButtonStyle = CreateButtonStyle(MakeBrush("#66C84A43"));
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _windowHandle = new WindowInteropHelper(this).Handle;
            NativeMethods.SetClickThrough(_windowHandle, false);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _initialRevealUntil = DateTime.UtcNow.AddMilliseconds(1100);
            _visibilityTimer.Start();
            _saveTimer.Start();

            string pathToOpen = _startupFile;
            if (string.IsNullOrEmpty(pathToOpen)
                && !string.IsNullOrEmpty(_settings.LastBookPath)
                && File.Exists(_settings.LastBookPath))
            {
                pathToOpen = _settings.LastBookPath;
            }

            if (!string.IsNullOrEmpty(pathToOpen) && File.Exists(pathToOpen))
            {
                int preferredIndex = string.Equals(pathToOpen, _settings.LastBookPath, StringComparison.OrdinalIgnoreCase)
                    ? _settings.ChapterIndex
                    : 0;
                LoadBook(pathToOpen, preferredIndex);
            }
            else
            {
                ShowEmptyState();
            }
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveSettings();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _visibilityTimer.Stop();
            _saveTimer.Stop();
            if (_chapterPopup != null)
            {
                _chapterPopup.IsOpen = false;
            }
            if (_settingsPanel != null)
            {
                _settingsPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void OnDragAreaMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                    // 系统窗口状态切换瞬间可能无法拖动，忽略即可。
                }
                e.Handled = true;
            }
        }

        private void OnChapterButtonClick(object sender, RoutedEventArgs e)
        {
            if (_book == null || _book.Chapters.Count == 0)
            {
                OpenNovel();
                return;
            }

            _chapterPopup.IsOpen = !_chapterPopup.IsOpen;
            if (_chapterPopup.IsOpen)
            {
                Dispatcher.BeginInvoke(
                    new Action(ScrollCurrentChapterIntoView),
                    DispatcherPriority.Background);
            }
        }

        private void OnSettingsButtonClick(object sender, RoutedEventArgs e)
        {
            if (_settingsPanel.Visibility == Visibility.Visible)
            {
                _settingsPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                UpdateSettingsPanelControls();
                _settingsPanel.Visibility = Visibility.Visible;
            }
        }

        private void OnVisibilityTick(object sender, EventArgs e)
        {
            if (!IsLoaded || WindowState == WindowState.Minimized || _windowHandle == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.Point cursor;
            NativeMethods.Rect bounds;
            if (!NativeMethods.TryGetCursorPosition(out cursor)
                || !NativeMethods.TryGetWindowRect(_windowHandle, out bounds))
            {
                return;
            }

            bool inside = cursor.X >= bounds.Left
                && cursor.X < bounds.Right
                && cursor.Y >= bounds.Top
                && cursor.Y < bounds.Bottom;

            if (!_settings.AutoHide)
            {
                if (!_isVisible)
                {
                    RevealWindow();
                }
                return;
            }

            if (inside)
            {
                if (!_isVisible)
                {
                    RevealWindow();
                }
                return;
            }

            bool interactionOpen = IsPopupInteractionOpen();
            if (_isVisible
                && !interactionOpen
                && !_isChoosingFile
                && DateTime.UtcNow >= _initialRevealUntil
                && DateTime.UtcNow >= _suppressHideUntil)
            {
                HideWindow();
            }
        }

        private bool IsPopupInteractionOpen()
        {
            return (_chapterPopup != null && _chapterPopup.IsOpen)
                || (_fontCombo != null && _fontCombo.IsDropDownOpen);
        }

        private void RevealWindow()
        {
            _isVisible = true;
            NativeMethods.SetClickThrough(_windowHandle, false);

            BeginAnimation(OpacityProperty, null);
            if (Opacity < 0.01)
            {
                Opacity = 0;
            }

            var animation = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(170),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private void HideWindow()
        {
            if (!_isVisible)
            {
                return;
            }

            _isVisible = false;
            if (_chapterPopup != null)
            {
                _chapterPopup.IsOpen = false;
            }
            if (_settingsPanel != null)
            {
                _settingsPanel.Visibility = Visibility.Collapsed;
            }

            BeginAnimation(OpacityProperty, null);
            var animation = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(240),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            animation.Completed += delegate
            {
                if (!_isVisible)
                {
                    Opacity = 0;
                    NativeMethods.SetClickThrough(_windowHandle, true);
                }
            };
            BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = GetDroppedTextFile(e.Data) != null
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            string path = GetDroppedTextFile(e.Data);
            if (!string.IsNullOrEmpty(path))
            {
                LoadBook(path, 0);
            }
            e.Handled = true;
        }

        private string GetDroppedTextFile(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop))
            {
                return null;
            }

            string[] files = data.GetData(DataFormats.FileDrop) as string[];
            if (files == null)
            {
                return null;
            }

            foreach (string file in files)
            {
                if (File.Exists(file)
                    && (string.Equals(Path.GetExtension(file), ".txt", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetExtension(file), ".md", StringComparison.OrdinalIgnoreCase)))
                {
                    return file;
                }
            }
            return null;
        }

        private void OpenNovel()
        {
            _isChoosingFile = true;
            RevealWindow();

            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "选择 TXT 小说",
                    Filter = "文本小说 (*.txt;*.md)|*.txt;*.md|所有文件 (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false
                };
                bool? result = dialog.ShowDialog(this);
                if (result == true)
                {
                    LoadBook(dialog.FileName, 0);
                }
            }
            catch (Exception ex)
            {
                ShowError("无法打开文件", ex.Message);
            }
            finally
            {
                _isChoosingFile = false;
                _suppressHideUntil = DateTime.UtcNow.AddMilliseconds(500);
            }
        }

        private void LoadBook(string path, int preferredIndex)
        {
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                NovelBook loaded = NovelBook.Load(path);
                _book = loaded;
                _chapterIndex = Clamp(preferredIndex, 0, Math.Max(0, loaded.Chapters.Count - 1));
                _fileLabel.Text = string.Format("{0}   {1} 章", loaded.Title, loaded.Chapters.Count);
                _emptyPanel.Visibility = Visibility.Collapsed;
                _reader.Visibility = Visibility.Visible;
                PopulateChapterList();
                RenderChapter(false);
                RevealWindow();
                _suppressHideUntil = DateTime.UtcNow.AddMilliseconds(700);
                QueueSave();
            }
            catch (Exception ex)
            {
                ShowError("读取小说失败", ex.Message);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void ShowEmptyState()
        {
            _book = null;
            _chapterIndex = 0;
            _reader.Visibility = Visibility.Collapsed;
            _emptyPanel.Visibility = Visibility.Visible;
            if (_scrollIndicatorThumb != null)
            {
                _scrollIndicatorThumb.Visibility = Visibility.Collapsed;
            }
            _fileLabel.Text = "尚未打开小说";
            _statusText.Text = "拖入 TXT 小说，或按 Ctrl+O 打开";
            _chapterButtonText.Text = "章节";
            _chapterList.ItemsSource = null;
            UpdateNavigationState();
        }

        private void PopulateChapterList()
        {
            var options = new List<ChapterOption>();
            if (_book != null)
            {
                for (int i = 0; i < _book.Chapters.Count; i++)
                {
                    options.Add(new ChapterOption(i, _book.Chapters[i].Title));
                }
            }
            _chapterList.ItemsSource = options;
            SelectCurrentChapterInList();
        }

        private void SelectCurrentChapterInList()
        {
            if (_book == null || _chapterList.Items.Count == 0)
            {
                return;
            }

            _updatingChapterList = true;
            _chapterList.SelectedIndex = _chapterIndex;
            _updatingChapterList = false;
        }

        private void ScrollCurrentChapterIntoView()
        {
            if (_book == null || _chapterIndex < 0 || _chapterIndex >= _chapterList.Items.Count)
            {
                return;
            }

            object item = _chapterList.Items[_chapterIndex];
            _chapterList.ScrollIntoView(item);
            var container = _chapterList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
            if (container != null)
            {
                container.BringIntoView();
            }
        }

        private void OnChapterListSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingChapterList || _chapterList.SelectedIndex < 0)
            {
                return;
            }

            int selected = _chapterList.SelectedIndex;
            _chapterPopup.IsOpen = false;
            GoToChapter(selected);
        }

        private void GoToChapter(int index)
        {
            if (_book == null || _book.Chapters.Count == 0)
            {
                return;
            }

            int target = Clamp(index, 0, _book.Chapters.Count - 1);
            if (target == _chapterIndex)
            {
                return;
            }

            _chapterIndex = target;
            RenderChapter(false);
            _suppressHideUntil = DateTime.UtcNow.AddMilliseconds(400);
            QueueSave();
        }

        private void RenderChapter(bool preservePosition)
        {
            if (_book == null || _book.Chapters.Count == 0)
            {
                return;
            }

            _chapterIndex = Clamp(_chapterIndex, 0, _book.Chapters.Count - 1);
            NovelChapter chapter = _book.Chapters[_chapterIndex];
            double ratio = preservePosition ? GetScrollRatio() : 0;
            double fontSize = Clamp(_settings.FontSize, 15, 42);
            FontFamily bodyFont = GetBodyFontFamily();

            var document = new FlowDocument
            {
                PagePadding = new Thickness(18, 10, 26, 34),
                ColumnWidth = 10000,
                TextAlignment = TextAlignment.Justify,
                FontFamily = bodyFont,
                FontSize = fontSize,
                Foreground = _textBrush
            };

            var title = new Paragraph(new Run(chapter.Title))
            {
                FontFamily = bodyFont,
                FontSize = fontSize * 1.28,
                FontWeight = FontWeights.SemiBold,
                TextIndent = 0,
                TextAlignment = TextAlignment.Left,
                Margin = new Thickness(0, 0, 0, fontSize * 1.25),
                LineHeight = fontSize * 1.55
            };
            document.Blocks.Add(title);

            string[] paragraphs = chapter.Text.Split('\n');
            bool hasContent = false;
            foreach (string rawParagraph in paragraphs)
            {
                string text = rawParagraph.Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                var paragraph = new Paragraph(new Run(text))
                {
                    FontFamily = bodyFont,
                    FontSize = fontSize,
                    Foreground = _textBrush,
                    TextIndent = fontSize * 2,
                    LineHeight = fontSize * 1.82,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Margin = new Thickness(0, 0, 0, fontSize * 0.35)
                };
                document.Blocks.Add(paragraph);
                hasContent = true;
            }

            if (!hasContent)
            {
                document.Blocks.Add(new Paragraph(new Run("本章暂无内容。"))
                {
                    FontFamily = bodyFont,
                    FontSize = fontSize,
                    Foreground = _textBrush,
                    TextIndent = 0,
                    LineHeight = fontSize * 1.82
                });
            }

            _reader.Document = document;
            _chapterButtonText.Text = chapter.Title;
            _reader.Effect = CreateReaderShadow();
            _pendingScrollRatio = ratio;
            _hasPendingScroll = true;
            UpdateNavigationState();
            UpdateReadingStatus();

            Dispatcher.BeginInvoke(
                new Action(RestorePendingScrollPosition),
                DispatcherPriority.Loaded);
        }

        private void RestorePendingScrollPosition()
        {
            if (!_hasPendingScroll)
            {
                return;
            }

            _hasPendingScroll = false;
            ScrollViewer scrollViewer = GetReaderScrollViewer();
            if (scrollViewer == null)
            {
                return;
            }

            double maxOffset = Math.Max(0, scrollViewer.ExtentHeight - scrollViewer.ViewportHeight);
            scrollViewer.ScrollToVerticalOffset(maxOffset * Clamp(_pendingScrollRatio, 0, 1));
            UpdateReadingStatus();
        }

        private void OnReaderScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            UpdateReadingStatus();
            QueueSave();
        }

        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_book != null)
            {
                QueueSave();
                Dispatcher.BeginInvoke(
                    new Action(UpdateScrollIndicator),
                    DispatcherPriority.Background);
            }
        }

        private void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_settingsPanel != null
                && _settingsPanel.Visibility == Visibility.Visible
                && _settingsPanel.IsMouseOver)
            {
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                ChangeFontSize(e.Delta > 0 ? 1 : -1);
                e.Handled = true;
                return;
            }

            if (_book == null || _reader.Visibility != Visibility.Visible)
            {
                return;
            }

            ScrollViewer scrollViewer = GetReaderScrollViewer();
            if (scrollViewer == null)
            {
                return;
            }

            double maximum = Math.Max(0, scrollViewer.ExtentHeight - scrollViewer.ViewportHeight);
            double target = Clamp(scrollViewer.VerticalOffset - e.Delta, 0, maximum);
            scrollViewer.ScrollToVerticalOffset(target);
            e.Handled = true;
        }

        private void ChangeFontSize(double delta)
        {
            double next = Clamp(_settings.FontSize + delta, 15, 42);
            if (Math.Abs(next - _settings.FontSize) < 0.01)
            {
                return;
            }

            _settings.FontSize = next;
            if (_book != null)
            {
                RenderChapter(true);
            }
            QueueSave();
        }

        private void ToggleTopmost()
        {
            Topmost = !Topmost;
            _settings.Topmost = Topmost;
            UpdateSettingsPanelControls();
            QueueSave();
        }

        private void ToggleAutoHide()
        {
            _settings.AutoHide = !_settings.AutoHide;
            if (!_settings.AutoHide)
            {
                RevealWindow();
            }
            else
            {
                _suppressHideUntil = DateTime.UtcNow.AddMilliseconds(250);
            }
            UpdateSettingsPanelControls();
            QueueSave();
        }

        private void UpdateNavigationState()
        {
            bool hasBook = _book != null && _book.Chapters.Count > 0;
            _chapterButton.IsEnabled = hasBook;
            _previousButton.IsEnabled = hasBook && _chapterIndex > 0;
            _nextButton.IsEnabled = hasBook && _chapterIndex < _book.Chapters.Count - 1;
        }

        private void UpdateReadingStatus()
        {
            if (_book == null)
            {
                return;
            }

            int percent = (int)Math.Round(GetScrollRatio() * 100);
            _statusText.Text = string.Format(
                "第 {0} / {1} 章   ·   已读 {2}%",
                _chapterIndex + 1,
                _book.Chapters.Count,
                Clamp(percent, 0, 100));
            UpdateScrollIndicator();
        }

        private void UpdateScrollIndicator()
        {
            if (_scrollIndicatorCanvas == null || _scrollIndicatorThumb == null)
            {
                return;
            }

            ScrollViewer scrollViewer = GetReaderScrollViewer();
            if (_book == null
                || _reader.Visibility != Visibility.Visible
                || scrollViewer == null
                || _scrollIndicatorCanvas.ActualHeight <= 0
                || scrollViewer.ExtentHeight <= scrollViewer.ViewportHeight + 1)
            {
                _scrollIndicatorThumb.Visibility = Visibility.Collapsed;
                return;
            }

            double available = _scrollIndicatorCanvas.ActualHeight;
            double ratio = Clamp(scrollViewer.VerticalOffset
                / Math.Max(1, scrollViewer.ExtentHeight - scrollViewer.ViewportHeight), 0, 1);
            double thumbHeight = Math.Max(24, available * scrollViewer.ViewportHeight / scrollViewer.ExtentHeight);
            thumbHeight = Math.Min(available, thumbHeight);
            _scrollIndicatorThumb.Height = thumbHeight;
            Canvas.SetTop(_scrollIndicatorThumb, (available - thumbHeight) * ratio);
            Canvas.SetLeft(_scrollIndicatorThumb, 1);
            _scrollIndicatorThumb.Visibility = Visibility.Visible;
        }

        private double GetScrollRatio()
        {
            if (_reader == null)
            {
                return 0;
            }

            ScrollViewer scrollViewer = GetReaderScrollViewer();
            if (scrollViewer == null)
            {
                return 0;
            }

            double maximum = Math.Max(0, scrollViewer.ExtentHeight - scrollViewer.ViewportHeight);
            if (maximum <= 0.5)
            {
                return 1;
            }
            return Clamp(scrollViewer.VerticalOffset / maximum, 0, 1);
        }

        private ScrollViewer GetReaderScrollViewer()
        {
            if (_readerScrollViewer != null)
            {
                return _readerScrollViewer;
            }

            _readerScrollViewer = FindVisualChild<ScrollViewer>(_reader);
            return _readerScrollViewer;
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
            {
                return null;
            }

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                var match = child as T;
                if (match != null)
                {
                    return match;
                }

                T nested = FindVisualChild<T>(child);
                if (nested != null)
                {
                    return nested;
                }
            }
            return null;
        }

        private void ApplyTheme()
        {
            _lightText = !string.Equals(_settings.TextTheme, "dark", StringComparison.OrdinalIgnoreCase);
            ApplyTextAppearance(true);
            ApplyBackground();
        }

        private void ApplyTextAppearance(bool renderBook)
        {
            _lightText = !string.Equals(_settings.TextTheme, "dark", StringComparison.OrdinalIgnoreCase);
            Color baseColor = _lightText
                ? Color.FromRgb(0xF7, 0xF1, 0xE6)
                : Color.FromRgb(0x17, 0x13, 0x0F);
            byte alpha = (byte)Math.Round(Clamp(_settings.TextOpacity, 10, 100) * 2.55);
            _textBrush = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
            _textBrush.Freeze();
            if (_scrollIndicatorThumb != null)
            {
                _scrollIndicatorThumb.Background = MakeBrush(_lightText ? "#66F7F1E6" : "#6617130F");
            }

            foreach (FrameworkElement element in _chromeElements)
            {
                var textBlock = element as TextBlock;
                if (textBlock != null)
                {
                    textBlock.Foreground = _textBrush;
                    textBlock.Effect = CreateChromeShadow();
                    continue;
                }

                var control = element as Control;
                if (control != null)
                {
                    control.Foreground = _textBrush;
                    control.Effect = CreateChromeShadow();
                }
            }

            if (_reader != null)
            {
                _reader.Effect = CreateReaderShadow();
            }
            if (renderBook && _book != null)
            {
                RenderChapter(true);
            }
            else if (_reader != null && _reader.Document != null)
            {
                _reader.Document.Foreground = _textBrush;
                foreach (Block block in _reader.Document.Blocks)
                {
                    var paragraph = block as Paragraph;
                    if (paragraph != null)
                    {
                        paragraph.Foreground = _textBrush;
                    }
                }
            }
        }

        private void ApplyBackground()
        {
            if (_backgroundLayer == null)
            {
                return;
            }

            double opacity = Clamp(_settings.BackgroundOpacity, 0, 100);
            byte alpha = (byte)Math.Round(opacity * 2.55);
            Color baseColor = _lightText
                ? Color.FromRgb(0x18, 0x16, 0x13)
                : Color.FromRgb(0xF7, 0xF1, 0xE6);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
            brush.Freeze();
            _backgroundLayer.Background = brush;
        }

        private DropShadowEffect CreateChromeShadow()
        {
            double factor = Clamp(_settings.TextOpacity / 100.0, 0.1, 1.0);
            return new DropShadowEffect
            {
                Color = _lightText ? Colors.Black : Colors.White,
                BlurRadius = _lightText ? 5.5 : 6.5,
                ShadowDepth = _lightText ? 1.2 : 0.4,
                Direction = 315,
                Opacity = (_lightText ? 0.92 : 0.9) * factor,
                RenderingBias = RenderingBias.Performance
            };
        }

        private DropShadowEffect CreateReaderShadow()
        {
            double factor = Clamp(_settings.TextOpacity / 100.0, 0.1, 1.0);
            return new DropShadowEffect
            {
                Color = _lightText ? Colors.Black : Colors.White,
                BlurRadius = _lightText ? 7 : 8,
                ShadowDepth = _lightText ? 1.5 : 0.5,
                Direction = 315,
                Opacity = (_lightText ? 0.9 : 0.88) * factor,
                RenderingBias = RenderingBias.Performance
            };
        }

        private void OnBackgroundOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingSettingsPanel)
            {
                return;
            }

            _settings.BackgroundOpacity = Math.Round(e.NewValue);
            if (_backgroundOpacityValue != null)
            {
                _backgroundOpacityValue.Text = string.Format("{0:0}%", _settings.BackgroundOpacity);
            }
            ApplyBackground();
            QueueSave();
        }

        private void OnTextOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingSettingsPanel)
            {
                return;
            }

            _settings.TextOpacity = Math.Round(e.NewValue);
            if (_textOpacityValue != null)
            {
                _textOpacityValue.Text = string.Format("{0:0}%", _settings.TextOpacity);
            }
            ApplyTextAppearance(false);
            QueueSave();
        }

        private void OnFontSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingSettingsPanel)
            {
                return;
            }

            string fontName = _fontCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(fontName))
            {
                return;
            }

            _settings.FontFamilyName = fontName;
            _fontCombo.IsDropDownOpen = false;
            if (_book != null)
            {
                RenderChapter(true);
            }
            QueueSave();
        }

        private void PopulateSystemFonts()
        {
            var names = new List<string>();
            foreach (FontFamily family in Fonts.SystemFontFamilies)
            {
                string name = family.Source;
                if (!string.IsNullOrWhiteSpace(name) && _systemFontNames.Add(name))
                {
                    names.Add(name);
                }
            }

            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            foreach (string name in names)
            {
                _fontCombo.Items.Add(name);
            }

            if (!_systemFontNames.Contains(_settings.FontFamilyName))
            {
                _settings.FontFamilyName = _systemFontNames.Contains("Microsoft YaHei UI")
                    ? "Microsoft YaHei UI"
                    : (names.Count > 0 ? names[0] : "Microsoft YaHei UI");
            }
            SelectFontInCombo();
        }

        private void SelectFontInCombo()
        {
            if (_fontCombo == null)
            {
                return;
            }

            for (int i = 0; i < _fontCombo.Items.Count; i++)
            {
                string name = _fontCombo.Items[i] as string;
                if (string.Equals(name, _settings.FontFamilyName, StringComparison.CurrentCultureIgnoreCase))
                {
                    _fontCombo.SelectedIndex = i;
                    return;
                }
            }
        }

        private FontFamily GetBodyFontFamily()
        {
            string name = _settings.FontFamilyName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "Microsoft YaHei UI";
            }
            return new FontFamily(name);
        }

        private void UpdateSettingsPanelControls()
        {
            if (_backgroundOpacitySlider == null
                || _textOpacitySlider == null
                || _fontCombo == null)
            {
                return;
            }

            _updatingSettingsPanel = true;
            try
            {
                _backgroundOpacitySlider.Value = Clamp(_settings.BackgroundOpacity, 0, 100);
                _textOpacitySlider.Value = Clamp(_settings.TextOpacity, 10, 100);
                _backgroundOpacityValue.Text = string.Format("{0:0}%", _settings.BackgroundOpacity);
                _textOpacityValue.Text = string.Format("{0:0}%", _settings.TextOpacity);
                _topmostCheckBox.IsChecked = Topmost;
                _autoHideCheckBox.IsChecked = _settings.AutoHide;
                _darkTextCheckBox.IsChecked = !_lightText;
                SelectFontInCombo();
            }
            finally
            {
                _updatingSettingsPanel = false;
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

            if (e.Key == Key.Escape && _settingsPanel.Visibility == Visibility.Visible)
            {
                _settingsPanel.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }

            if (control && e.Key == Key.O)
            {
                OpenNovel();
                e.Handled = true;
                return;
            }

            if (control && (e.Key == Key.OemPlus || e.Key == Key.Add))
            {
                ChangeFontSize(1);
                e.Handled = true;
                return;
            }

            if (control && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
            {
                ChangeFontSize(-1);
                e.Handled = true;
                return;
            }

            if (control && e.Key == Key.T)
            {
                _settings.TextTheme = _lightText ? "dark" : "light";
                ApplyTheme();
                UpdateSettingsPanelControls();
                QueueSave();
                e.Handled = true;
                return;
            }

            if (control && e.Key == Key.P)
            {
                ToggleTopmost();
                e.Handled = true;
                return;
            }

            if (control && e.Key == Key.H)
            {
                ToggleAutoHide();
                e.Handled = true;
                return;
            }

            if (alt && e.Key == Key.Left)
            {
                GoToChapter(_chapterIndex - 1);
                e.Handled = true;
                return;
            }

            if (alt && e.Key == Key.Right)
            {
                GoToChapter(_chapterIndex + 1);
                e.Handled = true;
            }
        }

        private void QueueSave()
        {
            if (_saveTimer == null)
            {
                return;
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void OnSaveTick(object sender, EventArgs e)
        {
            _saveTimer.Stop();
            SaveSettings();
        }

        private void SaveSettings()
        {
            if (_book != null)
            {
                _settings.LastBookPath = _book.FilePath;
                _settings.ChapterIndex = _chapterIndex;
            }
            _settings.FontSize = Clamp(_settings.FontSize, 15, 42);
            _settings.TextTheme = _lightText ? "light" : "dark";
            _settings.BackgroundOpacity = Clamp(_settings.BackgroundOpacity, 0, 100);
            _settings.TextOpacity = Clamp(_settings.TextOpacity, 10, 100);
            _settings.Topmost = Topmost;
            _settings.HasWindowBounds = true;
            if (!double.IsNaN(Left) && !double.IsNaN(Top))
            {
                _settings.Left = Left;
                _settings.Top = Top;
            }
            if (!double.IsNaN(Width) && Width > 0)
            {
                _settings.Width = Width;
            }
            if (!double.IsNaN(Height) && Height > 0)
            {
                _settings.Height = Height;
            }
            _settings.Save();
        }

        private void ShowError(string title, string message)
        {
            RevealWindow();
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void RegisterChrome(FrameworkElement element)
        {
            _chromeElements.Add(element);
        }

        private void AddAction(Panel panel, UIElement element, double leftMargin)
        {
            var frameworkElement = element as FrameworkElement;
            if (frameworkElement != null)
            {
                frameworkElement.Margin = new Thickness(leftMargin, 0, 0, 0);
            }
            panel.Children.Add(element);
        }

        private FrameworkElement CreateOpacityRow(
            out Slider slider,
            out TextBlock valueText,
            double minimum,
            double maximum,
            double value,
            RoutedPropertyChangedEventHandler<double> handler)
        {
            var row = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });

            slider = new Slider
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = value,
                IsSnapToTickEnabled = true,
                TickFrequency = 1,
                Height = 24,
                VerticalAlignment = VerticalAlignment.Center
            };
            slider.ValueChanged += handler;
            Grid.SetColumn(slider, 0);
            row.Children.Add(slider);

            valueText = CreatePanelText(string.Format("{0:0}%", value), 11, FontWeights.Normal);
            valueText.Opacity = 0.7;
            valueText.TextAlignment = TextAlignment.Right;
            valueText.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(valueText, 1);
            row.Children.Add(valueText);
            return row;
        }

        private static TextBlock CreatePanelText(string text, double fontSize, FontWeight weight)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = fontSize,
                FontWeight = weight,
                Foreground = MakeBrush("#FFF4EEE2")
            };
        }

        private CheckBox CreatePanelCheckBox(string text)
        {
            return new CheckBox
            {
                Content = text,
                Foreground = MakeBrush("#FFF4EEE2"),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 4),
                Cursor = Cursors.Hand
            };
        }

        private Button CreatePanelButton(string text, string toolTip, double width, double fontSize)
        {
            return new Button
            {
                Content = text,
                Width = width,
                Height = 28,
                Style = _normalButtonStyle,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = fontSize,
                Foreground = MakeBrush("#FFF4EEE2"),
                ToolTip = toolTip,
                Focusable = false,
                Cursor = Cursors.Hand,
                IsTabStop = false
            };
        }

        private static Border CreatePanelSpacer(double height)
        {
            return new Border
            {
                Height = height,
                Background = Brushes.Transparent
            };
        }

        private Button CreateButton(string text, string toolTip, double width, double fontSize)
        {
            return CreateButton(text, toolTip, width, fontSize, false);
        }

        private Button CreateButton(string text, string toolTip, double width, double fontSize, bool danger)
        {
            var button = new Button
            {
                Content = text,
                Width = width,
                Height = 28,
                Style = danger ? _dangerButtonStyle : _normalButtonStyle,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = fontSize,
                FontWeight = FontWeights.Normal,
                Foreground = _textBrush,
                ToolTip = toolTip,
                Focusable = false,
                Cursor = Cursors.Hand,
                IsTabStop = false
            };
            RegisterChrome(button);
            return button;
        }

        private Style CreateButtonStyle(Brush hoverBrush)
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 3, 8, 3)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0));

            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border), "ButtonBorder");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent
            });
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent
            });

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            var template = new ControlTemplate(typeof(Button));
            template.VisualTree = border;
            style.Setters.Add(new Setter(Control.TemplateProperty, template));

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, hoverBrush));
            style.Triggers.Add(hoverTrigger);

            var pressedTrigger = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressedTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.64));
            style.Triggers.Add(pressedTrigger);

            var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.28));
            style.Triggers.Add(disabledTrigger);

            return style;
        }

        private Style CreateChapterItemStyle()
        {
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.ForegroundProperty, MakeBrush("#FFF4EEE2")));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8)));
            style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 1, 0, 1)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));

            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border), "ItemBorder");
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent
            });
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent
            });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            border.AppendChild(presenter);

            var template = new ControlTemplate(typeof(ListBoxItem));
            template.VisualTree = border;
            style.Setters.Add(new Setter(Control.TemplateProperty, template));

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, MakeBrush("#24D8A45B")));
            style.Triggers.Add(hover);

            var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, MakeBrush("#42D8A45B")));
            style.Triggers.Add(selected);

            return style;
        }

        private Style CreateComboBoxItemStyle()
        {
            var style = new Style(typeof(ComboBoxItem));
            style.Setters.Add(new Setter(Control.BackgroundProperty, MakeBrush("#FF1C1916")));
            style.Setters.Add(new Setter(Control.ForegroundProperty, MakeBrush("#FFF4EEE2")));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));

            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border), "ComboItemBorder");
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent
            });
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent
            });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            border.AppendChild(presenter);

            var template = new ControlTemplate(typeof(ComboBoxItem));
            template.VisualTree = border;
            style.Setters.Add(new Setter(Control.TemplateProperty, template));

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, MakeBrush("#54D8A45B")));
            style.Triggers.Add(hover);

            var selected = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, MakeBrush("#72D8A45B")));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Triggers.Add(selected);

            return style;
        }

        private static ControlTemplate CreateFontComboBoxTemplate()
        {
            const string xaml =
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ComboBox}'>" +
                "<Grid>" +
                "<ToggleButton x:Name='DropDownToggle' Focusable='False' ClickMode='Press' " +
                "Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' " +
                "BorderThickness='{TemplateBinding BorderThickness}' Foreground='{TemplateBinding Foreground}' " +
                "IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>" +
                "<ToggleButton.Template>" +
                "<ControlTemplate TargetType='{x:Type ToggleButton}'>" +
                "<Border x:Name='Chrome' Background='{TemplateBinding Background}' " +
                "BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' " +
                "CornerRadius='8'>" +
                "<Grid>" +
                "<Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='38'/></Grid.ColumnDefinitions>" +
                "<ContentPresenter Margin='10,0,4,0' VerticalAlignment='Center' " +
                "Content='{Binding SelectionBoxItem, RelativeSource={RelativeSource AncestorType={x:Type ComboBox}}}' " +
                "ContentTemplate='{Binding SelectionBoxItemTemplate, RelativeSource={RelativeSource AncestorType={x:Type ComboBox}}}' " +
                "ContentStringFormat='{Binding SelectionBoxItemStringFormat, RelativeSource={RelativeSource AncestorType={x:Type ComboBox}}}'/>" +
                "<TextBlock Grid.Column='1' Text='⌄' FontSize='15' Foreground='{TemplateBinding Foreground}' " +
                "HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
                "</Grid>" +
                "</Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'>" +
                "<Setter TargetName='Chrome' Property='BorderBrush' Value='#99D8A45B'/>" +
                "<Setter TargetName='Chrome' Property='Background' Value='#FF34302A'/>" +
                "</Trigger>" +
                "<Trigger Property='IsChecked' Value='True'>" +
                "<Setter TargetName='Chrome' Property='BorderBrush' Value='#CCD8A45B'/>" +
                "</Trigger>" +
                "</ControlTemplate.Triggers>" +
                "</ControlTemplate>" +
                "</ToggleButton.Template>" +
                "</ToggleButton>" +
                "<Popup x:Name='PART_Popup' PlacementTarget='{Binding ElementName=DropDownToggle}' " +
                "Placement='Bottom' IsOpen='{TemplateBinding IsDropDownOpen}' AllowsTransparency='True' " +
                "Focusable='False' PopupAnimation='Fade' StaysOpen='False'>" +
                "<Border Background='#FF1C1916' BorderBrush='#66D8A45B' BorderThickness='1' " +
                "CornerRadius='8' Margin='0,4,0,0' " +
                "MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}' " +
                "MaxHeight='{TemplateBinding MaxDropDownHeight}'>" +
                "<ScrollViewer VerticalScrollBarVisibility='Hidden' HorizontalScrollBarVisibility='Disabled'>" +
                "<ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/>" +
                "</ScrollViewer>" +
                "</Border>" +
                "</Popup>" +
                "</Grid>" +
                "</ControlTemplate>";

            return (ControlTemplate)XamlReader.Parse(xaml);
        }

        private static string FindStartupFile(string[] args)
        {
            if (args == null)
            {
                return null;
            }

            foreach (string arg in args)
            {
                if (!string.IsNullOrWhiteSpace(arg) && File.Exists(arg))
                {
                    return Path.GetFullPath(arg);
                }
            }
            return null;
        }

        private static bool IsUsableBounds(double left, double top, double width, double height)
        {
            if (double.IsNaN(left) || double.IsNaN(top) || double.IsInfinity(left) || double.IsInfinity(top))
            {
                return false;
            }

            Rect virtualScreen = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);
            Rect window = new Rect(left, top, width, height);
            Rect intersection = Rect.Intersect(virtualScreen, window);
            return !intersection.IsEmpty && intersection.Width >= 120 && intersection.Height >= 100;
        }

        private static SolidColorBrush MakeBrush(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (maximum < minimum)
            {
                maximum = minimum;
            }
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            if (maximum < minimum)
            {
                maximum = minimum;
            }
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private sealed class ChapterOption
        {
            public ChapterOption(int index, string title)
            {
                Index = index;
                Title = title;
            }

            public int Index { get; private set; }
            public string Title { get; private set; }

            public override string ToString()
            {
                return string.Format("{0:000}   {1}", Index + 1, Title);
            }
        }
    }
}
