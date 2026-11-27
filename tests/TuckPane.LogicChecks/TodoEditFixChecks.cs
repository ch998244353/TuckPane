using System.Xml.Linq;
using TuckPane;
using TuckPane.Models;

internal static class TodoEditFixChecks
{
    internal static void RunFocusFont()
    {
        CheckCurrentInputAndRepeatedCommit();
        Console.WriteLine("PASS todo-edit-focus-font: current input and LostFocus-style commit reentry.");
        CheckFontNotifications();
        Console.WriteLine("PASS todo-edit-focus-font: default/enlarged font notifications during editing and reopening.");
        var (text, editor) = CheckTypographyContract();
        Require((string?)text.Attribute("IsTextScaleFactorEnabled") == "True" &&
                (string?)editor.Attribute("IsTextScaleFactorEnabled") == "True",
            "Display and editor must preserve system text scaling.");
        Require(text.Parent == editor.Parent &&
                (string?)editor.Parent?.Attribute("Loaded") == "TaskRow_Loaded" &&
                (string?)editor.Parent?.Attribute("DataContextChanged") == "TaskRow_DataContextChanged",
            "The row must synchronize typography on load and recycling.");
        Console.WriteLine("PASS todo-edit-focus-font: typography declarations and lifecycle hooks. No GUI; visual acceptance pending.");
    }

    private static void CheckFontNotifications()
    {
        foreach (double initialSize in new[] { 14d, 24d })
        {
            var row = new TodoWindow.TodoRow(new PortableTodoTask { Text = "字号同步" }, initialSize);
            var observedSizes = new List<double>();
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TodoWindow.TodoRow.FontSize)) observedSizes.Add(row.FontSize);
            };
            row.BeginEdit();
            row.FontSize = initialSize + 1;
            Require(row.CommitEdit("更新文字") && !row.IsEditing, "Editing did not finish.");
            row.BeginEdit();
            Require(row.FontSize == initialSize + 1 && row.EditText == "更新文字" &&
                    observedSizes.SequenceEqual(new[] { initialSize, initialSize + 1, initialSize + 1 }),
                "Entering, changing font while editing, or reopening lost the current font notification.");
        }
    }

    internal static void Run()
    {
        CheckCurrentInputAndRepeatedCommit();
        Console.WriteLine("PASS todo-edit-fixes: current input wins; notification reentry and repeated commit cannot overwrite it.");
        CheckCancelBlankAndFontSize();
        Console.WriteLine("PASS todo-edit-fixes: cancel, blank input, reopening and enlarged font size.");
        CheckXamlContract();
        Console.WriteLine("PASS todo-edit-fixes: matching font declarations and no undo button or dedicated column. No GUI.");
    }

    private static void CheckCurrentInputAndRepeatedCommit()
    {
        var task = new PortableTodoTask { Text = "原内容" };
        var row = new TodoWindow.TodoRow(task, 14);
        row.BeginEdit();
        bool reentered = false;
        bool reentryChanged = false;
        var notifications = new HashSet<string?>();
        row.PropertyChanged += (_, e) =>
        {
            notifications.Add(e.PropertyName);
            if (reentered) return;
            reentered = true;
            reentryChanged = row.CommitEdit("通知期间的旧输入");
        };

        Require(row.EditText == "原内容", "The draft must remain stale before submitting current editor text.");
        Require(row.CommitEdit("修改后的内容"), "Current editor text did not report a change.");
        Require(reentered && !reentryChanged && !row.IsEditing &&
                task.Text == "修改后的内容" && row.Text == task.Text && row.EditText == task.Text,
            "Commit used stale draft text or allowed notification reentry to overwrite the update.");
        Require(notifications.Contains(nameof(TodoWindow.TodoRow.Text)) &&
                notifications.Contains(nameof(TodoWindow.TodoRow.EditText)),
            "Commit did not notify the displayed text and editor draft.");
        Require(!row.CommitEdit("随后失焦的旧输入") && task.Text == "修改后的内容",
            "Repeated commit overwrote the text or requested another save.");
    }

    private static void CheckCancelBlankAndFontSize()
    {
        var task = new PortableTodoTask { Text = "保留原文" };
        var row = new TodoWindow.TodoRow(task, 14);
        row.FontSize = 24;
        row.BeginEdit();
        row.EditText = "取消的草稿";
        row.CancelEdit();
        Require(!row.CommitEdit("取消后的失焦输入") && !row.IsEditing &&
                task.Text == "保留原文" && row.EditText == task.Text && row.FontSize == 24,
            "Cancel committed a draft or changed the enlarged font size.");
        row.BeginEdit();
        Require(row.IsEditing && row.EditText == task.Text && row.FontSize == 24,
            "Reopening did not restore the current text and font size.");
        Require(!row.CommitEdit(" \t\r\n ") && !row.IsEditing && task.Text == "保留原文" && row.EditText == task.Text,
            "Blank submission erased the original or retained editing state.");
        row.BeginEdit();
        Require(row.CommitEdit("再次修改") && row.FontSize == 24, "Commit changed the enlarged font size.");
        row.BeginEdit();
        Require(row.EditText == "再次修改" && row.FontSize == 24, "Reopening resurrected stale text or changed font size.");
    }

    private static void CheckXamlContract()
    {
        var (text, editor) = CheckTypographyContract();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        Require(!text.Document!.Descendants(presentation + "Button")
                .Any(element => (string?)element.Attribute("Click") == "UndoTask_Click"),
            "The right-side undo button is still present.");
        XElement? grid = editor.Parent;
        Require(grid == text.Parent && grid?.Name == presentation + "Grid" &&
                grid.Element(presentation + "Grid.ColumnDefinitions")?.Elements(presentation + "ColumnDefinition").Count() == 2,
            "The task row retains the dedicated undo column.");
    }

    private static (XElement Text, XElement Editor) CheckTypographyContract()
    {
        XDocument document = XDocument.Load(Path.Combine(Environment.CurrentDirectory, "src", "TuckPane", "TodoWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement text = document.Descendants(presentation + "TextBlock")
            .Single(element => (string?)element.Attribute("DoubleTapped") == "TaskText_DoubleTapped");
        XElement editor = document.Descendants()
            .Single(element => (string?)element.Attribute("KeyDown") == "TaskEditor_KeyDown");
        foreach (string property in new[] { "FontFamily", "FontSize", "FontWeight", "FontStretch", "CharacterSpacing" })
        {
            string? declared = (string?)text.Attribute(property);
            Require(!string.IsNullOrWhiteSpace(declared) && declared == (string?)editor.Attribute(property),
                $"Display and edit font declarations differ: {property}.");
        }
        Require((string?)text.Attribute("FontSize") == "{Binding FontSize}", "Text no longer follows the document font size.");
        return (text, editor);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
