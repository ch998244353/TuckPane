using System.Xml.Linq;
using System.Runtime.InteropServices;
using TuckPane;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class FontFileMenu422Checks
{
    internal static async Task RunAsync(string area)
    {
        if (area == "todo") CheckTodo();
        else if (area == "menu")
        {
            CheckCommandsAndTheme();
            CheckOptionalThemeEventsAndErrors();
            CheckRenameRules();
            await CheckAsyncLifecycle();
            await CheckShellInvocation();
            CheckModernMenuSource();
        }
        else throw new ArgumentException("Expected todo or menu.");
        Console.WriteLine($"PASS font-file-menu-422 {area}: focused non-GUI logic/source contracts. Visual and real Shell acceptance remain user tests.");
    }

    private static void CheckTodo()
    {
        foreach (double size in new[] { 14d, 24d })
        {
            var row = new TodoWindow.TodoRow(new PortableTodoTask { Text = "字号" }, size);
            var observed = new List<double>();
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(row.FontSize)) observed.Add(row.FontSize); };
            row.BeginEdit();
            Require(row.IsEditing && row.FontSize == size, "Entering editing changes the document font size.");
            row.FontSize = size + 2;
            Require(row.IsEditing && row.FontSize == size + 2 && observed.SequenceEqual(new[] { size, size + 2 }), "Changing size during editing loses the current document size or notification.");
        }

        XDocument xaml = XDocument.Load(SourcePath("TodoWindow.xaml"));
        XElement text = xaml.Descendants().Single(e => (string?)e.Attribute("DoubleTapped") == "TaskText_DoubleTapped");
        XElement editor = xaml.Descendants().Single(e => (string?)e.Attribute("KeyDown") == "TaskEditor_KeyDown");
        Require(editor.Name.LocalName == "TaskEditorTextBox" && text.Parent == editor.Parent, "The editor must synchronize its template text host.");
        foreach (string property in new[] { "FontSize", "FontFamily", "FontWeight", "FontStretch", "CharacterSpacing", "IsTextScaleFactorEnabled" })
            Require(text.Attribute(property) is { } declaration && declaration.Value == (string?)editor.Attribute(property), $"Display/editor typography differs: {property}.");
        Require((string?)editor.Attribute("FontSize") == "{Binding FontSize}" && (string?)editor.Attribute("IsTextScaleFactorEnabled") == "True", "Editor does not follow document size/system text scaling.");
        Require((string?)editor.Parent?.Attribute("Loaded") == "TaskRow_Loaded" && (string?)editor.Parent?.Attribute("DataContextChanged") == "TaskRow_DataContextChanged", "Realized/recycled row synchronization hooks are missing.");

        string control = Read("Controls/TaskEditorTextBox.cs");
        foreach (string hook in new[] { "override void OnApplyTemplate()", "GettingFocus +=", "GotFocus +=", "Loaded +=", "RegisterPropertyChangedCallback" })
            Require(control.Contains(hook, StringComparison.Ordinal), $"Typography lifecycle hook is missing: {hook}.");
        foreach (string property in new[] { "FontSize", "FontFamily", "FontWeight", "FontStyle", "FontStretch", "CharacterSpacing", "IsTextScaleFactorEnabled" })
            Require(control.Contains($"Bind(content, {property}Property") && control.Contains($"Bind(presenter, ContentPresenter.{property}Property"), $"Template host/presenter typography is missing {property}.");
        Require(control.Contains("request != _typographyRequest") && control.Contains("!ReferenceEquals(DataContext, context)") && control.Contains("!ReferenceEquals(Tag, tag)"), "Deferred template synchronization may target a reused editor.");

        string source = Read("TodoWindow.xaml.cs");
        string begin = Between(source, "private void BeginTaskEdit", "private void TaskRow_Loaded");
        Require(begin.Contains("request != _taskEditRequest") && begin.Contains("!row.IsEditing") && begin.Contains("ReferenceEquals(editor.DataContext, row)"), "Deferred focus may target a stale editing request or recycled row.");
        int focus = begin.IndexOf("editor.Focus(", StringComparison.Ordinal);
        int template = begin.IndexOf("editor.ApplyTemplate()", StringComparison.Ordinal);
        Require(template >= 0 && template < focus && begin.Contains("SynchronizeTemplateTypography()") && begin.Contains("SelectAll()"), "Template synchronization/focus/selection contract is incomplete.");
        string afterFocus = begin[focus..];
        Require(afterFocus.Contains("DispatcherQueue.TryEnqueue") && afterFocus.Contains("request != _taskEditRequest") && afterFocus.Contains("ReferenceEquals(editor.DataContext, row)") && afterFocus.Contains("ReferenceEquals(editor.Tag, row)") && afterFocus.Contains("focusedEditor.SynchronizeTemplateTypography()"), "Post-focus selection must synchronize only the current edit request and row.");
        string sync = Between(source, "private static void SyncTaskTypography", "private void TaskEditor_KeyDown");
        Require(sync.Contains("Source = row") && sync.Contains("nameof(TodoRow.FontSize)") && sync.Contains("BindingMode.OneWay"), "Reused editor font binding must target its current row.");
        Console.WriteLine("PASS todo: 14/24 size models and template/focus/recycling source contracts (static typography; no visual claim).");
    }

    internal static void CheckModernMenuSource()
    {
        string service = Read("Services/ShellContextMenuService.cs");
        Require(service.Contains("ShellMenuSession.OpenAsync") && service.Contains("ShowClassicAsync") && !service.Contains("CommandBarFlyout") &&
                !service.Contains("File.Exists(") && !service.Contains("Directory.Exists("),
            "File menu must open an asynchronous Shell session and show the native classic menu directly.");
        string native = Read("Services/ShellMenuSession.cs");
        Require(native.Contains("TrackPopupMenuEx") && native.Contains("popup.Position.X") && native.Contains("popup.Position.Y") &&
                native.Contains("CreatePopupParameters(exclusion)") && native.Contains("_window, in parameters)") &&
                !native.Contains("ReadMenu") && !native.Contains("MeasureClassic"),
            "Native menu must consume the captured position without projecting or measuring menu entries.");
        string operation = Read("Services/FileContextMenuOperation.cs");
        Require(operation.Contains("await session.ShowAsync(") && !operation.Contains("session.Show()"), "Operation must await the asynchronous menu session.");
        string route = Read("MainWindow.FileContextMenu.cs");
        int capture = route.IndexOf("GetFileMenuPosition(", StringComparison.Ordinal);
        int run = route.IndexOf("FileContextMenuOperation.RunAsync", StringComparison.Ordinal);
        Require(capture >= 0 && run > capture, "File menu must capture its screen position before entering the asynchronous operation.");
        Require(route.Contains("await BeginFileRenameAsync(host, item)") && route.Contains("!selection.IsFallback && !IsDocumentItem(item)"), "Rename must enter the editor and ordinary file commands must reach Shell invocation.");
        Require(route.Contains("FileContextMenuOperation.RunAsync") && route.Contains("FlushPortableWindowAsync") && route.Contains("cancellationToken:"), "File route lacks asynchronous protection, document preparation or cancellation.");
        Require(route.Contains("ClosePortableWindowWithoutSave"), "Opaque document commands must detach the saved editor before Shell invocation.");
        Require(route.Contains("FileMenuCommands.ErrorDetails(ex)"), "File menu error reporting must retain an actionable detail when COM provides no message.");
        string rename = Read("MainWindow.FileRename.cs");
        Require(rename.Contains("ItemNameEditor_LostFocus") && rename.Contains("VirtualKey.Escape") && rename.Contains("Saving: false") && rename.Contains("CancelFileRenameForHost"), "Inline editing needs commit/cancel, duplicate-save and recycling protection.");
        string window = Read("MainWindow.xaml.cs");
        string target = Between(window, "private string? TryGetRenameTarget", "private async Task RenameFileItemAsync");
        Require(target.Contains("File.Exists(targetPath) || Directory.Exists(targetPath)"), "Rename must reject both file and directory name collisions.");
        string commit = Between(window, "private async Task RenameFileItemAsync", "private async Task DeleteFileItemAsync");
        Require(commit.Contains("ShellFileOperations.RenameAsync") && commit.Contains("_definition.ItemOrder[orderIndex] = newRelativeName") && commit.Contains("RenameExternalNoteAsync") && commit.Contains("RenameExternalTodoAsync"), "Rename must use Shell, preserve ordering and route document path updates through the host.");
        Console.WriteLine("PASS menu source: native Shell/captured-position/inline-edit/document routes (static; no visual claim).");
    }

    private static void CheckOptionalThemeEventsAndErrors()
    {
        var unsupported = new COMException(string.Empty, unchecked((int)0x80070490));
        var trace = new List<string>();
        using (var failed = OptionalEventSubscription.TryCreate(() => throw unsupported,
            () => trace.Add("unsubscribe"), error =>
            {
                Require(ReferenceEquals(error, unsupported), "Optional event failure must preserve the original platform error.");
                trace.Add("logged");
            }))
        {
            Require(failed is null, "Unavailable WinRT event must return no subscription instead of blocking menu initialization.");
            trace.Add("continue initialization");
        }
        Require(trace.SequenceEqual(new[] { "logged", "continue initialization" }), "Failed optional event must allow construction to continue and must not attempt cleanup.");

        int cleanupCalls = 0;
        var supported = OptionalEventSubscription.TryCreate(() => { }, () => cleanupCalls++,
            error => throw new InvalidOperationException("Supported event unexpectedly failed.", error));
        Require(supported is not null, "Supported theme event must retain its cleanup handle.");
        supported!.Dispose(); supported.Dispose();
        Require(cleanupCalls == 1, "Theme event cleanup must remain idempotent.");

        string detail = FileMenuCommands.ErrorDetails(unsupported);
        Require(!string.IsNullOrWhiteSpace(detail) && detail.Contains("0x80070490", StringComparison.OrdinalIgnoreCase) &&
                detail.Contains(nameof(COMException), StringComparison.Ordinal),
            "Empty COM error messages must report the exception type and HRESULT.");
        Require(FileMenuCommands.ErrorDetails(new IOException("Readable cause")).Contains("Readable cause", StringComparison.Ordinal),
            "Error formatting must preserve an available original message.");
        Console.WriteLine("PASS optional theme events: unavailable notification permits initialization, cleanup occurs once and empty COM errors retain HRESULT (fake callbacks; no UI).");
    }

    private static void CheckCommandsAndTheme()
    {
        ShellMenuEntry separator = new(0, "", null, Separator: true);
        ShellMenuEntry[] entries = [separator, new(81, "Open", "open"), new(12, "Copy", "COPY"),
            new(30, "Paste", "paste", Enabled: false), separator, separator,
            new(96, "Extension", null, Checked: true), new(91, "Custom drawing", null, Unsupported: true),
            new(44, "Send to", null, Submenu: 1234), new(22, "Cut", "cut"), separator];
        var (primary, secondary) = ShellMenuModel.Partition(entries);
        Require(primary.Select(e => e.Id).SequenceEqual(new uint[] { 22, 12, 30 }) && !primary[^1].Enabled,
            "Primary commands must preserve Shell IDs and disabled state in standard verb order.");
        Require(secondary.Select(e => e.Id).SequenceEqual(new uint[] { 81, 0, 96, 44 }) && secondary[2].Checked && secondary[^1].Submenu == 1234,
            "Secondary commands must retain Shell order/state/submenus, compact separators and omit unsupported drawing.");
        var empty = ShellMenuModel.Partition([separator, new(7, "Unsupported", null, Unsupported: true), separator]);
        Require(empty.Primary.Count == 0 && empty.Secondary.Count == 0, "Unsupported-only menus must not retain orphan separators.");
        Require(ShellMenuModel.DisplayLabel("&Open && inspect\tCtrl+O") == "Open & inspect", "Shell access keys must not corrupt literal ampersands.");
        Require(FileMenuCommands.IsDark(0, 0, 0) && FileMenuCommands.IsDark(127, 127, 127) &&
                !FileMenuCommands.IsDark(128, 128, 128) && !FileMenuCommands.IsDark(255, 255, 255) &&
                !FileMenuCommands.IsDark(0, 255, 0) && FileMenuCommands.IsDark(255, 0, 255), "System background luminance does not select the expected dark/light theme.");
    }

    private static void CheckRenameRules()
    {
        string path = Path.Combine(Path.GetTempPath(), "report.final.txt");
        Require(FileRenameRules.InitialName(path, false) == "report.final.txt" && FileRenameRules.InitialName(path, true) == "report.final", "Ordinary filenames include the extension; portable document editors hide only the fixed extension.");
        Require(FileRenameRules.SelectionLength("report.final.txt", false, false) == 12 &&
                FileRenameRules.SelectionLength("folder.name", true, false) == 11 &&
                FileRenameRules.SelectionLength("note.title", false, true) == 10 &&
                FileRenameRules.SelectionLength(".gitignore", false, false) == 10,
            "Initial rename selection must preserve ordinary extensions and select full folder/document names.");
        foreach (string invalid in new[] { "", " ", "a/b", "trailing.", "trailing ", "NUL.txt", "com1", "LPT².log" })
            Require(FileRenameRules.Validate(invalid, "", false) is not null, $"Invalid Windows filename accepted: {invalid}.");
        Require(FileRenameRules.Validate("更名.md", "", false) is null && FileRenameRules.Validate("笔记", ".tucknote", true) is null &&
                FileRenameRules.Validate(new string('a', 121), ".tucknote", true) is not null &&
                FileRenameRules.Validate(new string('a', 256), "", false) is not null,
            "Valid extension changes or filename/document length limits are incorrect.");
    }

    private static async Task CheckAsyncLifecycle()
    {
        await CheckPendingOperation();
        await CheckCancellation();
        foreach (string stage in new[] { "create", "show", "handle" }) await CheckFailure(stage);
        await CheckDismissal();
        Console.WriteLine("PASS menu logic: dynamic command projection/name rules; pending commands reject duplicates; cancellation, dismissal and failures release session/protection in order.");
    }

    private static async Task CheckPendingOperation()
    {
        var trace = new List<string>();
        var gate = new FileContextMenuGate();
        var selection = new TaskCompletionSource<FileMenuSelection?>();
        var handled = new TaskCompletionSource<bool>();
        var handlerEntered = new TaskCompletionSource();
        var session = new FakeSession(trace, token => selection.Task.WaitAsync(token));
        Task first = Run(trace, gate, session, verb =>
        {
            Require(verb == "copy", "Async selection loses the canonical command.");
            trace.Add("handle");
            handlerEntered.SetResult();
            return handled.Task;
        });
        Require(!first.IsCompleted && gate.IsActive && string.Join(',', trace) == "enter,prepare,create,show", "Menu show must stay asynchronous while holding protection.");
        await RejectDuplicate(gate);
        selection.SetResult(new FileMenuSelection(3, "copy"));
        await handlerEntered.Task;
        Require(!first.IsCompleted && gate.IsActive, "Command execution released protection too soon.");
        await RejectDuplicate(gate);
        handled.SetResult(true);
        await first;
        Require(string.Join(',', trace) == "enter,prepare,create,show,handle,dispose,refresh,leave" && !gate.IsActive, "Async command terminal release order is incorrect.");
        IDisposable? next = gate.TryEnter();
        Require(next is not null && gate.IsActive, "Released gate must accept the next user request.");
        next!.Dispose(); next.Dispose();
        Require(!gate.IsActive, "Gate lease disposal must be idempotent.");
    }

    private static Task RejectDuplicate(FileContextMenuGate gate) => FileContextMenuOperation.RunAsync(
        enter: () => throw new InvalidOperationException("Duplicate request entered protection."),
        prepare: () => throw new InvalidOperationException("Duplicate request prepared documents."),
        create: () => throw new InvalidOperationException("Duplicate request created a menu."),
        handle: _ => throw new InvalidOperationException("Duplicate request dispatched a command."),
        refresh: () => throw new InvalidOperationException("Duplicate request refreshed files."),
        leave: () => throw new InvalidOperationException("Duplicate request released another request's protection."),
        gate: gate);

    private static async Task CheckCancellation()
    {
        var trace = new List<string>();
        var gate = new FileContextMenuGate();
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<FileMenuSelection?>();
        var session = new FakeSession(trace, token =>
        {
            Require(token == cancellation.Token, "Window cancellation token was not forwarded to menu show.");
            return pending.Task.WaitAsync(token);
        });
        Task run = Run(trace, gate, session, _ => throw new InvalidOperationException("Cancelled menu dispatched a command."), cancellation.Token);
        cancellation.Cancel();
        bool cancelled = false;
        try { await run; } catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled && !gate.IsActive && string.Join(',', trace) == "enter,prepare,create,show,dispose,leave", "Cancelled window/menu must dispose before releasing protection, without refresh.");
    }

    private static async Task CheckFailure(string stage)
    {
        var trace = new List<string>();
        var gate = new FileContextMenuGate();
        var session = new FakeSession(trace, _ => stage == "show" ? Task.FromException<FileMenuSelection?>(new ExpectedFailure()) : Task.FromResult<FileMenuSelection?>(new(1, "open")));
        bool failed = false;
        try
        {
            await FileContextMenuOperation.RunAsync(
                () => trace.Add("enter"), () => { trace.Add("prepare"); return Task.FromResult(true); },
                () => { trace.Add("create"); return stage == "create" ? throw new ExpectedFailure() : session; },
                _ => { trace.Add("handle"); return Task.FromException<bool>(new ExpectedFailure()); },
                () => { trace.Add("refresh"); return Task.CompletedTask; }, () => trace.Add("leave"), gate: gate);
        }
        catch (ExpectedFailure) { failed = true; }
        string expected = stage switch
        {
            "create" => "enter,prepare,create,leave",
            "show" => "enter,prepare,create,show,dispose,leave",
            _ => "enter,prepare,create,show,handle,dispose,leave"
        };
        Require(failed && !gate.IsActive && string.Join(',', trace) == expected, $"{stage} failure must release owned resources exactly once: {string.Join(',', trace)}.");
    }

    private static async Task CheckDismissal()
    {
        var trace = new List<string>();
        var gate = new FileContextMenuGate();
        await Run(trace, gate, new FakeSession(trace, _ => Task.FromResult<FileMenuSelection?>(null)), _ => throw new InvalidOperationException("Dismissed menu dispatched a command."));
        Require(!gate.IsActive && string.Join(',', trace) == "enter,prepare,create,show,dispose,leave", "Dismissal did not release session/protection exactly once.");
    }

    private static async Task CheckShellInvocation()
    {
        foreach (bool fail in new[] { false, true })
        {
            var trace = new List<string>();
            var gate = new FileContextMenuGate();
            var invoked = new TaskCompletionSource();
            var released = new TaskCompletionSource();
            using var cancellation = new CancellationTokenSource();
            var expected = new FileMenuSelection(932, null);
            var session = new FakeSession(trace, _ => Task.FromResult<FileMenuSelection?>(expected), (selection, token) =>
            {
                Require(selection == expected && token == cancellation.Token, "Opaque Shell command ID/token must reach invocation unchanged.");
                invoked.SetResult();
                return released.Task;
            });
            Task run = FileContextMenuOperation.RunAsync(() => trace.Add("enter"), () => Task.FromResult(true), () => session,
                _ => throw new InvalidOperationException("Selection-aware route must take precedence over verb-only handling."),
                () => { trace.Add("refresh"); return Task.CompletedTask; }, () => trace.Add("leave"), cancellation.Token, gate,
                selection => { Require(selection == expected, "Selection-aware handler lost Shell ID."); return Task.FromResult(false); });
            await invoked.Task;
            Require(!run.IsCompleted && gate.IsActive && session.InvocationCount == 1, "InvokeAsync must be awaited while protection is held.");
            await RejectDuplicate(gate);
            if (fail) released.SetException(new ExpectedFailure()); else released.SetResult();
            bool failed = false;
            try { await run; } catch (ExpectedFailure) { failed = true; }
            Require(failed == fail && !gate.IsActive && session.InvocationCount == 1 && string.Join(',', trace) ==
                (fail ? "enter,show,invoke,dispose,leave" : "enter,show,invoke,dispose,refresh,leave"),
                "Shell invocation must execute once and release resources on success or failure.");
        }
        Console.WriteLine("PASS Shell dispatch fake: opaque command IDs, asynchronous single invocation and success/failure cleanup; no native command executed.");
    }

    private static Task Run(List<string> trace, FileContextMenuGate gate, FakeSession session, Func<string?, Task<bool>> handle, CancellationToken cancellationToken = default) =>
        FileContextMenuOperation.RunAsync(() => trace.Add("enter"),
            () => { trace.Add("prepare"); return Task.FromResult(true); },
            () => { trace.Add("create"); return session; }, handle,
            () => { trace.Add("refresh"); return Task.CompletedTask; }, () => trace.Add("leave"), cancellationToken, gate);

    private sealed class FakeSession(List<string> trace, Func<CancellationToken, Task<FileMenuSelection?>> show,
        Func<FileMenuSelection, CancellationToken, Task>? invoke = null) : IFileContextMenuSession
    {
        internal int InvocationCount { get; private set; }
        public FileMenuSelection? Show() => throw new InvalidOperationException("Synchronous Show must not be called.");
        public Task<FileMenuSelection?> ShowAsync(CancellationToken cancellationToken = default) { trace.Add("show"); return show(cancellationToken); }
        public void Invoke(FileMenuSelection selection) => throw new InvalidOperationException("Synchronous Invoke must not be called.");
        public Task InvokeAsync(FileMenuSelection selection, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            trace.Add("invoke");
            return invoke?.Invoke(selection, cancellationToken) ?? throw new InvalidOperationException("Handled command must not be invoked again.");
        }
        public void Dispose() => trace.Add("dispose");
    }
    private sealed class ExpectedFailure : Exception { }

    private static string SourcePath(string file) => Path.Combine(Environment.CurrentDirectory, "src", "TuckPane", file);
    private static string Read(string file) => File.ReadAllText(SourcePath(file));
    private static string Between(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal), last = source.IndexOf(end, first < 0 ? 0 : first, StringComparison.Ordinal);
        Require(first >= 0 && last > first, $"Missing source boundaries: {start} / {end}.");
        return source[first..last];
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
