using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.Text;
using WinUIEx;
using WinRT.Interop;

namespace TuckPane;

public sealed partial class TodoWindow : Window
{
    private const double MinimumWidthDip = 280;
    private const double MinimumHeightDip = 340;
    private const double MaximumWidthDip = 1600;
    private const double MaximumHeightDip = 1200;

    private readonly AppHost _host;
    private readonly NoteStore _store;
    private readonly PortableTodoDocument _document;
    private readonly ObservableCollection<TodoRow> _rows;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _completionTimer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private string _externalPath;
    private AppWindow _appWindow = null!;
    private InputNonClientPointerSource? _titleInput;
    private NativeWindowChromeController? _chrome;
    private IntPtr _hwnd;
    private bool _initialized;
    private bool _permanentClose;
    private bool _visible;
    private bool _restoringPlacement;
    private bool _renaming;
    private bool _renameCommitInProgress;
    private bool _syncingTaskCheckBox;

    internal TodoWindow(AppHost host, NoteStore store, string path, PortableTodoDocument document)
    {
        _host = host;
        _store = store;
        _externalPath = Path.GetFullPath(path);
        _document = document;
        bool removedExpired = TodoRules.RemoveExpired(_document, DateTimeOffset.UtcNow) > 0;
        _rows = new ObservableCollection<TodoRow>(_document.Tasks.Select(task => new TodoRow(task, _document.FontSize)));

        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDragSurface);
        SystemBackdrop = new TransparentTintBackdrop(Colors.Transparent);
        TaskList.ItemsSource = _rows;
        NewTaskBox.FontSize = _document.FontSize;
        WindowRoot.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(WindowRoot_PointerWheelChanged), handledEventsToo: true);

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += async (_, _) => await FlushAsync();

        _completionTimer = DispatcherQueue.CreateTimer();
        _completionTimer.Interval = TimeSpan.FromMilliseconds(50);
        _completionTimer.IsRepeating = true;
        _completionTimer.Tick += CompletionTimer_Tick;

        Closed += TodoWindow_Closed;
        ApplyLanguage();
        ApplyTheme();
        RefreshCompletionTimer();
        if (removedExpired) ScheduleSave();
    }

    internal bool IsVisible => _visible;
    internal string ExternalPath => _externalPath;

    internal void InitializeHostWindow()
    {
        if (_initialized) return;
        _initialized = true;
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _titleInput = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
        _appWindow.IsShownInSwitchers = true;
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsAlwaysOnTop = _host.State.GlobalSettings.NoteAlwaysOnTop;
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }
        long extendedStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        extendedStyle = (extendedStyle | NativeMethods.WS_EX_APPWINDOW) & ~NativeMethods.WS_EX_TOOLWINDOW;
        _ = NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(extendedStyle));
        _ = NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        _chrome = new NativeWindowChromeController(_hwnd, DispatcherQueue);
        RestorePlacement();
        _appWindow.Changed += AppWindow_Changed;
        _appWindow.Closing += AppWindow_Closing;
    }

    internal void ShowAndActivate()
    {
        InitializeHostWindow();
        _visible = true;
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore(activateWindow: true);
        _appWindow.Show(activateWindow: true);
        Activate();
        _ = NativeMethods.SetWindowPos(
            _hwnd,
            _host.State.GlobalSettings.NoteAlwaysOnTop ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_TOP,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW |
            NativeMethods.SWP_NOOWNERZORDER);
        _ = NativeMethods.SetForegroundWindow(_hwnd);
        _ = DispatcherQueue.TryEnqueue(UpdateTitlePassthrough);
    }

    internal void ApplyAlwaysOnTopSetting()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = _host.State.GlobalSettings.NoteAlwaysOnTop;
    }

    internal async Task ApplyGlobalThemeAsync(NoteTheme theme)
    {
        _document.Theme = theme;
        ApplyTheme();
        await FlushAsync();
    }

    internal void RebindExternalPath(string path)
    {
        _externalPath = Path.GetFullPath(path);
        UpdateTitle();
    }

    internal void HideForTray()
    {
        if (!_visible) return;
        _visible = false;
        _appWindow.Hide();
    }

    internal void RestoreFromTray()
    {
        if (_permanentClose) return;
        _visible = true;
        _appWindow.Show(activateWindow: false);
    }

    internal async Task HideAsync()
    {
        if (_permanentClose) return;
        await FlushAsync();
        _visible = false;
        _appWindow.Hide();
    }

    internal async Task<bool> FlushAndHideForDragAsync()
    {
        bool wasVisible = _visible;
        if (!await FlushAsync()) throw new IOException(AppStrings.Get("TodoDragSaveFailed"));
        if (wasVisible)
        {
            _visible = false;
            _appWindow.Hide();
        }
        return wasVisible;
    }

    internal void RestoreAfterDrag()
    {
        if (_permanentClose) return;
        _visible = true;
        _appWindow.Show(activateWindow: false);
    }

    internal Task<bool> FlushForExitAsync() =>
        _permanentClose ? Task.FromResult(true) : FlushAsync();

    internal async Task ClosePermanentlyAsync()
    {
        if (_permanentClose) return;
        await FlushAsync();
        _permanentClose = true;
        Close();
    }

    internal void ClosePermanentlyWithoutSave()
    {
        if (_permanentClose) return;
        _saveTimer.Stop();
        _completionTimer.Stop();
        _permanentClose = true;
        Close();
    }

    internal void ApplyLanguage()
    {
        UpdateTitle();
        AutomationProperties.SetName(NewTaskBox, AppStrings.Get("TodoAddPlaceholder"));
        AutomationProperties.SetName(ColorButton, AppStrings.Get("TodoColor"));
        ToolTipService.SetToolTip(ColorButton, AppStrings.Get("TodoColor"));
        AutomationProperties.SetName(CloseButton, AppStrings.Get("CloseTodo"));
        ToolTipService.SetToolTip(CloseButton, AppStrings.Get("CloseTodo"));
        foreach (TodoRow row in _rows) row.ApplyLanguage();
    }

    private void UpdateTitle()
    {
        string name = Path.GetFileNameWithoutExtension(_externalPath);
        Title = name;
        TodoTitleText.Text = name;
        AutomationProperties.SetName(TodoTitleText, name);
        AutomationProperties.SetHelpText(TodoTitleText, AppStrings.Get("TodoRenameHint"));
    }

    private async Task<bool> FlushAsync()
    {
        _saveTimer.Stop();
        await _saveGate.WaitAsync();
        try
        {
            _document.Tasks = _rows.Select(row => row.Task).ToList();
            await _store.SaveTodoAsync(_externalPath, _document);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法保存待办：{_externalPath}", ex);
            ShowError(AppStrings.Format("TodoSaveErrorFormat", ex.Message));
            return false;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void NewTaskBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        try
        {
            PortableTodoTask task = TodoRules.Add(_document, NewTaskBox.Text);
            _rows.Add(new TodoRow(task, _document.FontSize));
            NewTaskBox.Text = string.Empty;
            ScheduleSave();
        }
        catch (ArgumentException)
        {
            // Empty input is intentionally ignored.
        }
    }

    private void TaskCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingTaskCheckBox || sender is not CheckBox { Tag: TodoRow row } box) return;
        TodoRules.SetDone(row.Task, box.IsChecked == true, DateTimeOffset.UtcNow);
        row.RefreshVisual(DateTimeOffset.UtcNow);
        RefreshCompletionTimer();
        ScheduleSave();
    }

    private void UndoTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TodoRow row }) return;
        TodoRules.SetDone(row.Task, done: false, DateTimeOffset.UtcNow);
        row.RefreshVisual(DateTimeOffset.UtcNow);
        SyncTaskCheckBox(row);
        RefreshCompletionTimer();
        ScheduleSave();
    }

    private void TaskCheckBox_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is CheckBox box && args.NewValue is TodoRow row) SetTaskCheckBoxState(box, row.Done);
    }

    private void SyncTaskCheckBox(TodoRow row)
    {
        if (TaskList.ContainerFromItem(row) is not ListViewItem container) return;
        CheckBox? box = FindDescendant<CheckBox>(container, candidate => ReferenceEquals(candidate.Tag, row));
        if (box is not null) SetTaskCheckBoxState(box, row.Done);
    }

    private void SetTaskCheckBoxState(CheckBox box, bool isChecked)
    {
        _syncingTaskCheckBox = true;
        try { box.IsChecked = isChecked; }
        finally { _syncingTaskCheckBox = false; }
    }

    private void TaskText_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoRow row }) BeginTaskEdit(row);
        e.Handled = true;
    }

    private void EditTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: TodoRow row }) BeginTaskEdit(row);
    }

    private void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: TodoRow row }) return;
        _rows.Remove(row);
        _document.Tasks.Remove(row.Task);
        RefreshCompletionTimer();
        ScheduleSave();
    }

    private void BeginTaskEdit(TodoRow row)
    {
        row.BeginEdit();
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (TaskList.ContainerFromItem(row) is not ListViewItem container) return;
            TextBox? editor = FindDescendant<TextBox>(container, candidate => ReferenceEquals(candidate.Tag, row));
            editor?.Focus(FocusState.Programmatic);
            editor?.SelectAll();
        });
    }

    private void TaskEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: TodoRow row }) return;
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            row.CancelEdit();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitTaskEdit(row);
            e.Handled = true;
        }
    }

    private void TaskEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: TodoRow row }) CommitTaskEdit(row);
    }

    private void CommitTaskEdit(TodoRow row)
    {
        if (!row.IsEditing) return;
        if (TodoRules.UpdateText(row.Task, row.EditText)) ScheduleSave();
        row.EndEdit();
    }

    private void TaskList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _document.Tasks = _rows.Select(row => row.Task).ToList();
        ScheduleSave();
    }

    private void CompletionTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (TodoRow row in _rows) row.RefreshVisual(now);
        TodoRow[] expired = _rows.Where(row =>
            row.Task.Done &&
            row.Task.CompletedAtUtc is DateTimeOffset completed &&
            now.ToUniversalTime() - completed >= TodoRules.CompletionDelay).ToArray();
        foreach (TodoRow row in expired)
        {
            _rows.Remove(row);
            _document.Tasks.Remove(row.Task);
        }
        if (expired.Length > 0)
        {
            ScheduleSave();
        }
        RefreshCompletionTimer();
    }

    private void RefreshCompletionTimer()
    {
        if (_rows.Any(row => row.Done)) _completionTimer.Start();
        else _completionTimer.Stop();
    }

    private void WindowRoot_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if ((NativeMethods.GetKeyState(NativeMethods.VK_CONTROL) & 0x8000) == 0) return;
        int delta = e.GetCurrentPoint(WindowRoot).Properties.MouseWheelDelta;
        if (delta == 0) return;
        double next = Math.Clamp(
            _document.FontSize + Math.Sign(delta),
            OrganizerNoteRules.MinimumFontSize,
            OrganizerNoteRules.MaximumFontSize);
        if (next == _document.FontSize) return;
        _document.FontSize = next;
        foreach (TodoRow row in _rows) row.FontSize = next;
        NewTaskBox.FontSize = next;
        ScheduleSave();
        e.Handled = true;
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        foreach (NoteThemeColors colors in NoteThemePalette.All)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = AppStrings.Get(colors.NameKey),
                Tag = colors.Theme,
                IsChecked = colors.Theme == _document.Theme
            };
            item.Click += ThemeItem_Click;
            flyout.Items.Add(item);
        }
        flyout.ShowAt(ColorButton);
    }

    private async void ThemeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: NoteTheme theme }) return;
        try { await _host.SetNoteThemeAsync(theme); }
        catch (Exception ex) { ShowError(AppStrings.Format("TodoSaveErrorFormat", ex.Message)); }
    }

    private void ApplyTheme()
    {
        NoteThemeColors colors = NoteThemePalette.Get(_document.Theme);
        WindowRoot.RequestedTheme = colors.TextColor.R > 128 ? ElementTheme.Dark : ElementTheme.Light;
        var editor = new SolidColorBrush(colors.EditorColor);
        var surface = new SolidColorBrush(colors.SurfaceColor);
        var text = (SolidColorBrush)WindowRoot.Resources["TodoTextBrush"];
        var input = (SolidColorBrush)WindowRoot.Resources["TodoInputBrush"];
        var border = (SolidColorBrush)WindowRoot.Resources["TodoBorderBrush"];
        var accent = new SolidColorBrush(colors.AccentColor);
        text.Color = colors.TextColor;
        input.Color = ColorHelper.FromArgb(24, colors.TextColor.R, colors.TextColor.G, colors.TextColor.B);
        border.Color = colors.BorderColor;
        WindowRoot.Resources["TextControlBackgroundPointerOver"] = input;
        WindowRoot.Resources["TextControlBackgroundFocused"] = input;
        WindowRoot.Resources["TextControlForegroundPointerOver"] = text;
        WindowRoot.Resources["TextControlForegroundFocused"] = text;
        WindowRoot.Resources["TextControlBorderBrushPointerOver"] = border;
        WindowRoot.Resources["TextControlBorderBrushFocused"] = border;
        WindowFrame.Background = editor;
        WindowFrame.BorderBrush = border;
        DragTitleBar.Background = surface;
        TodoTitleText.Foreground = text;
        TodoTitleEditor.Foreground = text;
        ColorButton.Foreground = accent;
        CloseButton.Foreground = text;
        TaskList.Foreground = text;
    }

    private async void CloseButton_Click(object sender, RoutedEventArgs e) => await HideAsync();

    private void TodoTitleText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_renaming) return;
        _renaming = true;
        TodoTitleEditor.Text = Path.GetFileNameWithoutExtension(_externalPath);
        TodoTitleText.Visibility = Visibility.Collapsed;
        TodoTitleEditor.Visibility = Visibility.Visible;
        UpdateTitlePassthrough();
        TodoTitleEditor.Focus(FocusState.Programmatic);
        TodoTitleEditor.SelectAll();
        e.Handled = true;
    }

    private async void TodoTitleEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            EndRename();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await CommitRenameAsync();
        }
    }

    private async void TodoTitleEditor_LostFocus(object sender, RoutedEventArgs e) => await CommitRenameAsync();

    private async Task CommitRenameAsync()
    {
        if (!_renaming || _renameCommitInProgress) return;
        _renameCommitInProgress = true;
        try
        {
            if (!await FlushAsync()) return;
            _externalPath = await _host.RenameExternalTodoAsync(_externalPath, TodoTitleEditor.Text);
            EndRename();
            UpdateTitle();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法重命名待办：{_externalPath}", ex);
            ShowError(ex.Message);
        }
        finally
        {
            _renameCommitInProgress = false;
            if (_renaming)
            {
                TodoTitleEditor.Focus(FocusState.Programmatic);
                TodoTitleEditor.SelectAll();
            }
        }
    }

    private void EndRename()
    {
        _renaming = false;
        TodoTitleEditor.Visibility = Visibility.Collapsed;
        TodoTitleText.Visibility = Visibility.Visible;
        UpdateTitlePassthrough();
    }

    private void TitleDragSurface_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitlePassthrough();

    private void UpdateTitlePassthrough()
    {
        FrameworkElement target = _renaming ? TitleDragSurface : TodoTitleText;
        if (_titleInput is null || target.ActualWidth <= 0 || target.ActualHeight <= 0) return;
        Rect bounds = target.TransformToVisual(WindowRoot)
            .TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        _titleInput.SetRegionRects(
            NonClientRegionKind.Passthrough,
            [new RectInt32(
                (int)Math.Floor(bounds.X * scale),
                (int)Math.Floor(bounds.Y * scale),
                Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(bounds.Height * scale))) ]);
    }

    private void RestorePlacement()
    {
        PortableNotePlacement placement = _document.Placement ?? new PortableNotePlacement
        {
            WidthDip = 360,
            HeightDip = 480
        };
        DisplayInfo display = DisplayPlacementService.GetDisplay(placement.MonitorDevice);
        int width = (int)Math.Round(placement.WidthDip * display.Scale);
        int height = (int)Math.Round(placement.HeightDip * display.Scale);
        int left = string.IsNullOrWhiteSpace(placement.MonitorDevice)
            ? display.Work.Left + (display.Work.Width - width) / 2
            : display.Work.Left + (int)Math.Round(placement.XDip * display.Scale);
        int top = string.IsNullOrWhiteSpace(placement.MonitorDevice)
            ? display.Work.Top + (display.Work.Height - height) / 2
            : display.Work.Top + (int)Math.Round(placement.YDip * display.Scale);
        NativeMethods.RECT bounds = DisplayPlacementService.Clamp(new NativeMethods.RECT
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height
        }, display.Work);
        _restoringPlacement = true;
        _appWindow.MoveAndResize(new RectInt32(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
        _restoringPlacement = false;
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_restoringPlacement || (!args.DidPositionChange && !args.DidSizeChange)) return;
        if (args.DidSizeChange)
        {
            DisplayInfo currentDisplay = DisplayPlacementService.ForBounds(new NativeMethods.RECT
            {
                Left = sender.Position.X,
                Top = sender.Position.Y,
                Right = sender.Position.X + sender.Size.Width,
                Bottom = sender.Position.Y + sender.Size.Height
            });
            int width = Math.Clamp(sender.Size.Width,
                (int)Math.Round(MinimumWidthDip * currentDisplay.Scale),
                (int)Math.Round(MaximumWidthDip * currentDisplay.Scale));
            int height = Math.Clamp(sender.Size.Height,
                (int)Math.Round(MinimumHeightDip * currentDisplay.Scale),
                (int)Math.Round(MaximumHeightDip * currentDisplay.Scale));
            if (width != sender.Size.Width || height != sender.Size.Height)
            {
                _restoringPlacement = true;
                sender.Resize(new SizeInt32(width, height));
                _restoringPlacement = false;
            }
        }
        if (!NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT bounds)) return;
        DisplayInfo display = DisplayPlacementService.ForBounds(bounds);
        _document.Placement = new PortableNotePlacement
        {
            MonitorDevice = display.Device,
            XDip = (bounds.Left - display.Work.Left) / display.Scale,
            YDip = (bounds.Top - display.Work.Top) / display.Scale,
            WidthDip = bounds.Width / display.Scale,
            HeightDip = bounds.Height / display.Scale
        };
        ScheduleSave();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_permanentClose) return;
        args.Cancel = true;
        _ = HideAsync();
    }

    private void TodoWindow_Closed(object sender, WindowEventArgs args)
    {
        _saveTimer.Stop();
        _completionTimer.Stop();
        _chrome?.Dispose();
        _chrome = null;
        _saveGate.Dispose();
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && predicate(match)) return match;
            if (FindDescendant(child, predicate) is T nested) return nested;
        }
        return null;
    }

    public sealed class TodoRow : INotifyPropertyChanged
    {
        private double _fontSize;
        private bool _isEditing;
        private string _editText;
        private double _opacity = 1;

        internal TodoRow(PortableTodoTask task, double fontSize)
        {
            Task = task;
            _fontSize = fontSize;
            _editText = task.Text;
            RefreshVisual(DateTimeOffset.UtcNow);
        }

        internal PortableTodoTask Task { get; }
        public string Text => Task.Text;
        public bool Done => Task.Done;
        public TextDecorations TextDecorations => Done
            ? Windows.UI.Text.TextDecorations.Strikethrough
            : Windows.UI.Text.TextDecorations.None;
        public Visibility UndoVisibility => Done ? Visibility.Visible : Visibility.Collapsed;
        public string UndoLabel => AppStrings.Get("TodoUndo");
        public string EditLabel => AppStrings.Get("TodoEdit");
        public string DeleteLabel => AppStrings.Get("TodoDelete");
        public Visibility TextVisibility => _isEditing ? Visibility.Collapsed : Visibility.Visible;
        public Visibility EditorVisibility => _isEditing ? Visibility.Visible : Visibility.Collapsed;
        public bool IsEditing => _isEditing;
        public double CheckBoxSize => 20 + Math.Max(0, _fontSize - 14);

        public string EditText
        {
            get => _editText;
            set => _editText = value;
        }

        public double FontSize
        {
            get => _fontSize;
            set
            {
                if (_fontSize == value) return;
                _fontSize = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CheckBoxSize));
            }
        }

        public double Opacity
        {
            get => _opacity;
            private set { if (Math.Abs(_opacity - value) > .001) { _opacity = value; OnPropertyChanged(); } }
        }

        internal void BeginEdit()
        {
            _editText = Task.Text;
            _isEditing = true;
            OnPropertyChanged(nameof(EditText));
            OnPropertyChanged(nameof(TextVisibility));
            OnPropertyChanged(nameof(EditorVisibility));
        }

        internal void CancelEdit()
        {
            _editText = Task.Text;
            EndEdit();
        }

        internal void EndEdit()
        {
            _isEditing = false;
            _editText = Task.Text;
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(EditText));
            OnPropertyChanged(nameof(TextVisibility));
            OnPropertyChanged(nameof(EditorVisibility));
        }

        internal void RefreshVisual(DateTimeOffset now)
        {
            Opacity = TodoRules.GetOpacity(Task, now);
            OnPropertyChanged(nameof(Done));
            OnPropertyChanged(nameof(TextDecorations));
            OnPropertyChanged(nameof(UndoVisibility));
        }

        internal void ApplyLanguage()
        {
            OnPropertyChanged(nameof(UndoLabel));
            OnPropertyChanged(nameof(EditLabel));
            OnPropertyChanged(nameof(DeleteLabel));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
