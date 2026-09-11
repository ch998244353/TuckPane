using System.Xml.Linq;
using System.Runtime.InteropServices;
using TuckPane.Core;

internal static class SettingsAppearanceChecks
{
    internal static async Task RunAsync(string area)
    {
        if (area is not ("all" or "appearance" or "structure" or "notifications" or "feedback" or "startup"))
            throw new ArgumentException($"Unknown settings appearance area: {area}.");
        if (area is "all" or "appearance") CheckAppearance();
        if (area is "all" or "structure") CheckStructure();
        if (area is "all" or "notifications") CheckNotifications();
        if (area is "all" or "feedback") await CheckFeedbackAsync();
        if (area is "all" or "startup") CheckStartup();
        Console.WriteLine($"PASS --settings-appearance {area}: selected source/XAML and production logic contracts. No windows, input automation or old gates; visual quality and actual controls require user acceptance.");
    }

    private static void CheckStartup()
    {
        var unsupported = new COMException("Element not found", unchecked((int)0x80070490));
        int unsubscribeCalls = 0;
        Exception? logged = null;
        IDisposable? failed = OptionalEventSubscription.TryCreate(
            () => throw unsupported, () => unsubscribeCalls++, error => logged = error);
        bool remainingInitializationReached = true;
        failed?.Dispose();
        Require(remainingInitializationReached && failed is null && ReferenceEquals(logged, unsupported) && unsubscribeCalls == 0,
            "Unsupported WinRT event subscription must log, return null and let initialization continue without later unsubscription.");

        int subscribeCalls = 0;
        IDisposable? subscribed = OptionalEventSubscription.TryCreate(
            () => subscribeCalls++, () => unsubscribeCalls++, error => throw new InvalidOperationException("Successful subscription must not log an error.", error));
        Require(subscribed is not null && subscribeCalls == 1, "Supported event subscription must return a cleanup handle.");
        subscribed!.Dispose();
        subscribed.Dispose();
        Require(unsubscribeCalls == 1, "Repeated window cleanup must unsubscribe a successful optional event exactly once.");
        logged = null;
        IDisposable? failingCleanup = OptionalEventSubscription.TryCreate(
            () => { }, () => throw unsupported, error => logged = error);
        failingCleanup!.Dispose();
        Require(ReferenceEquals(logged, unsupported), "An unsupported WinRT unsubscription must log without interrupting window cleanup.");

        string console = Source("ConsoleWindow.xaml.cs");
        string constructor = Method(console, "public ConsoleWindow(AppHost host)");
        Require(constructor.Contains("OptionalEventSubscription.TryCreate(", StringComparison.Ordinal) &&
                constructor.Contains("HighContrastChanged += SettingsHighContrastChanged", StringComparison.Ordinal) &&
                constructor.Contains("HighContrastChanged -= SettingsHighContrastChanged", StringComparison.Ordinal),
            "The real settings constructor must protect both WinRT event registration and cleanup through the optional subscription adapter.");
        string activated = Method(console, "private void ConsoleWindow_Activated(object sender, WindowActivatedEventArgs args)");
        string refresh = Method(Source("ConsoleWindow.Appearance.cs"), "private void RefreshSettingsAccessibility()");
        Require(activated.Contains("RefreshSettingsAccessibility()", StringComparison.Ordinal) &&
                refresh.Contains("_settingsHighContrast == _accessibilitySettings.HighContrast", StringComparison.Ordinal) &&
                refresh.Contains("ApplyTheme()", StringComparison.Ordinal) &&
                Method(console, "private void ApplyConsoleSurfacePalette()").Contains("_settingsHighContrast = highContrast", StringComparison.Ordinal) &&
                console.Contains("_accessibilitySubscription?.Dispose()", StringComparison.Ordinal),
            "Activating settings must refresh changed high-contrast state even when event registration is unavailable.");
    }

    private static void CheckAppearance()
    {
        XDocument settings = XDocument.Parse(Source("ConsoleWindow.xaml"));
        string console = Source("ConsoleWindow.xaml.cs");
        Require(!settings.Descendants().Any(e => Name(e) == "SettingsThemeTargetButton") &&
                !console.Contains("SettingsThemeTargetButton", StringComparison.Ordinal),
            "Settings theme selection must have no remaining UI or event wiring.");
        foreach (string target in new[] { "Organizer", "Station", "Dock" })
            Require((string?)Named(settings, target + "ThemeTargetButton").Attribute("Click") == "ThemeTargetButton_Click",
                $"The existing {target} theme entry must remain connected.");
        string applyTheme = Method(console, "public void ApplyTheme()");
        Require(!applyTheme.Contains("GetTheme(", StringComparison.Ordinal) &&
                !applyTheme.Contains("ThemeTarget.Settings", StringComparison.Ordinal),
            "The settings appearance must not depend on persisted theme choices.");
        Require((string?)Named(settings, "ConsoleRoot").Attribute("RequestedTheme") == "Light" ||
                console.Contains("RequestedTheme = ElementTheme.Light", StringComparison.Ordinal),
            "Settings must request a stable light appearance regardless of system dark mode.");
        string palette = Method(console, "private void ApplyConsoleSurfacePalette()");
        Require(!palette.Contains("GlobalSettings", StringComparison.Ordinal) &&
                palette.Contains("_accessibilitySettings.HighContrast", StringComparison.Ordinal) &&
                palette.Contains("_uiSettings.UIElementColor", StringComparison.Ordinal) &&
                applyTheme.Contains("SolidColorMode: true, SolidOpacity: 1", StringComparison.Ordinal),
            "Settings must use an opaque fixed surface while retaining system high-contrast colors.");
    }

