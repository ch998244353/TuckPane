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
    private readonly WindowThemeBrushes _themeBrushes = new();
    private readonly NoteStore _store;
    private readonly PortableTodoDocument _document;
    private readonly TodoPlacementRecovery _placementRecovery;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private readonly ObservableCollection<TodoRow> _rows;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _completionTimer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly TodoComposer _composer = new();
    private string _externalPath;
    private AppWindow _appWindow = null!;
    private InputNonClientPointerSource? _titleInput;
    private NativeWindowChromeController? _chrome;
    private IntPtr _hwnd;
    private bool _initialized;
    private bool _permanentClose;
    private bool _visibleValue;
    private bool _visible
    {
        get => _visibleValue;
        set
        {
            if (_visibleValue == value) return;
            _visibleValue = value;
            _host.NotifyDockOpenStateChanged();
        }
    }
    private bool _restoringPlacement;
    private bool _renaming;
    private bool _renameCommitInProgress;
    private bool _syncingTaskCheckBox;
    private bool _saveErrorVisible;
    private int _taskEditRequest;

    internal TodoWindow(AppHost host, NoteStore store, string path, PortableTodoDocument document)
    {
        _host = host;
        _store = store;
        _externalPath = Path.GetFullPath(path);
        _document = document;
        _placementRecovery = new TodoPlacementRecovery(document.Placement);
        _document.Placement = _placementRecovery.CaptureForSave(document.Placement);
        bool removedExpired = TodoRules.RemoveExpired(_document, DateTimeOffset.UtcNow) > 0;
        _rows = new ObservableCollection<TodoRow>(_document.Tasks.Select(task =>
            new TodoRow(task, _document.FontSize, _host.State.GlobalSettings.TodoShowSeparators)));

        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDragSurface);
        SystemBackdrop = new TransparentTintBackdrop(Colors.Transparent);
        TaskList.ItemsSource = _rows;
        UpdateComposerMetrics();
        _uiSettings.TextScaleFactorChanged += TextScaleFactorChanged;
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
        ApplySeparatorSetting();
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
        try { ApplyTheme(); }
        catch (Exception ex)
        {
            AppLogger.Error("待办主题外观应用失败", ex);
            throw;
        }
        if (!await FlushAsync())
            throw new IOException(AppStrings.Get("NoteThemeDocumentSaveFailed"));
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
        AutomationProperties.SetName(AddTaskButton, AppStrings.Get("TodoAddPlaceholder"));
        AutomationProperties.SetName(ColorButton, AppStrings.Get("TodoColor"));
        ToolTipService.SetToolTip(ColorButton, AppStrings.Get("TodoColor"));
        AutomationProperties.SetName(SeparatorsButton, AppStrings.Get("TodoSeparators"));
        ToolTipService.SetToolTip(SeparatorsButton, AppStrings.Get("TodoSeparators"));
        AutomationProperties.SetName(MinimizeButton, AppStrings.Get("WindowMinimize"));
        ToolTipService.SetToolTip(MinimizeButton, AppStrings.Get("WindowMinimize"));
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
        try { return await FlushCoreAsync(); }
        finally { _saveGate.Release(); }
    }

    internal async Task BeginDirectoryRenameAsync()
    {
        _saveTimer.Stop();
        await _saveGate.WaitAsync();
        if (await FlushCoreAsync())
        {
            WindowRoot.IsHitTestVisible = false;
            return;
        }
        _saveGate.Release();
        throw new IOException(AppStrings.Get("TodoDragSaveFailed"));
    }

    internal void EndDirectoryRename()
    {
        WindowRoot.IsHitTestVisible = true;
        _saveGate.Release();
    }

    private async Task<bool> FlushCoreAsync()
    {
        try
        {
            _document.Tasks = _rows.Select(row => row.Task).ToList();
            _document.Placement = _placementRecovery.CaptureForSave(_document.Placement);
            await _store.SaveTodoAsync(_externalPath, _document);
            if (_saveErrorVisible)
            {
                ErrorBar.IsOpen = false;
                _saveErrorVisible = false;
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法保存待办：{_externalPath}", ex);
            ShowError(AppStrings.Format("TodoSaveErrorFormat", ex.Message), isSaveError: true);
            return false;
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void NewTaskBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            _composer.Cancel();
            RefreshComposer(focusAdd: true);
            return;
        }
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        if (_composer.Submit(_document) is not PortableTodoTask task) return;
        _rows.Add(new TodoRow(task, _document.FontSize, _host.State.GlobalSettings.TodoShowSeparators));
        UpdateComposerMetrics();
        RefreshComposer(focusAdd: true);
        ScheduleSave();
        _ = DispatcherQueue.TryEnqueue(() => NewTaskEntry.StartBringIntoView());
    }

    private void AddTaskButton_Click(object sender, RoutedEventArgs e)
    {
        _composer.Begin();
        RefreshComposer();
        NewTaskBox.Focus(FocusState.Programmatic);
    }

    private void NewTaskBox_TextChanged(object sender, TextChangedEventArgs e) =>
        _composer.Text = NewTaskBox.Text;

    private void NewTaskBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _composer.Blur();
        RefreshComposer();
    }

    private void RefreshComposer(bool focusAdd = false)
    {
        NewTaskBox.Text = _composer.Text;
        AddTaskButton.Visibility = _composer.IsEditing ? Visibility.Collapsed : Visibility.Visible;
        NewTaskBox.Visibility = _composer.IsEditing ? Visibility.Visible : Visibility.Collapsed;
        if (focusAdd) AddTaskButton.Focus(FocusState.Programmatic);
    }

    private void UpdateComposerMetrics()
    {
        NewTaskBox.FontSize = _document.FontSize;
        AddTaskButton.FontSize = _document.FontSize;
        // Measure one ordinary text line, independent of wrapped/finished rows.
        var line = new TextBlock
        {
            Text = "Ag中", FontSize = _document.FontSize,
            FontFamily = NewTaskBox.FontFamily,
            IsTextScaleFactorEnabled = NewTaskBox.IsTextScaleFactorEnabled
        };
        line.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double height = TodoRowMetrics.SingleLineHeight(
            _document.FontSize, line.DesiredSize.Height, _host.State.GlobalSettings.TodoShowSeparators);
        NewTaskEntry.Height = height;
        foreach (TodoRow row in _rows) row.SingleLineHeight = height;
    }

    private void TextScaleFactorChanged(Windows.UI.ViewManagement.UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_permanentClose) UpdateComposerMetrics();
        });

    private void TaskCheckTarget_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Native CheckBox taps already toggle themselves; only bridge the surrounding hit area.
        if (sender is not Grid target || !ReferenceEquals(e.OriginalSource, target) ||
            FindDescendant<CheckBox>(target, candidate => candidate.Tag is TodoRow) is not { IsEnabled: true } box) return;
        _ = box.Focus(FocusState.Pointer);
        box.IsChecked = box.IsChecked != true;
        TaskCheckBox_Click(box, e);
        e.Handled = true;
    }

    private void TaskCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingTaskCheckBox || sender is not CheckBox { Tag: TodoRow row } box) return;
        TodoRules.SetDone(row.Task, box.IsChecked == true, DateTimeOffset.UtcNow);
        row.RefreshVisual(DateTimeOffset.UtcNow);
        RefreshCompletionTimer();
        ScheduleSave();
    }

    private void TaskCheckBox_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is CheckBox box && args.NewValue is TodoRow row) SetTaskCheckBoxState(box, row.Done);
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
        int request = ++_taskEditRequest;
        SyncRealizedTaskTypography(row);
        row.BeginEdit();
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (request != _taskEditRequest || !row.IsEditing) return;
            if (TaskList.ContainerFromItem(row) is not ListViewItem container) return;
            TextBox? editor = FindDescendant<TextBox>(container, candidate => ReferenceEquals(candidate.Tag, row));
            if (editor is null || !ReferenceEquals(editor.DataContext, row) ||
                editor.Visibility != Visibility.Visible) return;
            if (editor.Parent is not Grid content) return;
            SyncTaskTypography(content);
            editor.ApplyTemplate();
            if (editor is Controls.TaskEditorTextBox taskEditor) taskEditor.SynchronizeTemplateTypography();
            if (!editor.Focus(FocusState.Programmatic)) return;
            // Focus initializes the native input view. Select only after its
            // presenter has inherited this row's font, including recycled rows.
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (request != _taskEditRequest || !row.IsEditing ||
                    !ReferenceEquals(editor.DataContext, row) || !ReferenceEquals(editor.Tag, row) ||
                    editor.Visibility != Visibility.Visible || editor.FocusState == FocusState.Unfocused) return;
                SyncTaskTypography(content);
                if (editor is Controls.TaskEditorTextBox focusedEditor) focusedEditor.SynchronizeTemplateTypography();
                editor.SelectAll();
            });
        });
    }

    private void TaskRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Grid content) SyncTaskTypography(content);
    }

    private void TaskRow_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Grid content) SyncTaskTypography(content);
    }

    private void SyncRealizedTaskTypography(TodoRow row)
    {
        if (TaskList.ContainerFromItem(row) is not ListViewItem container) return;
        TextBox? editor = FindDescendant<TextBox>(container, candidate => ReferenceEquals(candidate.Tag, row));
        if (editor?.Parent is Grid content && ReferenceEquals(content.DataContext, row))
            SyncTaskTypography(content);
    }

    private static void SyncTaskTypography(Grid content)
    {
        if (content.DataContext is not TodoRow row) return;
        TextBlock? text = content.Children.OfType<TextBlock>().FirstOrDefault();
        TextBox? editor = content.Children.OfType<TextBox>().FirstOrDefault();
        if (text is null || editor is null) return;

        // Bind directly to this row, including when a collapsed editor is reused.
        // Keep a live binding so a font change during editing reaches both controls.
        text.SetBinding(TextBlock.FontSizeProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = row, Path = new PropertyPath(nameof(TodoRow.FontSize)),
            Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
        });
        editor.SetBinding(Control.FontSizeProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = row, Path = new PropertyPath(nameof(TodoRow.FontSize)),
            Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
        });
        editor.FontFamily = text.FontFamily;
        editor.FontWeight = text.FontWeight;
        editor.FontStyle = text.FontStyle;
        editor.FontStretch = text.FontStretch;
        editor.CharacterSpacing = text.CharacterSpacing;
        editor.IsTextScaleFactorEnabled = text.IsTextScaleFactorEnabled;
    }

    private void TaskEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: TodoRow row } editor) return;
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            row.CancelEdit();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            string currentText = editor.Text;
            CommitTaskEdit(row, currentText);
            // Collapsing the focused editor lets XAML choose another tab stop.
            // Restore the row only for Enter; a pointer-driven LostFocus keeps
            // the user's chosen destination. Commit first to make reentry inert.
            if (TaskList.ContainerFromItem(row) is ListViewItem container)
                _ = container.Focus(FocusState.Programmatic);
        }
    }

    private void TaskEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: TodoRow row } editor) CommitTaskEdit(row, editor.Text);
    }

    private void CommitTaskEdit(TodoRow row, string currentText)
    {
        // TextBox.Text may not have reached the two-way binding source before Enter.
        if (row.CommitEdit(currentText)) ScheduleSave();
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
        foreach (TodoRow row in _rows)
        {
            row.FontSize = next;
            SyncRealizedTaskTypography(row);
        }
        UpdateComposerMetrics();
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
        catch (Exception ex)
        {
            AppLogger.Error("无法同步全局待办主题", ex);
            ShowError(AppStrings.Format("NoteThemeChangeErrorFormat", ex.Message));
        }
    }

    internal void ApplySeparatorSetting()
    {
        bool enabled = _host.State.GlobalSettings.TodoShowSeparators;
        SeparatorsButton.IsChecked = enabled;
        foreach (TodoRow row in _rows) row.SetSeparators(enabled);
        UpdateComposerMetrics();
    }

    private async void SeparatorsButton_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = SeparatorsButton.IsChecked == true;
        SeparatorsButton.IsEnabled = false;
        try { await _host.SetTodoShowSeparatorsAsync(enabled); }
        catch (Exception ex)
        {
            AppLogger.Error("无法保存待办分隔线设置", ex);
            ShowError(AppStrings.Format("TodoSaveErrorFormat", ex.Message));
        }
        finally
        {
            ApplySeparatorSetting();
            SeparatorsButton.IsEnabled = true;
        }
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
        var separator = (SolidColorBrush)WindowRoot.Resources["TodoSeparatorBrush"];
        var accent = new SolidColorBrush(colors.AccentColor);
        text.Color = colors.TextColor;
        input.Color = ColorHelper.FromArgb(24, colors.TextColor.R, colors.TextColor.G, colors.TextColor.B);
        border.Color = colors.BorderColor;
        separator.Color = colors.BorderColor;
        _themeBrushes.Set(WindowRoot.Resources, "TextControlBackgroundPointerOver", input.Color);
        _themeBrushes.Set(WindowRoot.Resources, "TextControlBackgroundFocused", input.Color);
        _themeBrushes.Set(WindowRoot.Resources, "TextControlForegroundPointerOver", text.Color);
        _themeBrushes.Set(WindowRoot.Resources, "TextControlForegroundFocused", text.Color);
        _themeBrushes.Set(WindowRoot.Resources, "TextControlBorderBrushPointerOver", border.Color);
        _themeBrushes.Set(WindowRoot.Resources, "TextControlBorderBrushFocused", border.Color);
        Color addColor = ColorHelper.FromArgb(12, colors.TextColor.R, colors.TextColor.G, colors.TextColor.B);
        Color addBorder = ColorHelper.FromArgb(80, colors.BorderColor.R, colors.BorderColor.G, colors.BorderColor.B);
        ((SolidColorBrush)WindowRoot.Resources["TodoAddBrush"]).Color = addColor;
        ((SolidColorBrush)WindowRoot.Resources["TodoAddBorderBrush"]).Color = addBorder;
        _themeBrushes.Set(NewTaskBox.Resources, "TextControlBackgroundPointerOver", addColor);
        _themeBrushes.Set(NewTaskBox.Resources, "TextControlBackgroundFocused", addColor);
        _themeBrushes.Set(NewTaskBox.Resources, "TextControlBorderBrushPointerOver", addBorder);
        _themeBrushes.Set(NewTaskBox.Resources, "TextControlBorderBrushFocused", colors.AccentColor);
        _themeBrushes.Set(AddTaskButton.Resources, "ButtonBackgroundPointerOver", ColorHelper.FromArgb(24, colors.TextColor.R, colors.TextColor.G, colors.TextColor.B));
        _themeBrushes.Set(AddTaskButton.Resources, "ButtonBackgroundPressed", ColorHelper.FromArgb(36, colors.TextColor.R, colors.TextColor.G, colors.TextColor.B));
        _themeBrushes.Set(AddTaskButton.Resources, "ButtonForegroundPointerOver", colors.TextColor);
        _themeBrushes.Set(AddTaskButton.Resources, "ButtonForegroundPressed", colors.TextColor);
        _themeBrushes.Set(AddTaskButton.Resources, "ButtonBorderBrushPointerOver", addBorder);
        _themeBrushes.Set(AddTaskButton.Resources, "ButtonBorderBrushPressed", colors.AccentColor);
        WindowFrame.Background = editor;
        WindowFrame.BorderBrush = border;
        DragTitleBar.Background = surface;
        TodoTitleText.Foreground = text;
        TodoTitleEditor.Foreground = text;
        ColorButton.Foreground = accent;
        SeparatorsButton.Foreground = text;
        _themeBrushes.Set(SeparatorsButton.Resources, "ToggleButtonBackgroundChecked",
            ColorHelper.FromArgb(68, colors.AccentColor.R, colors.AccentColor.G, colors.AccentColor.B));
        _themeBrushes.Set(SeparatorsButton.Resources, "ToggleButtonBackgroundCheckedPointerOver",
            ColorHelper.FromArgb(88, colors.AccentColor.R, colors.AccentColor.G, colors.AccentColor.B));
        _themeBrushes.Set(SeparatorsButton.Resources, "ToggleButtonForegroundChecked", text.Color);
        _themeBrushes.Set(SeparatorsButton.Resources, "ToggleButtonForegroundCheckedPointerOver", text.Color);
        MinimizeButton.Foreground = text;
        _themeBrushes.Set(MinimizeButton.Resources, "ButtonBackgroundPointerOver", ColorHelper.FromArgb(24, text.Color.R, text.Color.G, text.Color.B));
        _themeBrushes.Set(MinimizeButton.Resources, "ButtonBackgroundPressed", ColorHelper.FromArgb(42, text.Color.R, text.Color.G, text.Color.B));
        _themeBrushes.Set(MinimizeButton.Resources, "ButtonForegroundPointerOver", text.Color);
        _themeBrushes.Set(MinimizeButton.Resources, "ButtonForegroundPressed", text.Color);
        CloseButton.Foreground = text;
        TaskList.Foreground = text;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
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
        PortableNotePlacement placement = _placementRecovery.CaptureForSave(_document.Placement);
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
        if (_restoringPlacement || (!args.DidPositionChange && !args.DidSizeChange) ||
            sender.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored }) return;
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
        PortableNotePlacement? placement = _placementRecovery.CaptureWindowChange(new PortableNotePlacement
        {
            MonitorDevice = display.Device,
            XDip = (bounds.Left - display.Work.Left) / display.Scale,
            YDip = (bounds.Top - display.Work.Top) / display.Scale,
            WidthDip = bounds.Width / display.Scale,
            HeightDip = bounds.Height / display.Scale
        }, sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored });
        if (placement is null) return;
        _document.Placement = placement;
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
        _uiSettings.TextScaleFactorChanged -= TextScaleFactorChanged;
        _saveTimer.Stop();
        _completionTimer.Stop();
        _chrome?.Dispose();
        _chrome = null;
        _saveGate.Dispose();
    }

    private void ShowError(string message, bool isSaveError = false)
    {
        _saveErrorVisible = isSaveError;
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
        private bool _showSeparators;
        private double _singleLineHeight;

        internal TodoRow(PortableTodoTask task, double fontSize, bool showSeparators = false)
        {
            Task = task;
            _fontSize = fontSize;
            _showSeparators = showSeparators;
            _editText = task.Text;
            RefreshVisual(DateTimeOffset.UtcNow);
        }

        internal PortableTodoTask Task { get; }
        public string Text => Task.Text;
        public bool Done => Task.Done;
        public TextDecorations TextDecorations => Done
            ? Windows.UI.Text.TextDecorations.Strikethrough
            : Windows.UI.Text.TextDecorations.None;
        public string EditLabel => AppStrings.Get("TodoEdit");
        public string DeleteLabel => AppStrings.Get("TodoDelete");
        public Visibility TextVisibility => _isEditing ? Visibility.Collapsed : Visibility.Visible;
        public Visibility EditorVisibility => _isEditing ? Visibility.Visible : Visibility.Collapsed;
        public bool IsEditing => _isEditing;
        public double CheckBoxSize => _fontSize * .8;
        public double SingleLineHeight
        {
            get => _singleLineHeight;
            set
            {
                if (_singleLineHeight == value) return;
                _singleLineHeight = value;
                OnPropertyChanged();
            }
        }
        public Thickness SeparatorThickness => new(0, 0, 0, _showSeparators ? 1 : 0);

        internal void SetSeparators(bool enabled)
        {
            if (_showSeparators == enabled) return;
            _showSeparators = enabled;
            OnPropertyChanged(nameof(SeparatorThickness));
        }

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
            OnPropertyChanged(nameof(FontSize));
            OnPropertyChanged(nameof(EditText));
            OnPropertyChanged(nameof(TextVisibility));
            OnPropertyChanged(nameof(EditorVisibility));
        }

        internal void CancelEdit()
        {
            _editText = Task.Text;
            EndEdit();
        }

        internal bool CommitEdit(string currentText)
        {
            if (!_isEditing) return false;
            bool changed = TodoRules.UpdateText(Task, currentText);
            // EndEdit clears the editing flag before notifications can trigger LostFocus.
            EndEdit();
            return changed;
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
        }

        internal void ApplyLanguage()
        {
            OnPropertyChanged(nameof(EditLabel));
            OnPropertyChanged(nameof(DeleteLabel));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