    private static void CheckStructure()
    {
        XDocument settings = XDocument.Parse(Source("ConsoleWindow.xaml"));
        Require(!settings.Descendants().Any(e =>
                ((string?)e.Attribute("Tag"))?.EndsWith("Description", StringComparison.Ordinal) == true ||
                Name(e) == "PerformanceProfileDescription"),
            "Setting descriptions and their obsolete dynamic placeholder must be removed.");
        // Representative controls cover navigation, switches, selectors, sliders and management.
        foreach (var (name, eventName, handler) in new[]
        {
            ("RootNavigation", "SelectionChanged", "RootNavigation_SelectionChanged"),
            ("StartupToggle", "Toggled", "StartupToggle_Toggled"),
            ("LanguageCombo", "SelectionChanged", "LanguageCombo_SelectionChanged"),
            ("HoverExpandDelaySlider", "ValueChanged", "HoverDelaySlider_ValueChanged"),
            ("AddPlacementModeCombo", "SelectionChanged", "AddControl_Changed")
        })
            Require((string?)Named(settings, name).Attribute(eventName) == handler,
                $"Restyling must retain the existing {name} interaction binding.");
        foreach (string name in new[] { "ThemeTransparencyValue", "HoverExpandDelayValue", "MissingStorageInfo" })
            _ = Named(settings, name);
        foreach (string page in new[] { "SystemPage", "DisplayPage", "MenuPage", "InteractionPage", "ThemePage" })
            Require(Named(settings, page).Descendants().Any(e => (string?)e.Attribute("Style") == "{StaticResource SettingsGroup}"),
                $"The {page} controls must participate in the shared settings group layout.");
        string preferences = Source("ConsoleWindow.OrganizerPreferences.cs");
        Require(preferences.Contains("toggle.Toggled += OrganizerMenuToggle_Toggled", StringComparison.Ordinal) &&
                preferences.Contains("_host.SetOrganizerMenuVisibilityAsync(key, toggle.IsOn)", StringComparison.Ordinal) &&
                preferences.Contains("CreateMenuSettingRow(toggle, AppStrings.Get(key))", StringComparison.Ordinal),
            "Dynamically styled menu choices must retain their event and persistence chain.");
    }

    private static void CheckNotifications()
    {
        string tray = Source("Services/TrayIconService.cs");
        Require(!tray.Contains("ShowNotification(", StringComparison.Ordinal) &&
                !tray.Contains("NativeMethods.NIF_INFO", StringComparison.Ordinal),
            "The tray service must no longer emit system balloon notifications.");
        foreach (string token in new[] { "NativeMethods.NIM_ADD", "TrayCommand.OpenConsole", "TrayCommand.Exit" })
            Require(tray.Contains(token, StringComparison.Ordinal), "Notification removal must preserve tray lifecycle and commands.");
        string console = Source("ConsoleWindow.xaml.cs");
        Require(!console.Contains("ShowTransparencyNotice(", StringComparison.Ordinal),
            "Transparency fallback must not display a routine information notice.");
        foreach (string prefix in new[] { "Folder", "Desktop" })
        {
            string controls = Method(prefix == "Folder" ? console : Source("ConsoleWindow.DesktopMenu.cs"),
                $"private void Update{prefix}MenuControls(string? error = null)");
            Require(!controls.Contains($"AppStrings.Get(\"{prefix}MenuEnabled\")", StringComparison.Ordinal) &&
                    !controls.Contains($"AppStrings.Get(\"{prefix}MenuDisabled\")", StringComparison.Ordinal) &&
                    controls.Contains($"AppStrings.Get(\"{prefix}MenuBroken\")", StringComparison.Ordinal) &&
                    controls.Contains($"{prefix}MenuStatusText.Visibility = string.IsNullOrEmpty", StringComparison.Ordinal),
                "Shell menu rows must hide ordinary status without hiding repairable failures.");
        }
    }

    private static async Task CheckFeedbackAsync()
    {
        XDocument settings = XDocument.Parse(Source("ConsoleWindow.xaml"));
        Require((string?)Named(settings, "MissingStorageInfo").Attribute("Severity") == "Warning",
            "Missing storage must retain its actionable warning.");
        foreach (string prefix in new[] { "Folder", "Desktop" })
            Require((string?)Named(settings, prefix + "MenuRepairButton").Attribute("Click") == prefix + "MenuRepairButton_Click",
                "Broken shell integration must retain its repair action.");
        string console = Source("ConsoleWindow.xaml.cs");
        string settingsError = Method(console, "private void ShowError(string title, string message)");
        Require(settingsError.Contains("title", StringComparison.Ordinal) && settingsError.Contains("message", StringComparison.Ordinal) &&
                (settingsError.Contains("ReportOperationError(", StringComparison.Ordinal) ||
                 settingsError.Contains("ConsoleInfoBar.IsOpen = true", StringComparison.Ordinal)),
            "Settings errors must retain their title, message and visible feedback path.");
        string feedback = Source("AppHost.Feedback.cs");
        string status = Method(feedback, "public void LogStatus(string title, string message)");
        Require(status.Contains("AppLogger.Info(", StringComparison.Ordinal) &&
                !status.Contains("OwnedDialogWindow", StringComparison.Ordinal) && !status.Contains("ReportOperationError", StringComparison.Ordinal),
            "Routine status must only log, without raising a dialog.");
        string report = Method(feedback, "public void ReportOperationError(");
        string display = Method(feedback, "private Task ShowOperationErrorAsync(");
        Require(report.Contains("LogStatus(title, message)", StringComparison.Ordinal) &&
                report.Contains("ObserveOperationErrorAsync(title, message", StringComparison.Ordinal) &&
                display.Contains("_dispatcher.TryEnqueue", StringComparison.Ordinal) &&
                display.Contains("_operationFeedbackQueue.ShowAsync", StringComparison.Ordinal) &&
                display.Contains("OwnedDialogWindow.ShowMessageAsync", StringComparison.Ordinal) &&
                display.Contains("title, message", StringComparison.Ordinal) &&
                !display.Contains("OpenConsole(", StringComparison.Ordinal),
            "Actionable errors must preserve their content and reach a serialized application dialog without opening Settings.");
        string message = Method(Source("MainWindow.xaml.cs"), "private void ShowMessage(string message, InfoBarSeverity severity)");
        Require(message.Contains("severity is InfoBarSeverity.Warning or InfoBarSeverity.Error", StringComparison.Ordinal) &&
                message.Contains("_host.ReportOperationError(\"TuckPane\", message", StringComparison.Ordinal) &&
                message.Contains("_host.LogStatus(\"TuckPane\", message)", StringComparison.Ordinal),
            "Main-window feedback must route warnings/errors separately from ordinary status.");

        var queue = new OperationFeedbackQueue();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool secondEntered = false;
        Task first = queue.ShowAsync(() => release.Task);
        Task second = queue.ShowAsync(() => { secondEntered = true; return Task.CompletedTask; });
        Require(!secondEntered && !second.IsCompleted, "A second error must wait until the first dialog finishes.");
        release.SetResult(true);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Require(secondEntered, "Queued error feedback must resume after the previous dialog closes.");
        var expectedFailure = new InvalidOperationException("simulated dialog failure");
        try
        {
            await queue.ShowAsync(() => Task.FromException(expectedFailure));
            throw new InvalidOperationException("Dialog failure must propagate to the observing adapter.");
        }
        catch (InvalidOperationException error) when (ReferenceEquals(error, expectedFailure)) { }
        bool recovered = false;
        await queue.ShowAsync(() => { recovered = true; return Task.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(5));
        Require(recovered, "Failed dialog feedback must release the queue for later errors.");
    }

    private static string SourceRoot() => Path.Combine(Environment.CurrentDirectory, "src", "TuckPane");
    private static string Source(string relative) => File.ReadAllText(Path.Combine(SourceRoot(), relative));
    private static string? Name(XElement element) => (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"));
    private static XElement Named(XDocument document, string name) => document.Descendants().Single(e => Name(e) == name);
    private static string Method(string source, string signature)
    {
        int signatureStart = source.IndexOf(signature, StringComparison.Ordinal);
        Require(signatureStart >= 0, $"Expected feedback/appearance adapter: {signature}");
        int start = source.IndexOf('{', signatureStart);
        int expressionStart = source.IndexOf("=>", signatureStart, StringComparison.Ordinal);
        if (expressionStart >= 0 && (start < 0 || expressionStart < start))
            return source[(expressionStart + 2)..source.IndexOf(';', expressionStart)];
        Require(start >= 0, $"Expected method body: {signature}");
        int depth = 1;
        for (int end = start + 1; end < source.Length; end++)
        {
            if (source[end] == '{') depth++;
            if (source[end] == '}' && --depth == 0) return source[(start + 1)..end];
        }
        throw new InvalidOperationException($"Unterminated method body: {signature}");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
